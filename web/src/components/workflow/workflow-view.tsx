"use client";

import { useEffect, useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { AnswerView } from "@/components/ask/answer-view";
import { Button } from "@/components/ui/button";
import { ProblemAlert } from "@/components/ui/problem-alert";
import { Link } from "@/i18n/navigation";
import type { ApiProblem } from "@/lib/api/problem";
import type { Answer } from "@/lib/ask/events";
import {
  MAX_FOCUS,
  runWorkflow,
  validateWorkflow,
  type WorkflowRequest,
} from "@/lib/workflow/requests";

export interface WorkflowDocument {
  id: string;
  fileName: string;
}

type Run = typeof runWorkflow;
type State =
  | { phase: "idle" }
  | { phase: "running" }
  | { phase: "done"; answer: Answer; names: string[] }
  | { phase: "failed"; problem: ApiProblem }
  | { phase: "cancelled" };

const selectClass =
  "h-10 w-full rounded-md border border-input bg-background px-3 text-base focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none";

function DocumentSelect({
  id,
  label,
  value,
  onChange,
  documents,
  disabledId,
  disabled,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (v: string) => void;
  documents: WorkflowDocument[];
  /** The document chosen in the other selector: not offered twice (UI-SUM-2). */
  disabledId?: string;
  disabled: boolean;
}) {
  const t = useTranslations("Workflow");
  return (
    <div className="space-y-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      <select
        id={id}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        dir="auto"
        className={selectClass}
      >
        <option value="">{t("choose")}</option>
        {documents.map((d) => (
          <option key={d.id} value={d.id} disabled={d.id === disabledId}>
            {d.fileName}
          </option>
        ))}
      </select>
    </div>
  );
}

/**
 * Summary of one document or comparison of two (UI-SUM-1 to 4): optional focus, an indeterminate working state with cancel and the
 * expected wait (the call is not streamed), the shared answer view, and the demo cost note before submitting.
 */
export function WorkflowView({
  kind,
  documents,
  initialA,
  demo,
  resetsAt,
  run = runWorkflow,
}: {
  kind: "summary" | "compare";
  /** Ready documents only. */
  documents: WorkflowDocument[];
  initialA?: string | undefined;
  demo: boolean;
  resetsAt?: string | undefined;
  /** Test seam. */
  run?: Run;
}) {
  const t = useTranslations("Workflow");
  const [a, setA] = useState(documents.some((d) => d.id === initialA) ? (initialA ?? "") : "");
  const [b, setB] = useState("");
  const [focus, setFocus] = useState("");
  const [state, setState] = useState<State>({ phase: "idle" });
  const [error, setError] = useState<ReturnType<typeof validateWorkflow>>(null);
  const [announcement, setAnnouncement] = useState("");
  const aborter = useRef<AbortController | null>(null);
  const running = state.phase === "running";

  useEffect(() => () => aborter.current?.abort(), []);

  const nameOf = (id: string) => documents.find((d) => d.id === id)?.fileName ?? "";

  const start = async () => {
    const request: WorkflowRequest =
      kind === "summary"
        ? { kind, documentId: a, focus }
        : { kind, documentIdA: a, documentIdB: b, focus };
    const invalid = validateWorkflow(request);
    setError(invalid);
    if (invalid) return;
    const controller = new AbortController();
    aborter.current = controller;
    setState({ phase: "running" });
    setAnnouncement(t("working"));
    try {
      const result = await run(request, controller.signal);
      if (controller.signal.aborted) return;
      if (result.ok) {
        setState({
          phase: "done",
          answer: result.data,
          names: kind === "summary" ? [nameOf(a)] : [nameOf(a), nameOf(b)],
        });
        setAnnouncement(t(`${kind}.ready`));
      } else {
        setState({ phase: "failed", problem: result.problem });
      }
    } catch {
      // aborted by the user: the state was already set by cancel()
    } finally {
      if (aborter.current === controller) aborter.current = null;
    }
  };

  const cancel = () => {
    aborter.current?.abort();
    setState({ phase: "cancelled" });
    setAnnouncement(t("cancelledAnnounce"));
  };

  if (documents.length < (kind === "compare" ? 2 : 1)) {
    return (
      <div
        className="rounded-lg border border-dashed border-border p-6 text-center"
        data-testid="not-enough"
      >
        <p className="font-medium">{t(`${kind}.notEnough`)}</p>
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
      <form
        noValidate
        className="space-y-5"
        data-testid="workflow-form"
        onSubmit={(e) => {
          e.preventDefault();
          if (!running) void start();
        }}
      >
        {demo ? (
          <p
            className="rounded-md border border-border bg-muted p-3 text-sm"
            data-testid="demo-cost"
          >
            {t("demoCost")}
          </p>
        ) : null}
        <DocumentSelect
          id="doc-a"
          label={kind === "summary" ? t("summary.documentLabel") : t("compare.firstLabel")}
          value={a}
          onChange={(v) => {
            setA(v);
            setError(null);
          }}
          documents={documents}
          {...(kind === "compare" ? { disabledId: b } : {})}
          disabled={running}
        />
        {kind === "compare" ? (
          <DocumentSelect
            id="doc-b"
            label={t("compare.secondLabel")}
            value={b}
            onChange={(v) => {
              setB(v);
              setError(null);
            }}
            documents={documents}
            disabledId={a}
            disabled={running}
          />
        ) : null}
        <div className="space-y-1.5">
          <label htmlFor="focus" className="text-sm font-medium">
            {t("focusLabel")}
          </label>
          <textarea
            id="focus"
            dir="auto"
            rows={3}
            maxLength={MAX_FOCUS}
            value={focus}
            readOnly={running}
            onChange={(e) => setFocus(e.target.value)}
            aria-describedby="focus-hint focus-count"
            className="min-h-20 w-full rounded-md border border-input bg-background p-3 text-start text-base focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
          />
          <div className="flex justify-between gap-2 text-sm text-muted-foreground">
            <span id="focus-hint">{t("focusHint")}</span>
            <span id="focus-count" data-testid="focus-count">
              {t("counter", { count: focus.length, max: MAX_FOCUS })}
            </span>
          </div>
        </div>
        {error ? (
          <p role="alert" className="text-sm text-destructive" data-testid="form-error">
            {t(`errors.${error}`)}
          </p>
        ) : null}
        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={running} aria-busy={running}>
            {t(`${kind}.submit`)}
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

      {state.phase === "running" ? (
        <div className="space-y-2" data-testid="working">
          <p className="font-medium">{t("working")}</p>
          <progress className="meter" aria-label={t("working")} />
          <p className="text-sm text-muted-foreground">{t("expectedWait")}</p>
        </div>
      ) : null}
      {state.phase === "cancelled" ? (
        <p className="text-sm" data-testid="cancelled">
          {t("cancelled")}
        </p>
      ) : null}
      {state.phase === "failed" ? (
        <ProblemAlert
          problem={state.problem}
          ctx={{ area: kind }}
          resetsAt={resetsAt}
          onRetry={() => void start()}
        />
      ) : null}
      {state.phase === "done" ? (
        <section aria-labelledby="result-title" className="space-y-3" data-testid="result">
          <h2 id="result-title" className="text-lg font-semibold">
            {t(`${kind}.resultTitle`)}
          </h2>
          <p className="text-sm text-muted-foreground">
            {state.names.map((name, i) => (
              <span key={i}>
                {i > 0 ? " · " : null}
                <bdi dir="auto" className="font-medium text-foreground">
                  {name}
                </bdi>
              </span>
            ))}
          </p>
          <AnswerView answer={state.answer} idPrefix={`${kind}-`} />
        </section>
      ) : null}
    </div>
  );
}
