// Client-side wait for a cold-starting API (UI-GEN-7). Framework free so the state machine is unit tested with fake timers.

export type ReadinessPhase = "idle" | "starting" | "timeout";

export interface ReadinessSnapshot {
  phase: ReadinessPhase;
  /** Whole seconds since waiting began. */
  elapsed: number;
}

export const POLL_INTERVAL_MS = 3_000;
export const GIVE_UP_AFTER_MS = 180_000;

type Check = () => Promise<boolean>;

export class ReadinessController {
  private snap: ReadinessSnapshot = { phase: "idle", elapsed: 0 };
  private listeners = new Set<() => void>();
  private waiters: ((ready: boolean) => void)[] = [];
  private poll: ReturnType<typeof setInterval> | undefined;
  private tick: ReturnType<typeof setInterval> | undefined;
  private startedAt = 0;
  private checking = false;
  private prewarmed = false;

  constructor(
    private readonly check: Check,
    private readonly now: () => number = Date.now,
  ) {}

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  getSnapshot = (): ReadinessSnapshot => this.snap;

  private set(next: ReadinessSnapshot) {
    if (next.phase === this.snap.phase && next.elapsed === this.snap.elapsed) return;
    this.snap = next;
    for (const l of this.listeners) l();
  }

  /** Once per page load: asks the BFF whether the API is awake; only when it is not does the notice appear. */
  prewarm(): void {
    if (this.prewarmed || this.snap.phase !== "idle") return;
    this.prewarmed = true;
    void this.safeCheck().then((ready) => {
      if (!ready) void this.waitUntilReady();
    });
  }

  /** Resolves true once the API answers, false after about three minutes. Concurrent callers share one wait. */
  waitUntilReady(): Promise<boolean> {
    return new Promise((resolve) => {
      this.waiters.push(resolve);
      if (this.snap.phase === "starting") return;
      this.begin();
    });
  }

  /** After a timeout: start over. */
  retry(): void {
    if (this.snap.phase === "starting") return;
    this.begin();
  }

  private begin() {
    this.stop();
    this.startedAt = this.now();
    this.set({ phase: "starting", elapsed: 0 });
    this.tick = setInterval(() => {
      const ms = this.now() - this.startedAt;
      if (ms >= GIVE_UP_AFTER_MS) return this.finish(false);
      this.set({ phase: "starting", elapsed: Math.floor(ms / 1000) });
    }, 1000);
    this.poll = setInterval(() => void this.pollOnce(), POLL_INTERVAL_MS);
    void this.pollOnce();
  }

  private async pollOnce() {
    if (this.checking) return;
    this.checking = true;
    const ready = await this.safeCheck();
    this.checking = false;
    if (ready && this.snap.phase === "starting") this.finish(true);
  }

  private async safeCheck(): Promise<boolean> {
    try {
      return await this.check();
    } catch {
      return false;
    }
  }

  private finish(ready: boolean) {
    this.stop();
    this.set({ phase: ready ? "idle" : "timeout", elapsed: ready ? 0 : this.snap.elapsed });
    const waiters = this.waiters;
    this.waiters = [];
    for (const w of waiters) w(ready);
  }

  private stop() {
    clearInterval(this.poll);
    clearInterval(this.tick);
    this.poll = this.tick = undefined;
  }
}

/** Runs `run`; when its result says the API is starting, waits for the API and runs it once more (never more than once). */
export async function withColdStartRetry<R>(
  controller: Pick<ReadinessController, "waitUntilReady">,
  run: () => Promise<R>,
  isStarting: (result: R) => boolean,
): Promise<R> {
  const first = await run();
  if (!isStarting(first)) return first;
  const ready = await controller.waitUntilReady();
  return ready ? run() : first;
}

/** Browser: ask our own BFF. */
export async function fetchReady(): Promise<boolean> {
  const res = await fetch("/api/status", { cache: "no-store", credentials: "same-origin" });
  if (!res.ok) return false;
  const body = (await res.json()) as { ready?: unknown };
  return body.ready === true;
}

let shared: ReadinessController | undefined;
export function getReadiness(): ReadinessController {
  shared ??= new ReadinessController(fetchReady);
  return shared;
}

/** For server actions (`ActionResult`): `formError: "apiStarting"` means the API was asleep; wait, then submit the same data once more. */
export function actionWithColdStartRetry<R extends { ok: boolean; formError?: string }>(
  run: () => Promise<R>,
): Promise<R> {
  return withColdStartRetry(getReadiness(), run, (r) => !r.ok && r.formError === "apiStarting");
}
