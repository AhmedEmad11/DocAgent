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

const here = dirname(fileURLToPath(import.meta.url));
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const dir = join(here, "..", "messages");
  const catalogs = {
    en: JSON.parse(readFileSync(join(dir, "en.json"), "utf8")),
    ar: JSON.parse(readFileSync(join(dir, "ar.json"), "utf8")),
  };
  const problems = findMismatches(catalogs);
  if (problems.length > 0) {
    console.error(problems.join("\n"));
    process.exit(1);
  }
  console.log("i18n catalogs are in sync");
}
