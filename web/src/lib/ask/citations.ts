// Inline citation markers `[n]` in an answer become links to the matching citation (UI-QA-3, QA-8). A remark plugin,
// so markers inside code spans, code blocks and existing links are left alone, and markers without a citation stay plain text.
interface MdNode {
  type: string;
  value?: string;
  url?: string;
  children?: MdNode[];
  data?: Record<string, unknown>;
}

const SKIP = new Set(["code", "inlineCode", "link", "linkReference", "definition", "html"]);
const MARKER = /\[(\d{1,3})\]/g;

export const citationHref = (prefix: string, index: number) => `#${prefix}cite-${index}`;

function split(node: MdNode, valid: ReadonlySet<number>, prefix: string): MdNode[] {
  const text = node.value ?? "";
  const out: MdNode[] = [];
  let last = 0;
  for (const m of text.matchAll(MARKER)) {
    const index = Number(m[1]);
    if (!valid.has(index)) continue;
    if (m.index > last) out.push({ type: "text", value: text.slice(last, m.index) });
    out.push({
      type: "link",
      url: citationHref(prefix, index),
      children: [{ type: "text", value: `[${index}]` }],
    });
    last = m.index + m[0].length;
  }
  if (out.length === 0) return [node];
  if (last < text.length) out.push({ type: "text", value: text.slice(last) });
  return out;
}

function walk(node: MdNode, valid: ReadonlySet<number>, prefix: string) {
  if (!node.children || SKIP.has(node.type)) return;
  node.children = node.children.flatMap((child) => {
    if (child.type === "text") return split(child, valid, prefix);
    walk(child, valid, prefix);
    return [child];
  });
}

/** `remarkPlugins={[[remarkCitations, { indexes: [1, 2], prefix: "a1-" }]]}` */
export function remarkCitations(options: { indexes: readonly number[]; prefix: string }) {
  const valid = new Set(options.indexes);
  return (tree: MdNode) => walk(tree, valid, options.prefix);
}
