import { defineConfig, devices } from "@playwright/test";

const webPort = 3100;
const mockApiPort = 4010;
const baseURL = `http://127.0.0.1:${webPort}`;
// Placeholders for the e2e run only (never real secrets).
const e2eEnv = {
  SESSION_SECRET: "example-only-session-secret-0123456789abcdef",
  BFF_SHARED_SECRET: "example-only-bff-secret",
};

// Runs against a production build (`npm run build` first): CSP and headers differ in dev.
export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.spec.ts",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : "list",
  use: { baseURL, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: [
    {
      command: "node e2e/mock-api/server.mjs",
      url: `http://127.0.0.1:${mockApiPort}/health`,
      env: {
        MOCK_API_PORT: String(mockApiPort),
        MOCK_BFF_SECRET: e2eEnv.BFF_SHARED_SECRET,
        MOCK_ALLOWED_RETURN: `${baseURL}/api/auth/external/callback`,
        MOCK_UI_ORIGIN: baseURL,
      },
      reuseExistingServer: !process.env.CI,
    },
    {
      command: `npx next start --hostname 127.0.0.1 --port ${webPort}`,
      url: `${baseURL}/en`,
      env: {
        DOCAGENT_API_URL: `http://127.0.0.1:${mockApiPort}`,
        NEXT_PUBLIC_SITE_URL: baseURL,
        ...e2eEnv,
      },
      reuseExistingServer: !process.env.CI,
    },
  ],
});
