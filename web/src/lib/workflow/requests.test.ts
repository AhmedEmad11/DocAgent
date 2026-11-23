import { afterEach, describe, expect, it, vi } from "vitest";
import { MAX_FOCUS, runWorkflow, validateWorkflow } from "./requests";

afterEach(() => vi.unstubAllGlobals());

const A = "3f2b8c1e-6a52-4f0e-9d3b-0a1b2c3d4e5f";
const B = "4f2b8c1e-6a52-4f0e-9d3b-0a1b2c3d4e5f";
const answer = {
  answer: "x",
  notFound: false,
  citations: [],
  mode: "byok",
  provider: "p",
  model: "m",
  incomplete: false,
  notices: [],
};

describe("validateWorkflow (UI-SUM-1, 2)", () => {
  it("summary needs a document and a focus within 500 characters", () => {
    expect(validateWorkflow({ kind: "summary", documentId: "", focus: "" })).toBe("chooseDocument");
    expect(validateWorkflow({ kind: "summary", documentId: A, focus: "" })).toBeNull();
    expect(
      validateWorkflow({ kind: "summary", documentId: A, focus: "x".repeat(MAX_FOCUS) }),
    ).toBeNull();
    expect(
      validateWorkflow({ kind: "summary", documentId: A, focus: "x".repeat(MAX_FOCUS + 1) }),
    ).toBe("focusTooLong");
    expect(
      validateWorkflow({
        kind: "summary",
        documentId: A,
        focus: `${" ".repeat(10)}ok${" ".repeat(10)}`,
      }),
    ).toBeNull();
  });

  it("compare needs two different documents (the same-document guard)", () => {
    expect(validateWorkflow({ kind: "compare", documentIdA: A, documentIdB: "", focus: "" })).toBe(
      "chooseTwo",
    );
    expect(validateWorkflow({ kind: "compare", documentIdA: "", documentIdB: B, focus: "" })).toBe(
      "chooseTwo",
    );
    expect(validateWorkflow({ kind: "compare", documentIdA: A, documentIdB: A, focus: "" })).toBe(
      "sameDocument",
    );
    expect(
      validateWorkflow({ kind: "compare", documentIdA: A, documentIdB: B, focus: "" }),
    ).toBeNull();
  });
});

describe("runWorkflow", () => {
  it("posts the trimmed focus to the allowlisted summary route, and nothing when there is none", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(answer), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const signal = new AbortController().signal;
    expect(
      (await runWorkflow({ kind: "summary", documentId: A, focus: "  payment terms " }, signal)).ok,
    ).toBe(true);
    await runWorkflow({ kind: "summary", documentId: A, focus: "   " }, signal);
    const calls = fetchMock.mock.calls as unknown as [string, RequestInit][];
    expect(calls[0]![0]).toBe(`/api/proxy/documents/${A}/summary`);
    expect(JSON.parse(calls[0]![1].body as string)).toEqual({ focus: "payment terms" });
    expect(JSON.parse(calls[1]![1].body as string)).toEqual({});
    expect(calls[0]![1].signal).toBe(signal);
  });

  it("posts both ids to the compare route", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(answer), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    await runWorkflow(
      { kind: "compare", documentIdA: A, documentIdB: B, focus: "risks" },
      new AbortController().signal,
    );
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/proxy/documents/compare");
    expect(JSON.parse(init.body as string)).toEqual({
      documentIdA: A,
      documentIdB: B,
      focus: "risks",
    });
  });

  it("returns API problems and rejects a malformed answer as a problem", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(
        async () =>
          new Response(JSON.stringify({ title: "Timed out", code: undefined }), { status: 504 }),
      ),
    );
    const timeout = await runWorkflow(
      { kind: "summary", documentId: A, focus: "" },
      new AbortController().signal,
    );
    expect(!timeout.ok && timeout.problem.status).toBe(504);
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("{}", { status: 200 })),
    );
    const bad = await runWorkflow(
      { kind: "summary", documentId: A, focus: "" },
      new AbortController().signal,
    );
    expect(bad.ok).toBe(false);
  });
});
