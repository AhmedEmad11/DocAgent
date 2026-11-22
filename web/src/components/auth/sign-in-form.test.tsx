import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/test-utils";

const signInAction = vi.fn();
vi.mock("@/lib/auth/actions", () => ({ signInAction: (...a: unknown[]) => signInAction(...a) }));
vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));

import { SignInForm } from "./sign-in-form";

beforeEach(() => {
  signInAction.mockReset();
});

describe("SignInForm", () => {
  it("validates before calling the server action", async () => {
    renderWithIntl(<SignInForm />);
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("Enter your email address.")).toBeInTheDocument();
    expect(screen.getByText("Enter a password.")).toBeInTheDocument();
    expect(screen.getByLabelText("Email")).toHaveAttribute("aria-invalid", "true");
    expect(signInAction).not.toHaveBeenCalled();
  });

  it("sends credentials, locale and next to the action and shows the returned message key", async () => {
    signInAction.mockResolvedValue({ ok: false, formError: "badCredentials" });
    renderWithIntl(<SignInForm next="/en/documents" />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "Example-Wrong-3");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Email or password is incorrect, or the email is not confirmed yet.",
    );
    expect(signInAction).toHaveBeenCalledWith({
      email: "a@example.com",
      password: "Example-Wrong-3",
      locale: "en",
      next: "/en/documents",
    });
  });

  it("shows a rate-limit message with the seconds", async () => {
    signInAction.mockResolvedValue({ ok: false, formError: "rateLimitedAfter", retryAfter: 7 });
    renderWithIntl(<SignInForm />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "x");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Try again in 7 seconds.");
  });

  it("keeps the typed email after a failure and re-enables the button", async () => {
    signInAction.mockResolvedValue({ ok: false, formError: "server" });
    renderWithIntl(<SignInForm />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "x");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await screen.findByRole("alert");
    expect(screen.getByLabelText("Email")).toHaveValue("a@example.com");
    await waitFor(() => expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled());
  });

  it("falls back to a network message when the action throws", async () => {
    signInAction.mockRejectedValue(new Error("boom"));
    renderWithIntl(<SignInForm />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "x");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Cannot reach the service");
  });

  it("renders in Arabic with a right-to-left friendly, LTR email field", () => {
    renderWithIntl(<SignInForm />, "ar");
    expect(screen.getByLabelText("البريد الإلكتروني")).toHaveAttribute("dir", "ltr");
    expect(screen.getByRole("button", { name: "تسجيل الدخول" })).toBeInTheDocument();
  });
});
