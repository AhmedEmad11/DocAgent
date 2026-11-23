// Fails when a message key exists in one locale catalog but not in another (UI-I18N-1).
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

export function flattenKeys(obj, prefix = "") {
  return Object.entries(obj).flatMap(([k, v]) =>
    v !== null && typeof v === "object" ? flattenKeys(v, `${prefix}${k}.`) : [`${prefix}${k}`],
  );
}

export function findMismatches(catalogs) {
  const names = Object.keys(catalogs);
  const all = new Set(names.flatMap((n) => flattenKeys(catalogs[n])));
  const problems = [];
  for (const n of names) {
    const have = new Set(flattenKeys(catalogs[n]));
    for (const key of all) if (!have.has(key)) problems.push(`${n}: missing "${key}"`);
  }
  return problems;
}

/** ICU argument names (`{name`, `{name, plural, ...`) and rich-text tags (`<tag>`) used by a message. */
export function placeholders(message) {
  const args = [...message.matchAll(/\{\s*(\w+)\s*[,}]/g)].map((m) => m[1]);
  const tags = [...message.matchAll(/<(\w+)>/g)].map((m) => `<${m[1]}>`);
  return [...new Set([...args, ...tags])].sort();
}

/** A translation must use the same placeholders as the English source, or the UI would show a broken sentence. */
export function findPlaceholderMismatches(catalogs, source = "en") {
  const flat = (obj, prefix = "") =>
    Object.entries(obj).flatMap(([k, v]) =>
      v !== null && typeof v === "object" ? flat(v, `${prefix}${k}.`) : [[`${prefix}${k}`, v]],
    );
  const base = new Map(flat(catalogs[source]));
  const problems = [];
  for (const [name, catalog] of Object.entries(catalogs)) {
    if (name === source) continue;
    for (const [key, value] of flat(catalog)) {
      const expected = base.get(key);
      if (typeof expected !== "string" || typeof value !== "string") continue;
      const a = placeholders(expected).join(",");
      const b = placeholders(value).join(",");
      if (a !== b) problems.push(`${name}: "${key}" uses {${b}} but ${source} uses {${a}}`);
    }
  }
  return problems;
}

const here = dirname(fileURLToPath(import.meta.url));
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const dir = join(here, "..", "messages");
  const catalogs = {
    en: JSON.parse(readFileSync(join(dir, "en.json"), "utf8")),
    ar: JSON.parse(readFileSync(join(dir, "ar.json"), "utf8")),
  };
  const problems = [...findMismatches(catalogs), ...findPlaceholderMismatches(catalogs)];
  if (problems.length > 0) {
    console.error(problems.join("\n"));
    process.exit(1);
  }
  console.log("i18n catalogs are in sync");
}
