import { expect, type Page } from "@playwright/test";

export const MOCK_API = "http://127.0.0.1:4010";
export const PASSWORD = "Example-Only-1";

export const uniqueEmail = (prefix = "user") =>
  `${prefix}-${Math.random().toString(36).slice(2, 10)}@example.com`;

export async function mock<T = unknown>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${MOCK_API}${path}`, {
    method: body === undefined ? "GET" : "POST",
    headers: { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return (await res.json()) as T;
}

export interface MockState {
  refreshCalls: number;
  refreshByEmail: Record<string, number>;
  loginCalls: number;
  lastClientIp: string | null;
  loginIpByEmail: Record<string, string | null>;
  untrustedCalls: number;
  externalOutcome: string;
  lastExchangeCode: string | null;
  ticketsIssued: number;
  uploads: { name: string; size: number; auth: "ticket" | "bearer" }[];
  listCalls: number;
}
export const mockState = () => mock<MockState>("/__test/state");
export const mailbox = (email: string) =>
  mock<{ confirm?: { userId: string; code: string }; resetCode?: string }>(
    `/__test/mailbox?email=${encodeURIComponent(email)}`,
  );

/** Signs in through the form (English UI) and waits for the signed-in page. */
export async function signIn(page: Page, email: string, password = PASSWORD, path = "/en/sign-in") {
  await page.goto(path);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

export async function signInAndLand(page: Page, email: string) {
  await signIn(page, email);
  await expect(page).toHaveURL(/\/en\/documents$/);
  await expect(page.getByTestId("account-email")).toHaveText(email);
}

/** A fresh confirmed account on the mock, so token expiry/revocation in one test cannot disturb another. */
export async function newUser(prefix = "iso") {
  const email = uniqueEmail(prefix);
  await mock("/__test/user", { email });
  return email;
}

export interface SeedDocument {
  fileName: string;
  status?: "Pending" | "Processing" | "Ready" | "Failed";
  sizeBytes?: number;
  chunkCount?: number;
  failureReason?: string | null;
  extractionNotice?: string | null;
}

/** Replaces the user's documents on the mock; a seeded status stays as given (the list does not advance it). */
export const seedDocuments = (
  email: string,
  documents: SeedDocument[],
  extra: { hasKey?: boolean; chunksUsed?: number } = {},
) => mock("/__test/documents", { email, documents, ...extra });

export const nextUploadError = (status: number, code?: string, retryAfter?: number) =>
  mock("/__test/next-upload-error", { status, code, retryAfter });
