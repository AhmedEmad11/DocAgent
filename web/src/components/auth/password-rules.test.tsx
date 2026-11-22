import { screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/test-utils";
import { PasswordRules } from "./password-rules";

describe("PasswordRules (UI-AUTH-1)", () => {
  it("lists the rules before anything is typed, each with a text state", () => {
    renderWithIntl(<PasswordRules id="r" value="" />);
    const items = screen.getAllByRole("listitem");
    expect(items).toHaveLength(5);
    for (const li of items) expect(within(li).getByText("not met yet")).toBeInTheDocument();
  });

  it("marks the rules a password meets (text, not colour alone)", () => {
    renderWithIntl(<PasswordRules id="r" value="Abcdef1!" />);
    for (const li of screen.getAllByRole("listitem"))
      expect(within(li).getByText("met")).toBeInTheDocument();
  });

  it("works in Arabic", () => {
    renderWithIntl(<PasswordRules id="r" value="abc" />, "ar");
    expect(screen.getByText("تحتاج كلمة المرور إلى:")).toBeInTheDocument();
    expect(screen.getAllByText("غير مستوفى بعد").length).toBeGreaterThan(0);
  });
});
