"use client";

import dynamic from "next/dynamic";
import { useState } from "react";
import { Copy } from "lucide-react";
import { useTranslations } from "next-intl";
import { PrivacyText } from "@/components/demo/demo-banner-view";
import { Button } from "@/components/ui/button";
import { isKnownPrivacyNotice } from "@/lib/demo/privacy";
import type { Answer } from "@/lib/ask/events";

// The Markdown renderer is the heaviest part of the page: loaded when the first answer arrives (UI-PERF-2).
const MarkdownText = dynamic(() => import("./markdown-text").then((m) => m.MarkdownText), {
  ssr: false,
  loading: () => <p className="text-sm text-muted-foreground">…</p>,
});

/** Plain text of an answer with its citations, for the clipboard (UI-QA-12). */
export function answerAsText(answer: Answer): string {
  const cites = answer.citations
    .map(
      (c) =>
        `[${c.index}] ${c.fileName}${c.pageNumber ? `, p. ${c.pageNumber}` : ""}${c.section ? `, ${c.section}` : ""}: "${c.quote}"`,
    )
    .join("\n");
  return cites ? `${answer.answer}\n\n${cites}` : answer.answer;
}

/**
 * The shared answer view of ask, summary and compare (UI-QA-3 to 6, UI-SUM-1): sanitized Markdown with linked citation markers,
 * the distinct not-found state, the incomplete notice, mode/provider/model, notices and the citation list.
 */
export function AnswerView({ answer, idPrefix }: { answer: Answer; idPrefix: string }) {
  const t = useTranslations("Answer");
  const [copied, setCopied] = useState(false);
  const indexes = answer.citations.map((c) => c.index);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(answerAsText(answer));
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch {
      // clipboard unavailable: nothing to do
    }
  };

  return (
    <div className="space-y-4" data-testid="answer">
      {answer.notices.length > 0 ? (
        <ul className="space-y-2" aria-label={t("notices")} data-testid="answer-notices">
          {answer.notices.map((notice, i) => (
            // The demo privacy notice is translated (OI-13); other API text is English only and shown as given (UI-I18N-7).
            <li
              key={i}
              {...(isKnownPrivacyNotice(notice) ? {} : { dir: "ltr" as const })}
              className="rounded-md border border-border bg-muted p-3 text-start text-sm"
              data-testid={isKnownPrivacyNotice(notice) ? "privacy-notice" : "notice"}
            >
              <PrivacyText text={notice} />
            </li>
          ))}
        </ul>
      ) : null}

      {answer.notFound ? (
        <div
          className="space-y-2 rounded-md border border-border p-4"
          data-testid="answer-not-found"
        >
          <p className="font-medium">{t("notFound")}</p>
          <p className="text-sm text-muted-foreground">{t("notFoundHint")}</p>
          {answer.answer ? (
            <div dir="auto" className="text-start">
              {answer.answer}
            </div>
          ) : null}
        </div>
      ) : (
        <MarkdownText text={answer.answer} citationIndexes={indexes} idPrefix={idPrefix} />
      )}

      {answer.incomplete ? (
        <p
          role="status"
          className="rounded-md border border-border bg-muted p-3 text-sm"
          data-testid="answer-incomplete"
        >
          {t("incomplete")}
        </p>
      ) : null}

      {answer.citations.length > 0 ? (
        <section aria-labelledby={`${idPrefix}cites`} className="space-y-2">
          <h3 id={`${idPrefix}cites`} className="text-sm font-semibold">
            {t("citations")}
          </h3>
          <ol className="space-y-2">
            {answer.citations.map((c) => (
              <li
                key={c.index}
                id={`${idPrefix}cite-${c.index}`}
                tabIndex={-1}
                data-testid="citation"
                className="space-y-1 rounded-md border border-border p-3 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 target:bg-muted"
              >
                <p className="flex flex-wrap items-baseline gap-x-2">
                  <span className="font-medium">[{c.index}]</span>
                  <span dir="auto" className="font-medium">
                    {c.fileName}
                  </span>
                  <span className="text-muted-foreground">
                    {c.pageNumber ? t("page", { page: c.pageNumber }) : null}
                    {c.pageNumber && c.section ? " · " : null}
                    {c.section ? (
                      <span dir="auto">{t("section", { section: c.section })}</span>
                    ) : null}
                  </span>
                </p>
                <blockquote
                  dir="auto"
                  className="border-s-2 border-border ps-3 text-muted-foreground"
                >
                  {c.quote}
                </blockquote>
              </li>
            ))}
          </ol>
        </section>
      ) : null}

      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
        <p data-testid="answer-meta">
          {answer.mode === "demo" ? t("modeDemo") : t("modeByok")}{" "}
          <span dir="ltr" className="inline-block">
            ({answer.provider} / {answer.model})
          </span>
        </p>
        <Button type="button" variant="ghost" size="sm" onClick={() => void copy()}>
          <Copy aria-hidden="true" />
          {copied ? t("copied") : t("copy")}
        </Button>
      </div>
    </div>
  );
}
