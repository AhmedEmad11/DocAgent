import { screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { LocaleSwitcher } from "@/components/locale-switcher";
import { renderWithIntl } from "@/test-utils";

vi.mock("@/i18n/navigation", () => ({
  usePathname: () => "/privacy",
  Link: ({
    href,
    locale,
    children,
    ...rest
  }: {
    href: string;
    locale: string;
    children: React.ReactNode;
  }) => (
    <a href={`/${locale}${href}`} {...rest}>
      {children}
    </a>
  ),
}));

describe("LocaleSwitcher", () => {
  it("links to the Arabic version of the current page from English", () => {
    renderWithIntl(<LocaleSwitcher />, "en");
    const link = screen.getByRole("link", { name: /Switch language to العربية/ });
    expect(link).toHaveAttribute("href", "/ar/privacy");
    expect(link).toHaveAttribute("lang", "ar");
  });

  it("links to the English version from Arabic", () => {
    renderWithIntl(<LocaleSwitcher />, "ar");
    const link = screen.getByRole("link", { name: /English/ });
    expect(link).toHaveAttribute("href", "/en/privacy");
    expect(link).toHaveAttribute("lang", "en");
  });
});
