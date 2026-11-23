import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/test-utils";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));

import { ProblemAlert } from "./problem-alert";

afterEach(() => vi.useRealTimers());

describe("ProblemAlert", () => {
  it("shows the message for the code with the next step as a link (demo quota with the reset time)", () => {
    renderWithIntl(
      <ProblemAlert
        problem={{ status: 429, title: "x", code: "demo_quota_exceeded" }}
        resetsAt="2026-11-24T00:00:00Z"
      />,
    );
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("You have used today's demo questions. They reset at");
    expect(alert).toHaveTextContent("Add your own API key to keep going now.");
    expect(screen.getByRole("link", { name: "Add an API key" })).toHaveAttribute(
      "href",
      "/settings",
    );
  });

  it("falls back to wording without a time when none is known", () => {
    renderWithIntl(
      <ProblemAlert problem={{ status: 429, title: "x", code: "demo_quota_exceeded" }} />,
    );
    expect(screen.getByRole("alert")).not.toHaveTextContent("reset at");
  });

  it("counts a retry wait down before the retry button works", async () => {
    vi.useFakeTimers();
    const onRetry = vi.fn();
    renderWithIntl(
      <ProblemAlert
        problem={{ status: 429, title: "x", code: "demo_rate_limited", retryAfter: 3 }}
        onRetry={onRetry}
      />,
    );
    const button = screen.getByRole("button", { name: "Try again in 3 seconds" });
    expect(button).toBeDisabled();
    await act(async () => void (await vi.advanceTimersByTimeAsync(3000)));
    const ready = screen.getByRole("button", { name: "Try again" });
    expect(ready).toBeEnabled();
    vi.useRealTimers();
    await userEvent.click(ready);
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it("speaks Arabic with the Arabic plural for the wait", () => {
    renderWithIntl(<ProblemAlert problem={{ status: 429, title: "x", retryAfter: 2 }} />, "ar");
    expect(screen.getByRole("alert")).toHaveTextContent("بعد ثانيتين");
  });
});
