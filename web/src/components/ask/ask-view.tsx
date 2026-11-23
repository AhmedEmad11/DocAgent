"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { QuotaBlocked, isBlocked, type Quota } from "@/components/demo/quota-blocked";
import { ProblemAlert } from "@/components/ui/problem-alert";
import { dynamicT } from "@/i18n/dynamic";
import { Link } from "@/i18n/navigation";
import { askReducer, initialAskState, type AskAction, type AskState } from "@/lib/ask/state";
import { askStream, type AskRequestBody } from "@/lib/ask/stream-client";
import { AnswerView } from "./answer-view";

export const MAX_QUESTION = 2000;
export const MAX_SCOPE = 100;

export interface ScopeDocument {
  id: string;
  fileName: string;
  status: string;
}

interface Item {
  id: number;
  question: string;
  documentIds: string[] | undefined;
  state: AskState;
}

type Stream = typeof askStream;

const STAGES = ["searching", "checking", "verifying"] as const;

function Progress({ stage }: { stage: string }) {
  const t = useTranslations("Ask");
  const current = STAGES.indexOf(stage as (typeof STAGES)[number]);
  return (
    <div
      role="group"
      aria-label={t("progress")}
      data-testid="progress"
      data-stage={stage}
      className="space-y-2"
    >
      <p className="text-sm font-medium" data-testid="stage">
        {stage === "starting"
          ? t("stages.starting")
          : t(`stages.${stage as (typeof STAGES)[number]}`)}
      </p>
      <ol className="flex flex-wrap gap-2 text-xs" aria-hidden="true">
        {STAGES.map((s, i) => (
          <li
            key={s}
            className={`rounded-full border px-2.5 py-0.5 ${i <= current ? "border-primary/50 bg-primary/10 text-foreground" : "border-border text-muted-foreground"}`}
          >
            {t(`stages.${s}`)}
          </li>
        ))}
      </ol>
    </div>
  );
}

export function AskView({
  documents,
  initialSelected,
  quota = null,
  stream = askStream,
}: {
  documents: ScopeDocument[];
  initialSelected?: string[];
  /** The demo allowance (null for accounts with their own key). At zero the controls are off (UI-DEMO-2). */
  quota?: Quota | null;
  /** Test seam. */
  stream?: Stream;
}) {
  const router = useRouter();
  const t = useTranslations("Ask");
  const tk = dynamicT(t);
  const ready = documents.filter((d) => d.status === "Ready");
  const [question, setQuestion] = useState("");
  const [emptyError, setEmptyError] = useState(false);
  const [scope, setScope] = useState<"all" | "some">(initialSelected?.length ? "some" : "all");
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set((initialSelected ?? []).filter((id) => ready.some((d) => d.id === id))),
  );
  const [items, setItems] = useState<Item[]>([]);
  const [announcement, setAnnouncement] = useState("");
  const nextId = useRef(1);
  const aborter = useRef<AbortController | null>(null);
  const running = items.some((i) => i.state.phase === "running");
  const blocked = isBlocked(quota);
  const demo = quota !== null;

  useEffect(() => () => aborter.current?.abort(), []);

  const dispatch = useCallback((id: number, action: AskAction) => {
    setItems((all) =>
      all.map((it) => (it.id === id ? { ...it, state: askReducer(it.state, action) } : it)),
    );
  }, []);

  const run = useCallback(
    async (id: number, body: AskRequestBody) => {
      const controller = new AbortController();
      aborter.current = controller;
      dispatch(id, { type: "start" });
      setAnnouncement(t("stages.starting"));
      const end = await stream(
        body,
        (event) => {
          dispatch(id, { type: "event", event });
          if (event.type === "searching") setAnnouncement(t("stages.searching"));
          else if (event.type === "tool_call") setAnnouncement(t("stages.checking"));
          else if (event.type === "verifying") setAnnouncement(t("stages.verifying"));
          else if (event.type === "answer") {
            setAnnouncement(t("answerReady"));
            if (demo) router.refresh(); // the allowance in the banner follows (UI-DEMO-2)
            setQuestion((current) => (current.trim() === body.question ? "" : current));
          }
        },
        controller.signal,
      );
      if (end.kind === "refused") dispatch(id, { type: "refused", problem: end.problem });
      else if (end.kind === "dropped") dispatch(id, { type: "dropped" });
      if (aborter.current === controller) aborter.current = null;
    },
    [dispatch, stream, t, demo, router],
  );

  const ask = (text: string, documentIds: string[] | undefined) => {
    const id = nextId.current++;
    setItems((all) => [{ id, question: text, documentIds, state: initialAskState }, ...all]);
    void run(id, { question: text, ...(documentIds ? { documentIds } : {}) });
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    if (running || blocked) return;
    const text = question.trim();
    if (!text) {
      setEmptyError(true);
      return;
    }
    setEmptyError(false);
    const ids = scope === "some" && selected.size > 0 ? [...selected] : undefined;
    ask(text, ids);
  };

  const cancel = () => {
    aborter.current?.abort();
    const current = items.find((i) => i.state.phase === "running");
    if (current) {
      dispatch(current.id, { type: "cancel" });
      setAnnouncement(t("cancelledAnnounce"));
    }
  };

  const retry = (item: Item) => {
    if (running || blocked) return;
    void run(item.id, {
      question: item.question,
      ...(item.documentIds ? { documentIds: item.documentIds } : {}),
    });
  };

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else if (next.size < MAX_SCOPE) next.add(id);
      return next;
    });

  if (ready.length === 0) {
    return (
      <div
        className="rounded-lg border border-dashed border-border p-6 text-center"
        data-testid="no-ready"
      >
        <p className="font-medium">{t("noReady")}</p>
        <p className="text-sm text-muted-foreground">{t("noReadyHint")}</p>
        <Link
          href="/documents"
          className="mt-2 inline-block text-primary underline underline-offset-4"
        >
          {t("toDocuments")}
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {blocked && quota ? <QuotaBlocked resetsAt={quota.resetsAt} /> : null}
      <form onSubmit={submit} noValidate className="space-y-5" data-testid="ask-form">
        <div className="space-y-1.5">
          <label htmlFor="question" className="text-sm font-medium">
            {t("questionLabel")}
          </label>
          <textarea
            id="question"
            name="question"
            dir="auto"
            rows={4}
            maxLength={MAX_QUESTION}
            value={question}
            readOnly={running}
            onChange={(e) => {
              setQuestion(e.target.value);
              if (emptyError) setEmptyError(false);
            }}
            aria-invalid={emptyError || undefined}
            aria-describedby={`question-count${emptyError ? " question-error" : ""}`}
            className="min-h-24 w-full rounded-md border border-input bg-background p-3 text-start text-base focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
          />
          <div className="flex justify-between gap-2 text-sm">
            <span id="question-error" className="text-destructive">
              {emptyError ? t("errors.empty") : null}
            </span>
            <span id="question-count" className="text-muted-foreground" data-testid="counter">
              {t("counter", { count: question.length, max: MAX_QUESTION })}
            </span>
          </div>
        </div>

        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">{t("scopeLegend")}</legend>
          <label className="flex items-center gap-2">
            <input
              type="radio"
              name="scope"
              checked={scope === "all"}
              onChange={() => setScope("all")}
              disabled={running}
              className="size-4"
            />
            {t("scopeAll", { count: ready.length })}
          </label>
          <label className="flex items-center gap-2">
            <input
              type="radio"
              name="scope"
              checked={scope === "some"}
              onChange={() => setScope("some")}
              disabled={running}
              className="size-4"
            />
            {t("scopeSome")}
          </label>
          {scope === "some" ? (
            <ul
              className="max-h-60 space-y-1 overflow-y-auto rounded-md border border-border p-3"
              data-testid="scope-list"
            >
              {documents.map((d) => {
                const selectable = d.status === "Ready";
                return (
                  <li key={d.id}>
                    <label
                      className={`flex items-center gap-2 ${selectable ? "" : "text-muted-foreground"}`}
                    >
                      <input
                        type="checkbox"
                        className="size-4"
                        checked={selected.has(d.id)}
                        disabled={!selectable || running}
                        onChange={() => toggle(d.id)}
                      />
                      <span dir="auto" className="min-w-0 truncate" title={d.fileName}>
                        {d.fileName}
                      </span>
                      {selectable ? null : (
                        <span className="shrink-0 text-xs">({tk(`status.${d.status}`)})</span>
                      )}
                    </label>
                  </li>
                );
              })}
            </ul>
          ) : null}
          {scope === "some" && selected.size === 0 ? (
            <p className="text-sm text-muted-foreground">{t("scopeNone")}</p>
          ) : null}
        </fieldset>

        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={running || blocked} aria-busy={running}>
            {t("submit")}
          </Button>
          {running ? (
            <Button type="button" variant="outline" onClick={cancel}>
              {t("cancel")}
            </Button>
          ) : null}
        </div>
      </form>

      <p role="status" aria-live="polite" className="sr-only" data-testid="live-region">
        {announcement}
      </p>

      <section aria-label={t("conversation")} className="space-y-6" data-testid="conversation">
        {items.map((item) => (
          <article
            key={item.id}
            className="space-y-3 rounded-lg border border-border p-4"
            data-testid="qa-item"
            data-phase={item.state.phase}
          >
            <div>
              <h2 className="text-sm font-medium text-muted-foreground">{t("youAsked")}</h2>
              <p dir="auto" className="whitespace-pre-wrap text-start" data-testid="qa-question">
                {item.question}
              </p>
            </div>
            {item.state.phase === "running" ? <Progress stage={item.state.stage} /> : null}
            {item.state.phase === "answered" ? (
              <AnswerView answer={item.state.answer} idPrefix={`a${item.id}-`} />
            ) : null}
            {item.state.phase === "failed" ? (
              <ProblemAlert
                problem={item.state.problem}
                ctx={{ area: "ask" }}
                resetsAt={quota?.resetsAt}
                onRetry={running ? undefined : () => retry(item)}
              />
            ) : null}
            {item.state.phase === "cancelled" ? (
              <div className="space-y-2" data-testid="cancelled">
                <p className="text-sm">{t("cancelled")}</p>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => retry(item)}
                  disabled={running}
                >
                  {t("retry")}
                </Button>
              </div>
            ) : null}
          </article>
        ))}
      </section>
    </div>
  );
}
