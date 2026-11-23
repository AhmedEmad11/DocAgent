import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MarkdownText, safeUrl } from "./markdown-text";

const html = (text: string, indexes: number[] = [1, 2]) => {
  const { container } = render(
    <MarkdownText text={text} citationIndexes={indexes} idPrefix="t-" />,
  );
  return container;
};

describe("MarkdownText (UI-SEC-8, R9.11)", () => {
  it("renders formatting", () => {
    const c = html("**bold** and *em*\n\n- one\n- two");
    expect(c.querySelector("strong")?.textContent).toBe("bold");
    expect(c.querySelectorAll("li")).toHaveLength(2);
  });

  it("never renders raw HTML, scripts, images, iframes, styles or handlers", () => {
    const c = html(
      '<script>window.__x=1</script><img src=x onerror="window.__x=1"><iframe src="https://evil.test"></iframe><style>*{display:none}</style><div onclick="window.__x=1">div</div>\n\n<b onmouseover="1">raw</b> text',
    );
    expect(c.querySelector("script, img, iframe, style, [onclick], [onmouseover]")).toBeNull();
    expect((window as unknown as { __x?: number }).__x).toBeUndefined();
  });

  it("drops images written in Markdown too", () => {
    const c = html("![alt](https://evil.test/x.png)");
    expect(c.querySelector("img")).toBeNull();
  });

  it("only allows http(s), relative and in-page link targets", () => {
    const c = html(
      "[js](javascript:alert(1)) [data](data:text/html;base64,AAAA) [mail](mailto:a@b.c) [ok](https://example.com/x) [rel](/documents) [proto](//evil.test)",
    );
    const hrefs = [...c.querySelectorAll("a")].map((a) => a.getAttribute("href"));
    expect(hrefs).toEqual(["https://example.com/x", "/documents"]);
  });

  it("opens external links safely", () => {
    const c = html("[ok](https://example.com)");
    const a = c.querySelector("a")!;
    expect(a.getAttribute("rel")).toBe("noopener noreferrer");
    expect(a.getAttribute("target")).toBe("_blank");
  });

  it("links [n] markers to their citations and leaves others as text", () => {
    const c = html("Claim [1] and claim [2] but not [9].");
    const markers = [...c.querySelectorAll("[data-testid=citation-marker]")];
    expect(markers.map((m) => m.getAttribute("href"))).toEqual(["#t-cite-1", "#t-cite-2"]);
    expect(markers.every((m) => !m.hasAttribute("target"))).toBe(true);
    expect(c.textContent).toContain("[9]");
  });

  it("gives every block its own direction so Arabic and English paragraphs mix correctly (UI-I18N-6)", () => {
    const c = html("English paragraph\n\nفقرة عربية");
    const dirs = [...c.querySelectorAll("p")].map((p) => p.getAttribute("dir"));
    expect(dirs).toEqual(["auto", "auto"]);
  });

  it("renders headings as paragraphs so the page keeps one h1", () => {
    html("# Title\n\n## Sub");
    expect(screen.queryAllByRole("heading")).toHaveLength(0);
  });
});

describe("safeUrl", () => {
  it("accepts and rejects the right schemes", () => {
    expect(safeUrl("https://a.b")).toBe("https://a.b");
    expect(safeUrl("HTTP://a.b")).toBe("HTTP://a.b");
    expect(safeUrl("#cite-1")).toBe("#cite-1");
    expect(safeUrl("/x")).toBe("/x");
    for (const bad of [
      "javascript:x",
      " javascript:x",
      "data:x",
      "//evil",
      "mailto:a@b",
      "ftp://a",
      "vbscript:x",
      "",
    ])
      expect(safeUrl(bad)).toBe("");
  });
});
