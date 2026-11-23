import { describe, expect, it } from "vitest";
import { remarkCitations } from "./citations";

type N = { type: string; value?: string; url?: string; children?: N[] };
const text = (value: string): N => ({ type: "text", value });
const run = (tree: N, indexes: number[]) => {
  remarkCitations({ indexes, prefix: "p-" })(tree);
  return tree;
};
const root = (...children: N[]): N => ({
  type: "root",
  children: [{ type: "paragraph", children }, ...[]].slice(0, 1),
});

describe("citation markers", () => {
  it("turns [n] with a matching citation into an in-page link and keeps the text around it", () => {
    const tree = run(root(text("Rent is due on the 1st [1], late fees apply [2].")), [1, 2]);
    const parts = tree.children![0]!.children!;
    expect(parts.map((p) => p.type)).toEqual(["text", "link", "text", "link", "text"]);
    expect(parts[1]).toMatchObject({ url: "#p-cite-1", children: [{ value: "[1]" }] });
    expect(parts[3]).toMatchObject({ url: "#p-cite-2" });
    expect(parts[4]!.value).toBe(".");
  });

  it("leaves markers without a citation, and numbers that are not markers, as text", () => {
    const tree = run(root(text("See [3] and [x] and 5.")), [1]);
    expect(tree.children![0]!.children).toEqual([text("See [3] and [x] and 5.")]);
  });

  it("leaves code, inline code and existing links alone", () => {
    const tree = run(
      {
        type: "root",
        children: [
          { type: "code", value: "a[1]" },
          {
            type: "paragraph",
            children: [
              { type: "inlineCode", value: "b[1]" },
              { type: "link", url: "https://x", children: [text("c [1]")] },
            ],
          },
        ],
      },
      [1],
    );
    expect(JSON.stringify(tree)).not.toContain("#p-cite-1");
  });

  it("works inside emphasis and lists", () => {
    const tree = run(
      {
        type: "root",
        children: [
          {
            type: "list",
            children: [
              {
                type: "listItem",
                children: [
                  {
                    type: "paragraph",
                    children: [{ type: "emphasis", children: [text("a [1]")] }],
                  },
                ],
              },
            ],
          },
        ],
      },
      [1],
    );
    expect(JSON.stringify(tree)).toContain("#p-cite-1");
  });
});
