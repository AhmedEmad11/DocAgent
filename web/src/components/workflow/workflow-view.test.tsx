import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { Answer } from "@/lib/ask/events";
import { renderWithIntl } from "@/test-utils";
import { routerMock } from "@/test/router-mock";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));
vi.mock("next/dynamic", async () => {
  const { MarkdownText } = await import("@/components/ask/markdown-text");
  return { default: () => MarkdownText };
});

import { WorkflowView } from "./workflow-view";

const docs = [
  { id: "a", fileName: "lease.pdf" },
  { id: "b", fileName: "addendum.docx" },
  { id: "c", fileName: "عقد.pdf" },
];
const answer: Answer = {
  answer: "Both cover rent [1].",
  notFound: false,
  citations: [
    {
      index: 1,
      documentId: "a",
      fileName: "lease.pdf",
      pageNumber: 2,
      section: null,
      quote: "Rent is monthly.",
    },
  ],
  mode: "byok",
  provider: "p",
  model: "m",
  incomplete: false,
  notices: [],
};
type Run = NonNullable<React.ComponentProps<typeof WorkflowView>["run"]>;

describe("WorkflowView: summary", () => {
  it("summarizes the chosen document with the focus and shows the shared answer view", async () => {
    const run = vi.fn<Run>(async () => ({ ok: true, data: answer }));
    renderWithIntl(
      <WorkflowView kind="summary" documents={docs} initialA="b" demo={false} run={run} />,
    );
    expect(screen.getByLabelText("Document")).toHaveValue("b"); // preselected from the documents page
    await userEvent.type(screen.getByLabelText(/Focus/), "payment terms");
    await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
    const result = await screen.findByTestId("result");
    expect(result).toHaveTextContent("addendum.docx");
    expect(within(result).getByTestId("citation")).toHaveTextContent("Rent is monthly.");
    expect(run.mock.calls[0]![0]).toEqual({
      kind: "summary",
      documentId: "b",
      focus: "payment terms",
    });
    expect(screen.getByTestId("live-region")).toHaveTextContent("The summary is ready.");
  });

  it("asks for a document first and counts the focus up to 500", async () => {
    const run = vi.fn<Run>();
    renderWithIntl(<WorkflowView kind="summary" documents={docs} demo={false} run={run} />);
    await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
    expect(screen.getByTestId("form-error")).toHaveTextContent("Choose a document.");
    expect(run).not.toHaveBeenCalled();
    await userEvent.type(screen.getByLabelText(/Focus/), "abcd");
    expect(screen.getByTestId("focus-count")).toHaveTextContent("4 / 500");
    expect(screen.getByLabelText(/Focus/)).toHaveAttribute("maxlength", "500");
  });

  it("states the demo cost before submitting, only in demo mode", () => {
    const { unmount } = renderWithIntl(
      <WorkflowView kind="summary" documents={docs} demo run={vi.fn()} />,
    );
    expect(screen.getByTestId("demo-cost")).toHaveTextContent(
      "counts as one of your daily questions",
    );
    unmount();
    renderWithIntl(<WorkflowView kind="summary" documents={docs} demo={false} run={vi.fn()} />);
    expect(screen.queryByTestId("demo-cost")).not.toBeInTheDocument();
  });

  it("shows a working state with the expected wait and a cancel that aborts the request", async () => {
    let signal!: AbortSignal;
    const run: Run = (_r, s) => {
      signal = s;
      return new Promise(() => {});
    };
    renderWithIntl(
      <WorkflowView kind="summary" documents={docs} initialA="a" demo={false} run={run} />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
    expect(await screen.findByTestId("working")).toHaveTextContent("up to two minutes");
    expect(screen.getByRole("progressbar", { name: "Working on it" })).not.toHaveAttribute("value"); // indeterminate
    expect(screen.getByRole("button", { name: "Summarize" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(signal.aborted).toBe(true);
    expect(screen.getByTestId("cancelled")).toBeInTheDocument();
    expect(screen.queryByTestId("working")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Summarize" })).toBeEnabled();
  });

  it("ignores an answer that arrives after cancelling", async () => {
    let resolve!: (v: Awaited<ReturnType<Run>>) => void;
    const run: Run = () => new Promise((r) => (resolve = r));
    renderWithIntl(
      <WorkflowView kind="summary" documents={docs} initialA="a" demo={false} run={run} />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    resolve({ ok: true, data: answer });
    await waitFor(() => expect(screen.queryByTestId("result")).not.toBeInTheDocument());
  });

  it("shows the message for 404, 409 and the 504 timeout, with a retry", async () => {
    const run = vi.fn<Run>();
    renderWithIntl(
      <WorkflowView kind="summary" documents={docs} initialA="a" demo={false} run={run} />,
    );
    for (const [status, text] of [
      [404, "This document no longer exists."],
      [409, "This document is not ready yet."],
      [504, "The question took too long."],
    ] as const) {
      run.mockResolvedValueOnce({ ok: false, problem: { status, title: "x" } });
      await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
      expect(await screen.findByRole("alert")).toHaveTextContent(text);
    }
    run.mockResolvedValueOnce({ ok: true, data: answer });
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByTestId("result")).toBeInTheDocument();
  });

  it("explains an exhausted demo quota with the reset time", async () => {
    const run = vi.fn<Run>(async () => ({
      ok: false,
      problem: { status: 429, title: "x", code: "demo_quota_exceeded" },
    }));
    renderWithIntl(
      <WorkflowView
        kind="summary"
        documents={docs}
        initialA="a"
        demo
        quota={{ remaining: 1, resetsAt: "2026-11-24T00:00:00Z" }}
        run={run}
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      /They reset at .*Add your own API key/,
    );
  });

  it("turns the controls off at zero questions left, with the reset time and the way to add a key, and re-reads the allowance after an answer", async () => {
    const { unmount } = renderWithIntl(
      <WorkflowView
        kind="summary"
        documents={docs}
        initialA="a"
        demo
        quota={{ remaining: 0, resetsAt: "2026-11-24T00:00:00Z" }}
        run={vi.fn()}
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("They reset at");
    expect(screen.getByRole("link", { name: "Add an API key" })).toHaveAttribute(
      "href",
      "/settings",
    );
    expect(screen.getByRole("button", { name: "Summarize" })).toBeDisabled();
    unmount();

    const run = vi.fn<Run>(async () => ({ ok: true, data: answer }));
    renderWithIntl(
      <WorkflowView
        kind="summary"
        documents={docs}
        initialA="a"
        demo
        quota={{ remaining: 4, resetsAt: "2026-11-24T00:00:00Z" }}
        run={run}
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Summarize" }));
    await screen.findByTestId("result");
    expect(routerMock.refresh).toHaveBeenCalled();
  });

  it("shows an empty state when there is no ready document", () => {
    renderWithIntl(<WorkflowView kind="summary" documents={[]} demo={false} run={vi.fn()} />);
    expect(screen.getByTestId("not-enough")).toHaveTextContent("no documents that are ready yet");
  });
});

describe("WorkflowView: compare", () => {
  it("never offers the same document twice and blocks submitting it", async () => {
    const run = vi.fn<Run>();
    renderWithIntl(
      <WorkflowView kind="compare" documents={docs} initialA="a" demo={false} run={run} />,
    );
    const second = screen.getByLabelText("Second document");
    expect(within(second).getByRole("option", { name: "lease.pdf" })).toBeDisabled();
    await userEvent.selectOptions(second, "b");
    expect(
      within(screen.getByLabelText("First document")).getByRole("option", {
        name: "addendum.docx",
      }),
    ).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Compare" }));
    await waitFor(() => expect(run).toHaveBeenCalled());
    expect(run.mock.calls[0]![0]).toEqual({
      kind: "compare",
      documentIdA: "a",
      documentIdB: "b",
      focus: "",
    });
  });

  it("needs two documents and says so when only one is chosen", async () => {
    const run = vi.fn<Run>();
    renderWithIntl(
      <WorkflowView kind="compare" documents={docs} initialA="a" demo={false} run={run} />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Compare" }));
    expect(screen.getByTestId("form-error")).toHaveTextContent("Choose two documents.");
    expect(run).not.toHaveBeenCalled();
  });

  it("needs at least two ready documents", () => {
    renderWithIntl(
      <WorkflowView kind="compare" documents={[docs[0]!]} demo={false} run={vi.fn()} />,
    );
    expect(screen.getByTestId("not-enough")).toHaveTextContent("at least two documents");
  });

  it("shows both document names, also Arabic ones, in the result", async () => {
    const run = vi.fn<Run>(async () => ({ ok: true, data: answer }));
    renderWithIntl(
      <WorkflowView kind="compare" documents={docs} initialA="a" demo={false} run={run} />,
      "ar",
    );
    await userEvent.selectOptions(screen.getByLabelText("المستند الثاني"), "c");
    await userEvent.click(screen.getByRole("button", { name: "قارن" }));
    const result = await screen.findByTestId("result");
    expect(result).toHaveTextContent("lease.pdf");
    expect(result).toHaveTextContent("عقد.pdf");
    expect(result.querySelectorAll("bdi")).toHaveLength(2);
  });
});
