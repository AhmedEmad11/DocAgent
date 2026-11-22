import { describe, expect, it } from "vitest";
import {
  MAX_COOKIE_VALUE_BYTES,
  REFRESH_MARGIN_SECONDS,
  SESSION_MAX_AGE_SECONDS,
  needsRefresh,
  sealSessionData,
  unsealSessionData,
} from "./model";

const secrets = { current: "example-only-secret-0123456789abcdef-A" };
const NOW = Date.UTC(2026, 10, 22, 12, 0, 0);
const nowSec = Math.floor(NOW / 1000);

describe("session model", () => {
  it("seals and unseals the token pair and nothing else", async () => {
    const token = await sealSessionData({ at: "A", rt: "R", ate: nowSec + 3600 }, secrets, NOW);
    expect(await unsealSessionData(token, secrets, NOW)).toMatchObject({
      at: "A",
      rt: "R",
      ate: nowSec + 3600,
    });
  });

  it("expires after the refresh token's lifetime of 14 days (D26, UI-SEC-2)", async () => {
    expect(SESSION_MAX_AGE_SECONDS).toBe(14 * 24 * 3600);
    const token = await sealSessionData({ at: "A", rt: "R", ate: nowSec + 3600 }, secrets, NOW);
    expect(
      await unsealSessionData(token, secrets, NOW + (SESSION_MAX_AGE_SECONDS - 10) * 1000),
    ).not.toBeNull();
    expect(
      await unsealSessionData(token, secrets, NOW + (SESSION_MAX_AGE_SECONDS + 10) * 1000),
    ).toBeNull();
  });

  it("rejects a valid token of the wrong shape", async () => {
    const { seal } = await import("./seal");
    const odd = await seal({ at: "A" }, "session", secrets, 60, NOW);
    expect(await unsealSessionData(odd, secrets, NOW)).toBeNull();
  });

  it("decides when to refresh: within the margin before the access token expires", () => {
    const data = { at: "A", rt: "R", ate: nowSec + 3600 };
    expect(needsRefresh(data, NOW)).toBe(false);
    expect(needsRefresh({ ...data, ate: nowSec + REFRESH_MARGIN_SECONDS + 1 }, NOW)).toBe(false);
    expect(needsRefresh({ ...data, ate: nowSec + REFRESH_MARGIN_SECONDS }, NOW)).toBe(true);
    expect(needsRefresh({ ...data, ate: nowSec - 5 }, NOW)).toBe(true);
  });

  it("keeps a realistic token pair far below the 4 KB cookie limit (UI-SEC-2)", async () => {
    // ASP.NET Core bearer tokens are Data Protection tickets: opaque base64url strings of a few hundred characters
    // (measured sizes are in docs/DEVELOPMENT_PLAN.md, A8). Use generous sizes here.
    const at = "C".repeat(900);
    const rt = "D".repeat(700);
    const token = await sealSessionData({ at, rt, ate: nowSec + 3600 }, secrets, NOW);
    expect(token.length).toBeLessThan(MAX_COOKIE_VALUE_BYTES);
    expect(token.length).toBeLessThan(2400);
  });

  it("fails loudly instead of letting the browser drop an oversized cookie", async () => {
    await expect(
      sealSessionData({ at: "x".repeat(3000), rt: "y".repeat(1000), ate: nowSec }, secrets, NOW),
    ).rejects.toThrow(/too large/);
  });
});
