import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/test-utils";

vi.mock("@/lib/auth/actions", () => ({ deleteAccountAction: vi.fn() }));
vi.mock("@/i18n/navigation", () => ({
  Link: ({ href, children, ...p }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...p}>
      {children}
    </a>
  ),
}));

import { DeleteAccount, emailMatches } from "./delete-account";

describe("emailMatches", () => {
  it("ignores case and surrounding spaces, nothing else", () => {
    expect(emailMatches("  Ana@Example.COM ", "ana@example.com")).toBe(true);
    expect(emailMatches("ana@example.co", "ana@example.com")).toBe(false);
    expect(emailMatches("", "ana@example.com")).toBe(false);
  });
});

describe("DeleteAccount (UI-ACC-3)", () => {
  const open = async () => userEvent.click(screen.getByTestId("delete-account"));

  it("lists what is removed and keeps the confirm button off until the email is typed", async () => {
    renderWithIntl(<DeleteAccount email="ana@example.com" action={vi.fn()} onDone={vi.fn()} />);
    await open();
    const dialog = screen.getByRole("dialog", { hidden: true });
    for (const text of [
      "all your documents",
      "their text chunks and embeddings",
      "your saved API key",
      "your demo usage",
      "This cannot be undone.",
    ])
      expect(dialog).toHaveTextContent(text);
    const confirm = within(screen.getByRole("dialog", { hidden: true })).getByRole("button", {
      name: "Delete my account",
      hidden: true,
    });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByTestId("delete-email"), "wrong@example.com");
    expect(confirm).toBeDisabled();
    await userEvent.clear(screen.getByTestId("delete-email"));
    await userEvent.type(screen.getByTestId("delete-email"), "ANA@example.com");
    expect(confirm).toBeEnabled();
  });

  it("deletes and goes to the goodbye page; cancel deletes nothing", async () => {
    const action = vi.fn(async () => ({ ok: true as const, next: "/en/goodbye" }));
    const onDone = vi.fn();
    renderWithIntl(<DeleteAccount email="ana@example.com" action={action} onDone={onDone} />);
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Cancel", hidden: true }));
    expect(action).not.toHaveBeenCalled();

    await open();
    await userEvent.type(screen.getByTestId("delete-email"), "ana@example.com");
    await userEvent.click(
      within(screen.getByRole("dialog", { hidden: true })).getByRole("button", {
        name: "Delete my account",
        hidden: true,
      }),
    );
    await waitFor(() => expect(onDone).toHaveBeenCalledWith("/en/goodbye"));
    expect(action).toHaveBeenCalledWith({ locale: "en" });
  });

  it("shows the failure and keeps the dialog open", async () => {
    const action = vi.fn(async () => ({ ok: false as const, formError: "server" }));
    const onDone = vi.fn();
    renderWithIntl(<DeleteAccount email="ana@example.com" action={action} onDone={onDone} />);
    await open();
    await userEvent.type(screen.getByTestId("delete-email"), "ana@example.com");
    await userEvent.click(
      within(screen.getByRole("dialog", { hidden: true })).getByRole("button", {
        name: "Delete my account",
        hidden: true,
      }),
    );
    expect(await screen.findByRole("alert", { hidden: true })).toHaveTextContent(
      "Something went wrong on our side.",
    );
    expect(onDone).not.toHaveBeenCalled();
  });

  it("in Arabic the email stays left to right inside the sentence", async () => {
    renderWithIntl(
      <DeleteAccount email="ana@example.com" action={vi.fn()} onDone={vi.fn()} />,
      "ar",
    );
    await userEvent.click(screen.getByTestId("delete-account"));
    const bdi = screen.getByRole("dialog", { hidden: true }).querySelector("label bdi");
    expect(bdi).toHaveAttribute("dir", "ltr");
    expect(bdi).toHaveTextContent("ana@example.com");
  });
});
