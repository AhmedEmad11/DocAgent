import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { Answer, AskEvent } from "@/lib/ask/events";
import type { AskStreamEnd } from "@/lib/ask/stream-client";
import { renderWithIntl } from "@/test-utils";

vi.mock("@/i18n/navigation", () => ({
  Link: ({
    href,
    children,
    ...p
  }: {
    href: string | { pathname: string };
    children: React.ReactNode;
  }) => (
    <a href={typeof href === "string" ? href : href.pathname} {...p}>
      {children}
    </a>
  ),
}));
vi.mock("next/dynamic", async () => {
  const { MarkdownText } = await import("./markdown-text");
  return { default: () => MarkdownText };
});

import { AskView, type ScopeDocument } from "./ask-view";

const docs: ScopeDocument[] = [
  { id: "d1", fileName: "lease.pdf", status: "Ready" },
  { id: "d2", fileName: "notes.md", status: "Ready" },
  { id: "d3", fileName: "pending.txt", status: "Processing" },
];

const answer: Answer = {
  answer: "Rent is due monthly [1].",
  notFound: false,
  citations: [
    {
      index: 1,
      documentId: "d1",
      fileName: "lease.pdf",
      pageNumber: 3,
      section: null,
      quote: "Rent is payable on the first day.",
    },
  ],
  mode: "byok",
  provider: "openrouter",
  model: "test/model",
  incomplete: false,
  toolCalls: 2,
  notices: [],
};

type Stream = (
  body: { question: string; documentIds?: string[] },
  onEvent: (e: AskEvent) => void,
  signal: AbortSignal,
) => Promise<AskStreamEnd>;

const instant =
  (events: AskEvent[]): Stream =>
  async (_b, onEvent) => {
    events.forEach(onEvent);
    return { kind: "finished" };
  };

const type = async (text: string) => userEvent.type(screen.getByLabelText("Your question"), text);
const ask = async () => userEvent.click(screen.getByRole("button", { name: "Ask" }));

describe("AskView", () => {
  it("shows the empty state when no document is ready", () => {
    renderWithIntl(<AskView documents={[docs[2]!]} stream={vi.fn()} />);
    expect(screen.getByTestId("no-ready")).toHaveTextContent("no documents that are ready yet");
    expect(screen.queryByLabelText("Your question")).not.toBeInTheDocument();
  });

  it("requires a question and counts characters up to 2000", async () => {
    const stream = vi.fn<Stream>();
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await ask();
    expect(screen.getByText("Enter a question.")).toBeInTheDocument();
    expect(stream).not.toHaveBeenCalled();
    await type("abc");
    expect(screen.getByTestId("counter")).toHaveTextContent("3 / 2000");
    expect(screen.getByLabelText("Your question")).toHaveAttribute("maxlength", "2000");
    expect(screen.getByLabelText("Your question")).toHaveAttribute("dir", "auto");
  });

  it("streams: shows the stages, then the cited answer, and clears the box", async () => {
    let finish!: () => void;
    const stream: Stream = async (_b, onEvent) => {
      onEvent({ type: "searching" });
      onEvent({ type: "tool_call" });
      await new Promise<void>((resolve) => (finish = resolve));
      onEvent({ type: "verifying" });
      onEvent({ type: "answer", answer });
      return { kind: "finished" };
    };
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await type("When is rent due?");
    await ask();

    expect(await screen.findByTestId("stage")).toHaveTextContent("Checking sources");
    expect(screen.getByTestId("live-region")).toHaveTextContent("Checking sources");
    expect(screen.getByRole("button", { name: "Ask" })).toBeDisabled(); // one question at a time (UI-QA-7)
    finish();

    const view = await screen.findByTestId("answer");
    expect(within(view).getByText(/Rent is due monthly/)).toBeInTheDocument();
    expect(within(view).getByTestId("citation-marker")).toHaveAttribute("href", "#a1-cite-1");
    const cite = within(view).getByTestId("citation");
    expect(cite).toHaveTextContent("lease.pdf");
    expect(cite).toHaveTextContent("Page 3");
    expect(cite).toHaveTextContent("Rent is payable on the first day.");
    expect(view).toHaveTextContent("Answered with your own key");
    expect(view).toHaveTextContent("openrouter / test/model");
    expect(screen.getByTestId("live-region")).toHaveTextContent("The answer is ready.");
    expect(screen.getByLabelText("Your question")).toHaveValue("");
    expect(screen.getByTestId("qa-question")).toHaveTextContent("When is rent due?");
  });

  it("sends only the chosen documents; not-ready documents are not selectable", async () => {
    const stream = vi.fn<Stream>(instant([{ type: "answer", answer }]));
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await userEvent.click(screen.getByRole("radio", { name: "Only the documents I choose" }));
    expect(screen.getByRole("checkbox", { name: /pending\.txt/ })).toBeDisabled();
    await userEvent.click(screen.getByRole("checkbox", { name: /notes\.md/ }));
    await type("q");
    await ask();
    await waitFor(() => expect(stream).toHaveBeenCalled());
    expect(stream.mock.calls[0]![0]).toEqual({ question: "q", documentIds: ["d2"] });
  });

  it("omits documentIds when scope is all ready documents", async () => {
    const stream = vi.fn<Stream>(instant([{ type: "answer", answer }]));
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await type("q");
    await ask();
    await waitFor(() => expect(stream).toHaveBeenCalled());
    expect(stream.mock.calls[0]![0]).toEqual({ question: "q" });
  });

  it("preselects a document from the link on the documents page", () => {
    renderWithIntl(<AskView documents={docs} initialSelected={["d2", "d3"]} stream={vi.fn()} />);
    expect(screen.getByRole("radio", { name: "Only the documents I choose" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /notes\.md/ })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: /pending\.txt/ })).not.toBeChecked(); // not ready: ignored
  });

  it("shows the not-found state instead of a normal answer", async () => {
    renderWithIntl(
      <AskView
        documents={docs}
        stream={instant([
          {
            type: "answer",
            answer: { ...answer, notFound: true, citations: [], answer: "No mention." },
          },
        ])}
      />,
    );
    await type("q");
    await ask();
    expect(await screen.findByTestId("answer-not-found")).toHaveTextContent(
      "This is not in your documents.",
    );
  });

  it("shows the incomplete notice and the notices of the answer", async () => {
    renderWithIntl(
      <AskView
        documents={docs}
        stream={instant([
          {
            type: "answer",
            answer: { ...answer, incomplete: true, notices: ["Arabic text was repaired."] },
          },
        ])}
      />,
    );
    await type("q");
    await ask();
    expect(await screen.findByTestId("answer-incomplete")).toHaveTextContent("may be incomplete");
    expect(screen.getByTestId("answer-notices")).toHaveTextContent("Arabic text was repaired.");
  });

  it("an error event mid-stream shows the message for its code, keeps the question, and retry asks again", async () => {
    const stream = vi
      .fn<Stream>()
      .mockImplementationOnce(
        instant([
          { type: "searching" },
          { type: "error", status: 503, title: "x", code: "llm_unavailable" },
        ]),
      )
      .mockImplementationOnce(instant([{ type: "answer", answer }]));
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await type("keep me");
    await ask();
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "The model provider is not responding. Try again in a minute.",
    );
    expect(screen.getByLabelText("Your question")).toHaveValue("keep me");

    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByTestId("answer")).toBeInTheDocument();
    expect(stream).toHaveBeenCalledTimes(2);
    expect(stream.mock.calls[1]![0]).toEqual({ question: "keep me" });
    expect(screen.getAllByTestId("qa-item")).toHaveLength(1); // retried in place
  });

  it("a refusal before the stream (409) and a dropped connection each show their message", async () => {
    const refuse: Stream = async () => ({ kind: "refused", problem: { status: 409, title: "c" } });
    const { unmount } = renderWithIntl(<AskView documents={docs} stream={refuse} />);
    await type("q");
    await ask();
    expect(await screen.findByRole("alert")).toHaveTextContent("This document is not ready yet.");
    unmount();

    const dropped: Stream = async () => ({ kind: "dropped" });
    renderWithIntl(<AskView documents={docs} stream={dropped} />);
    await type("q");
    await ask();
    expect(await screen.findByRole("alert")).toHaveTextContent("Cannot reach the service.");
  });

  it("cancel aborts the request, shows the cancelled state and keeps the text", async () => {
    let signal!: AbortSignal;
    const stream: Stream = (_b, onEvent, s) => {
      signal = s;
      onEvent({ type: "searching" });
      return new Promise((resolve) =>
        s.addEventListener("abort", () => resolve({ kind: "aborted" })),
      );
    };
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await type("long one");
    await ask();
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    expect(signal.aborted).toBe(true);
    expect(await screen.findByTestId("cancelled")).toHaveTextContent(
      "You cancelled this question.",
    );
    expect(screen.getByLabelText("Your question")).toHaveValue("long one");
    expect(screen.getByRole("button", { name: "Ask" })).toBeEnabled();
    expect(screen.getByTestId("live-region")).toHaveTextContent("The question was cancelled.");
  });

  it("keeps the questions of this tab as a list, newest first (OI-12)", async () => {
    const stream = vi.fn<Stream>(instant([{ type: "answer", answer }]));
    renderWithIntl(<AskView documents={docs} stream={stream} />);
    await type("first");
    await ask();
    await screen.findByTestId("answer");
    await type("second");
    await ask();
    await waitFor(() => expect(screen.getAllByTestId("qa-item")).toHaveLength(2));
    expect(screen.getAllByTestId("qa-question").map((q) => q.textContent)).toEqual([
      "second",
      "first",
    ]);
  });

  it("renders in Arabic, mirrored text direction per question", async () => {
    renderWithIntl(
      <AskView documents={docs} stream={instant([{ type: "answer", answer }])} />,
      "ar",
    );
    await userEvent.type(screen.getByLabelText("سؤالك"), "متى يستحق الإيجار؟");
    await userEvent.click(screen.getByRole("button", { name: "اسأل" }));
    expect(await screen.findByTestId("qa-question")).toHaveAttribute("dir", "auto");
    expect(screen.getByTestId("answer-meta")).toHaveTextContent("تمت الإجابة بمفتاحك الخاص");
  });
});
