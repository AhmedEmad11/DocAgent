import { act, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { KNOWN_PRIVACY_NOTICE_EN } from "@/lib/demo/privacy";
import { renderWithIntl } from "@/test-utils";
import { routerMock } from "@/test/router-mock";

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));

import { DemoBannerView, type DemoState } from "./demo-banner-view";

const demo = (over: Partial<DemoState> = {}): DemoState => ({
  limit: 10,
  used: 3,
  remaining: 7,
  resetsAt: "2026-11-24T00:00:00Z",
  privacyNotice: KNOWN_PRIVACY_NOTICE_EN,
  ...over,
});

beforeEach(() => sessionStorage.clear());
afterEach(() => vi.useRealTimers());

describe("DemoBannerView (UI-DEMO-1 to 3)", () => {
  it("shows the live numbers from the API and a link to the key settings", () => {
    renderWithIntl(<DemoBannerView demo={demo()} />);
    expect(screen.getByTestId("demo-allowance")).toHaveTextContent(
      "Demo mode: 7 of 10 questions left today. Resets at",
    );
    expect(screen.getByRole("link", { name: "Add your own API key" })).toHaveAttribute(
      "href",
      "/settings",
    );
    expect(screen.getByRole("region", { name: "Demo mode" })).toBeInTheDocument();
    expect(screen.getByTestId("demo-allowance")).toHaveAttribute("role", "status"); // a change is announced politely
  });

  it("takes the numbers from the props, not from constants", () => {
    renderWithIntl(<DemoBannerView demo={demo({ limit: 25, used: 1, remaining: 24 })} />);
    expect(screen.getByTestId("demo-allowance")).toHaveTextContent("24 of 25 questions");
  });

  it("opens the privacy notice the first time in a browser session and keeps it closed afterwards, available on demand", () => {
    vi.useFakeTimers();
    const { unmount } = renderWithIntl(<DemoBannerView demo={demo()} />);
    const details = () => screen.getByTestId("demo-privacy") as HTMLDetailsElement;
    expect(details().open).toBe(true);
    expect(screen.getByTestId("demo-privacy-text")).toHaveTextContent(
      "sent to a free third-party language model",
    );
    act(() => void vi.advanceTimersByTime(3100)); // it stayed on screen: now it counts as seen
    unmount();

    renderWithIntl(<DemoBannerView demo={demo()} />);
    expect(details().open).toBe(false);
    expect(screen.getByText("Privacy notice")).toBeInTheDocument(); // the summary opens it on demand
  });

  it("a banner that disappears at once (the brief render on the sign-in page) does not use up the first view", () => {
    vi.useFakeTimers();
    const { unmount } = renderWithIntl(<DemoBannerView demo={demo()} />);
    act(() => void vi.advanceTimersByTime(200));
    unmount();
    renderWithIntl(<DemoBannerView demo={demo()} />);
    expect((screen.getByTestId("demo-privacy") as HTMLDetailsElement).open).toBe(true);
  });

  it("shows the notice in Arabic when it is the one the UI knows, and an unknown one as given in English", () => {
    const { unmount } = renderWithIntl(<DemoBannerView demo={demo()} />, "ar");
    expect(screen.getByTestId("demo-privacy-text")).toHaveTextContent("وضع التجربة");
    unmount();
    renderWithIntl(
      <DemoBannerView demo={demo({ privacyNotice: "A different notice from the API." })} />,
      "ar",
    );
    expect(screen.getByTestId("demo-privacy-text")).toHaveTextContent(
      "A different notice from the API.",
    );
    expect(screen.getByTestId("demo-privacy-text").querySelector("[dir=ltr]")).not.toBeNull();
  });

  it("at zero questions left it says so with the reset time and counts down", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-23T21:00:00Z"));
    renderWithIntl(<DemoBannerView demo={demo({ used: 10, remaining: 0 })} />);
    expect(screen.getByTestId("demo-allowance")).toHaveTextContent(
      "you have used all 10 questions for today",
    );
    await act(async () => void (await vi.advanceTimersByTimeAsync(30_000)));
    expect(screen.getByTestId("demo-allowance")).toHaveTextContent(/in 2h 5\dm|in 3h 0m|in 2h 59m/);
  });

  it("re-reads the allowance from the server when the reset time passes", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-23T23:59:40Z"));
    renderWithIntl(<DemoBannerView demo={demo({ used: 10, remaining: 0 })} />);
    expect(routerMock.refresh).not.toHaveBeenCalled();
    await act(async () => void (await vi.advanceTimersByTimeAsync(60_000)));
    expect(routerMock.refresh).toHaveBeenCalled();
  });

  it("does not run the countdown (or refresh) while there are questions left", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-11-23T23:59:40Z"));
    renderWithIntl(<DemoBannerView demo={demo()} />);
    await act(async () => void (await vi.advanceTimersByTimeAsync(120_000)));
    expect(routerMock.refresh).not.toHaveBeenCalled();
  });
});
