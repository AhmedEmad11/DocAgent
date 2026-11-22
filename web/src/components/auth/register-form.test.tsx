import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/test-utils";

const registerAction = vi.fn();
vi.mock("@/lib/auth/actions", () => ({
  registerAction: (...a: unknown[]) => registerAction(...a),
}));

vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={`/en${href}`} {...p}>
      {children}
    </a>
  ),
}));

import { RegisterForm } from "./register-form";

beforeEach(() => {
  registerAction.mockReset();
});

describe("RegisterForm (UI-AUTH-1)", () => {
  it("shows the rules live and lists each broken rule next to the field on submit", async () => {
    renderWithIntl(<RegisterForm />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "abc");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByText("The password needs an uppercase letter.")).toBeInTheDocument();
    expect(screen.getByText("The password needs a digit.")).toBeInTheDocument();
    expect(
      screen.getByText("The password needs a symbol, for example ! or #."),
    ).toBeInTheDocument();
    expect(registerAction).not.toHaveBeenCalled();
  });

  it("shows server-side rule violations per rule next to the password field", async () => {
    registerAction.mockResolvedValue({ ok: false, fieldErrors: { password: ["rule_unique"] } });
    renderWithIntl(<RegisterForm />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "Example-Only-1");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(
      await screen.findByText("The password needs more different characters."),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toHaveAttribute("aria-invalid", "true");
  });

  it("a duplicate account gets the generic message with a sign-in link", async () => {
    registerAction.mockResolvedValue({ ok: false, formError: "registerFailed" });
    renderWithIntl(<RegisterForm />);
    await userEvent.type(screen.getByLabelText("Email"), "a@example.com");
    await userEvent.type(screen.getByLabelText("Password"), "Example-Only-1");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "We could not create the account with these details.",
    );
    expect(screen.getByRole("link", { name: "Already registered? Sign in." })).toHaveAttribute(
      "href",
      "/en/sign-in",
    );
  });
});
