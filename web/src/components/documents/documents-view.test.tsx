import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ApiDocument } from "@/lib/api/documents-schema";
import { renderWithIntl } from "@/test-utils";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));
const uploadFile = vi.fn();
vi.mock("@/lib/documents/upload-client", () => ({
  uploadFile: (...a: unknown[]) => uploadFile(...a),
}));

import { DocumentsView } from "./documents-view";

const doc = (over: Partial<ApiDocument> & { id: string }): ApiDocument => ({
  fileName: `${over.id}.pdf`,
  contentType: "application/pdf",
  sizeBytes: 2048,
  pageCount: 4,
  chunkCount: 12,
  status: "Ready",
  failureReason: null,
  extractionNotice: null,
  createdAt: "2026-11-22T10:00:00Z",
  ...over,
});

const noPolling = vi.fn();
beforeEach(() => {
  uploadFile.mockReset();
  noPolling.mockReset();
});
afterEach(() => vi.unstubAllGlobals());

describe("DocumentsView", () => {
  it("shows the empty state with the file types and the tier limit", () => {
    renderWithIntl(
      <DocumentsView initial={[]} hasLlmKey={false} chunks={{ used: 0, limit: 3000 }} />,
    );
    expect(screen.getByTestId("empty")).toHaveTextContent("You have no documents yet.");
    expect(screen.getByTestId("empty")).toHaveTextContent("5 MB");
    expect(screen.getByText("Documents: 0 of 3")).toBeInTheDocument();
  });

  it("lists every status with a text badge, the failure reason with a hint, and the extraction notice", () => {
    renderWithIntl(
      <DocumentsView
        hasLlmKey
        chunks={{ used: 100, limit: 20000 }}
        fetchList={noPolling}
        initial={[
          doc({ id: "p", status: "Pending" }),
          doc({ id: "r", status: "Ready", extractionNotice: "Arabic text was repaired." }),
          doc({
            id: "f",
            status: "Failed",
            failureReason: "No extractable text: scanned PDFs are not supported.",
          }),
        ]}
      />,
    );
    const statuses = screen.getAllByTestId("doc-status").map((s) => s.textContent);
    expect(statuses).toEqual(["Waiting", "Ready", "Failed"]);
    expect(screen.getByTestId("doc-notice")).toHaveTextContent("Arabic text was repaired.");
    const failed = screen.getByTestId("doc-failed");
    expect(failed).toHaveTextContent("scanned PDFs are not supported.");
    expect(failed).toHaveTextContent("Upload a text-based version");
    expect(screen.getByText("Documents: 3 of 100")).toBeInTheDocument();
  });

  it("renders file names as text with dir=auto, also hostile and Arabic ones (UI-DOC-9)", () => {
    const hostile = '<img src=x onerror="alert(1)">.pdf';
    renderWithIntl(
      <DocumentsView
        hasLlmKey
        chunks={{ used: 0, limit: 20000 }}
        initial={[
          doc({ id: "a", fileName: "عقد الإيجار.pdf" }),
          doc({ id: "b", fileName: hostile }),
        ]}
      />,
      "ar",
    );
    const names = screen.getAllByTestId("doc-name");
    expect(names[0]).toHaveAttribute("dir", "auto");
    expect(names[0]).toHaveTextContent("عقد الإيجار.pdf");
    expect(names[1]!.textContent).toBe(hostile);
    expect(names[1]!.querySelector("img")).toBeNull();
    expect(names[1]).toHaveAttribute("title", hostile);
  });

  it("disables upload at the document limit and explains it, suggesting a key in demo mode", () => {
    renderWithIntl(
      <DocumentsView
        hasLlmKey={false}
        chunks={{ used: 0, limit: 3000 }}
        fetchList={noPolling}
        initial={["a", "b", "c"].map((id) => doc({ id }))}
      />,
    );
    expect(screen.getByTestId("at-limit")).toHaveTextContent(
      "You have reached the limit of 3 documents",
    );
    expect(screen.getByRole("link", { name: /Add your own API key/ })).toHaveAttribute(
      "href",
      "/settings",
    );
    expect(screen.getByRole("button", { name: "choose files" })).toBeDisabled();
    expect(screen.getByTestId("file-input")).toBeDisabled();
  });

  it("warns above 80% of the chunk limit", () => {
    renderWithIntl(
      <DocumentsView initial={[]} hasLlmKey={false} chunks={{ used: 2500, limit: 3000 }} />,
    );
    expect(screen.getByTestId("chunks-high")).toHaveTextContent("more than 80%");
    const bar = within(screen.getByTestId("meter-chunks")).getByRole("progressbar");
    expect(bar).toHaveAttribute("value", "2500");
    expect(bar).toHaveAttribute("max", "3000");
    expect(bar).toHaveAccessibleName("Storage: 2,500 of 3,000 text chunks");
  });

  it("uploads a picked file, adds it as Waiting and shows a per-file error for a refusal", async () => {
    uploadFile
      .mockResolvedValueOnce({
        ok: true,
        document: { id: "new", fileName: "ok.txt", status: "Pending" },
      })
      .mockResolvedValueOnce({ ok: false, problem: { status: 409, title: "Conflict" } });
    renderWithIntl(
      <DocumentsView
        initial={[]}
        hasLlmKey={false}
        chunks={{ used: 0, limit: 3000 }}
        fetchList={noPolling}
      />,
    );
    await userEvent.upload(screen.getByTestId("file-input"), [
      new File(["x"], "ok.txt"),
      new File(["y"], "second.txt"),
    ]);
    await waitFor(() => expect(screen.getAllByTestId("upload-item")).toHaveLength(2));
    await waitFor(() => expect(screen.getByTestId("doc-name")).toHaveTextContent("ok.txt"));
    expect(screen.getByTestId("doc-status")).toHaveTextContent("Waiting");
    expect(screen.getByTestId("upload-error")).toHaveTextContent(
      "You have reached your document limit (3).",
    );
    expect(screen.getByTestId("live-region")).toHaveTextContent("ok.txt was uploaded");
  });

  it("deletes after a confirmation that names the file and says it is permanent", async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);
    renderWithIntl(
      <DocumentsView
        hasLlmKey
        chunks={{ used: 0, limit: 20000 }}
        initial={[doc({ id: "keep" }), doc({ id: "drop", fileName: "drop.pdf" })]}
      />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Delete drop.pdf" }));
    const dialog = screen.getByRole("dialog", { hidden: true });
    expect(dialog).toHaveTextContent("drop.pdf");
    expect(dialog).toHaveTextContent("removed permanently");

    await userEvent.click(
      within(dialog).getByRole("button", { name: "Delete permanently", hidden: true }),
    );
    await waitFor(() => expect(screen.queryByText("drop.pdf")).not.toBeInTheDocument());
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/proxy/documents/drop",
      expect.objectContaining({ method: "DELETE" }),
    );
    expect(screen.getByTestId("live-region")).toHaveTextContent("drop.pdf was deleted.");
  });

  it("keeps the document and shows the message when the delete fails", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({ title: "x" }), { status: 500 })),
    );
    renderWithIntl(
      <DocumentsView hasLlmKey chunks={{ used: 0, limit: 20000 }} initial={[doc({ id: "a" })]} />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Delete a.pdf" }));
    await userEvent.click(screen.getByRole("button", { name: "Delete permanently", hidden: true }));
    expect(
      await screen.findByText("Something went wrong on our side. Try again.", { exact: false }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("doc-name")).toHaveTextContent("a.pdf");
  });

  it("announces a status change found by polling in a polite live region", async () => {
    vi.useFakeTimers();
    const fetchList = vi
      .fn()
      .mockResolvedValue({ ok: true, documents: [doc({ id: "a", status: "Ready" })] });
    vi.stubGlobal(
      "fetch",
      vi.fn(
        async () =>
          new Response(JSON.stringify({ chunks: { used: 12, limit: 3000 } }), { status: 200 }),
      ),
    );
    renderWithIntl(
      <DocumentsView
        hasLlmKey={false}
        chunks={{ used: 0, limit: 3000 }}
        fetchList={fetchList}
        initial={[doc({ id: "a", status: "Processing" })]}
      />,
    );
    await act(async () => void (await vi.advanceTimersByTimeAsync(2100)));
    vi.useRealTimers();
    expect(screen.getByTestId("live-region")).toHaveTextContent("a.pdf: Ready");
    expect(screen.getByTestId("live-region")).toHaveAttribute("aria-live", "polite");
  });
});
