import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { ThemeToggle } from "@/components/theme-toggle";
import { renderWithIntl } from "@/test-utils";

describe("ThemeToggle", () => {
  it("cycles system -> light -> dark -> system, applies the class and persists", async () => {
    const user = userEvent.setup();
    renderWithIntl(<ThemeToggle />);
    const button = screen.getByRole("button");
    expect(button).toHaveAttribute("data-theme-mode", "system");
    expect(document.documentElement).not.toHaveClass("dark");

    await user.click(button);
    expect(button).toHaveAttribute("data-theme-mode", "light");
    expect(document.documentElement).not.toHaveClass("dark");
    expect(localStorage.getItem("theme")).toBe("light");

    await user.click(button);
    expect(button).toHaveAttribute("data-theme-mode", "dark");
    expect(document.documentElement).toHaveClass("dark");

    await user.click(button);
    expect(button).toHaveAttribute("data-theme-mode", "system");
    expect(document.documentElement).not.toHaveClass("dark");
  });

  it("restores the stored choice", async () => {
    localStorage.setItem("theme", "dark");
    renderWithIntl(<ThemeToggle />);
    expect(await screen.findByRole("button")).toHaveAttribute("data-theme-mode", "dark");
  });

  it("has a translated accessible name in Arabic", () => {
    renderWithIntl(<ThemeToggle />, "ar");
    expect(screen.getByRole("button", { name: /المظهر: النظام/ })).toBeInTheDocument();
  });
});
