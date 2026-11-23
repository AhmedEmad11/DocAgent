import { afterEach, describe, expect, it, vi } from "vitest";
import { deleteKey, loadModels, saveKey } from "./client";

afterEach(() => vi.unstubAllGlobals());

const KEY = "sk-example-only-not-a-real-key-123456";
const stub = (response: Response) => {
  const fetchMock = vi.fn(async () => response);
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
};
const call = (fetchMock: ReturnType<typeof vi.fn>) =>
  fetchMock.mock.calls[0] as unknown as [string, RequestInit];

describe("key client (UI-KEY-3, UI-KEY-5)", () => {
  it("lists models with the key in a request header, never in the URL, and to our own BFF only", async () => {
    const fetchMock = stub(
      new Response(
        JSON.stringify({ provider: "openrouter", models: [{ id: "a/b", name: "AB" }] }),
        { status: 200 },
      ),
    );
    const res = await loadModels("openrouter", `  ${KEY} `);
    expect(res).toEqual({ ok: true, data: [{ id: "a/b", name: "AB" }] });
    const [url, init] = call(fetchMock);
    expect(url).toBe("/api/proxy/providers/openrouter/models");
    expect(url).not.toContain(KEY);
    expect((init.headers as Record<string, string>)["x-llm-api-key"]).toBe(KEY);
    expect(init.method).toBe("GET");
  });

  it("saves with the key in the JSON body only, trimmed", async () => {
    const fetchMock = stub(
      new Response(
        JSON.stringify({
          provider: "openai",
          model: "gpt-4.1",
          last4: "3456",
          updatedAt: "2026-11-23T10:00:00Z",
        }),
        { status: 200 },
      ),
    );
    const res = await saveKey({ provider: "openai", model: " gpt-4.1 ", apiKey: ` ${KEY} ` });
    expect(res.ok && res.data.last4).toBe("3456");
    const [url, init] = call(fetchMock);
    expect(url).toBe("/api/proxy/me/llm-key");
    expect(url).not.toContain("sk-");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({
      provider: "openai",
      model: "gpt-4.1",
      apiKey: KEY,
    });
  });

  it("returns the API's refusal as a problem, never echoing the key", async () => {
    stub(
      new Response(
        JSON.stringify({ title: "LLM key rejected", status: 400, code: "llm_key_invalid" }),
        { status: 400 },
      ),
    );
    const res = await saveKey({ provider: "openai", model: "gpt-4.1", apiKey: KEY });
    expect(res.ok).toBe(false);
    expect(JSON.stringify(res)).not.toContain(KEY);
  });

  it("deletes through the proxy", async () => {
    const fetchMock = stub(new Response(null, { status: 204 }));
    expect(await deleteKey()).toEqual({ ok: true, data: null });
    expect(call(fetchMock)[1].method).toBe("DELETE");
  });
});
