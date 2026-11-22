import { describe, expect, it } from "vitest";
import { seal, unseal } from "./seal";

const secrets = { current: "example-only-secret-0123456789abcdef-A" };
const NOW = Date.UTC(2026, 10, 22, 12, 0, 0);

describe("seal / unseal (UI-SEC-1)", () => {
  it("round-trips a payload", async () => {
    const token = await seal({ at: "access", rt: "refresh" }, "session", secrets, 60, NOW);
    const payload = await unseal(token, "session", secrets, NOW + 1000);
    expect(payload).toMatchObject({ at: "access", rt: "refresh" });
  });

  it("does not contain the plaintext (it is encrypted, not just encoded)", async () => {
    const token = await seal({ at: "SECRET-ACCESS-TOKEN" }, "session", secrets, 60, NOW);
    expect(token).not.toContain("SECRET-ACCESS-TOKEN");
    expect(Buffer.from(token.split(".").join(""), "base64url").toString("utf8")).not.toContain(
      "SECRET-ACCESS",
    );
    expect(token.split(".")).toHaveLength(5); // JWE compact
    expect(JSON.parse(Buffer.from(token.split(".")[0]!, "base64url").toString())).toEqual({
      alg: "dir",
      enc: "A256GCM",
    });
  });

  it("rejects an expired token (absolute lifetime)", async () => {
    const token = await seal({ at: "a" }, "session", secrets, 60, NOW);
    expect(await unseal(token, "session", secrets, NOW + 59_000)).not.toBeNull();
    expect(await unseal(token, "session", secrets, NOW + 61_000)).toBeNull();
  });

  it("rejects a token sealed with another secret, a tampered token and garbage", async () => {
    const token = await seal({ at: "a" }, "session", secrets, 60, NOW);
    expect(
      await unseal(token, "session", { current: "another-secret-another-secret-0123456789" }, NOW),
    ).toBeNull();
    const parts = token.split(".");
    parts[3] = parts[3]!.slice(0, -2) + (parts[3]!.endsWith("AA") ? "BB" : "AA");
    expect(await unseal(parts.join("."), "session", secrets, NOW)).toBeNull();
    expect(await unseal("", "session", secrets, NOW)).toBeNull();
    expect(await unseal("a.b.c.d.e", "session", secrets, NOW)).toBeNull();
    expect(await unseal("plain-text", "session", secrets, NOW)).toBeNull();
  });

  it("accepts the previous secret while rotating (and only then)", async () => {
    const old = { current: "old-secret-old-secret-0123456789abcdef" };
    const token = await seal({ at: "a" }, "session", old, 60, NOW);
    const rotated = { current: secrets.current, previous: old.current };
    expect(await unseal(token, "session", rotated, NOW)).not.toBeNull();
    expect(await unseal(token, "session", secrets, NOW)).toBeNull();
  });

  it("keeps purposes apart: a pending-email cookie is not a session", async () => {
    const token = await seal({ email: "a@example.com" }, "pending-email", secrets, 60, NOW);
    expect(await unseal(token, "session", secrets, NOW)).toBeNull();
    expect(await unseal(token, "external-state", secrets, NOW)).toBeNull();
    expect(await unseal(token, "pending-email", secrets, NOW)).not.toBeNull();
  });

  it("produces a different token every time (random IV)", async () => {
    const a = await seal({ at: "a" }, "session", secrets, 60, NOW);
    const b = await seal({ at: "a" }, "session", secrets, 60, NOW);
    expect(a).not.toBe(b);
  });
});
