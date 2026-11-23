"use client";

import ReactMarkdown, { type Components } from "react-markdown";
import rehypeSanitize, { type Options } from "rehype-sanitize";
import { remarkCitations } from "@/lib/ask/citations";

// Answers are model output and can carry prompt-injected content, so they are rendered as sanitized Markdown (UI-SEC-8, R9.11):
// raw HTML is never parsed (react-markdown without rehype-raw), the tree is sanitized again against an allow-list of tags with no
// images, scripts, styles, ids or event handlers, and links may only be http(s), relative or in-page (citation markers).

const SCHEMA: Options = {
  tagNames: [
    "p",
    "br",
    "strong",
    "em",
    "del",
    "ul",
    "ol",
    "li",
    "blockquote",
    "code",
    "pre",
    "a",
    "hr",
    "h1",
    "h2",
    "h3",
    "h4",
    "h5",
    "h6",
  ],
  attributes: { a: ["href"] },
  protocols: { href: ["http", "https"] },
  strip: ["script", "style", "iframe", "object", "embed"],
  clobber: [],
};

/** http(s), same-site and in-page links only (UI-SEC-8); everything else loses its target. */
export function safeUrl(url: string): string {
  const u = url.trim();
  if (/^https?:\/\//i.test(u)) return u;
  if (u.startsWith("#") || (u.startsWith("/") && !u.startsWith("//"))) return u;
  return "";
}

const block = (Tag: "p" | "li" | "blockquote", className = "") =>
  function Block({ children }: { children?: React.ReactNode }) {
    // Each block takes its own direction: Arabic and English paragraphs can alternate (UI-I18N-6).
    return (
      <Tag dir="auto" className={className}>
        {children}
      </Tag>
    );
  };

const heading = function Heading({ children }: { children?: React.ReactNode }) {
  return (
    <p dir="auto" className="font-semibold">
      {children}
    </p>
  );
};

export function MarkdownText({
  text,
  citationIndexes,
  idPrefix,
}: {
  text: string;
  citationIndexes: readonly number[];
  idPrefix: string;
}) {
  const components: Components = {
    p: block("p"),
    li: block("li"),
    blockquote: block("blockquote", "border-s-2 border-border ps-3 text-muted-foreground"),
    h1: heading,
    h2: heading,
    h3: heading,
    h4: heading,
    h5: heading,
    h6: heading,
    ul: ({ children }) => <ul className="list-disc space-y-1 ps-6">{children}</ul>,
    ol: ({ children }) => <ol className="list-decimal space-y-1 ps-6">{children}</ol>,
    pre: ({ children }) => (
      <pre dir="ltr" className="overflow-x-auto rounded-md bg-muted p-3 text-start text-sm">
        {children}
      </pre>
    ),
    code: ({ children }) => (
      <code className="rounded bg-muted px-1 py-0.5 text-sm" dir="ltr">
        {children}
      </code>
    ),
    a: ({ href, children }) => {
      if (href?.startsWith("#")) {
        return (
          <a
            href={href}
            className="text-primary underline underline-offset-2"
            data-testid="citation-marker"
            onClick={(e) => {
              // In-page jump that also moves focus to the citation (the default jump does not focus it).
              const target = document.getElementById(href.slice(1));
              if (target) {
                e.preventDefault();
                target.focus();
                target.scrollIntoView({ block: "nearest" });
              }
            }}
          >
            {children}
          </a>
        );
      }
      if (!href) return <>{children}</>;
      return (
        <a
          href={href}
          target="_blank"
          rel="noopener noreferrer"
          className="text-primary underline underline-offset-4"
        >
          {children}
        </a>
      );
    },
  };
  return (
    <div className="space-y-3">
      <ReactMarkdown
        skipHtml
        urlTransform={safeUrl}
        remarkPlugins={[[remarkCitations, { indexes: citationIndexes, prefix: idPrefix }]]}
        rehypePlugins={[[rehypeSanitize, SCHEMA]]}
        components={components}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}
