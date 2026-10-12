#!/usr/bin/env node
// Validates docs/ and maintains the generated feature-status index in docs/root.md.
//
//   node scripts/docs-index.mjs          print the index block and validation results
//   node scripts/docs-index.mjs --fix    upgrade legacy header lines to frontmatter, rewrite the index block
//   node scripts/docs-index.mjs --check  exit 1 if the block is stale or any document is invalid
//
// The format is specified in AGENTS.md ("Documentation"). The canonical copy of this script lives in
// alex-de-haas/docker-host; other repositories carry a byte-identical copy, so change it there first.
//
// Workflow docs live in docs/features/<name>/{feature.md,plan.md}, plus docs/vision.md and the
// docs/reviews/ archive. Any other Markdown under docs/ is rejected, except the top-level files other
// tooling owns (TOOLING_FILES).

import { existsSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const docsDir = join(repoRoot, "docs");
const rootMd = join(docsDir, "root.md");
const BEGIN = "<!-- docs-index:begin -->";
const END = "<!-- docs-index:end -->";
const PLAN_STATUSES = ["In Progress", "Ready", "Blocked", "Draft", "On Hold"];
const SUMMARY_MAX = 200;
// Top-level docs/ files that belong to other tooling: the Marketplace store page and an app's agent
// skill file. They are not workflow documents and are not validated.
const TOOLING_FILES = new Set(["store.md", "agent.md"]);
const WORKFLOW_DIRS = new Set(["features", "reviews"]);

// Required and optional frontmatter keys per document kind. Anything else is rejected so that a
// misspelled key fails loudly instead of silently dropping a field.
const SCHEMAS = {
  plan: { required: ["status", "created", "updated", "summary"], optional: ["components"] },
  feature: { required: ["created", "updated", "summary"], optional: ["components"] },
  vision: { required: ["created", "updated"], optional: [] },
};

const mode = process.argv.includes("--fix") ? "fix" : process.argv.includes("--check") ? "check" : "print";
const errors = [];
const rel = (p) => p.slice(repoRoot.length + 1).split(sep).join("/");

// A NUL byte turns a document into "data" as far as the toolchain is concerned: grep goes silent on
// it, which makes the file look empty rather than broken and hides every later check. One reached
// main on 2026-08-15 inside a hash-separator spec, unnoticed through two reviews for exactly that
// reason. Cheap to detect, so detect it.
function checkPrintable(file, text) {
  const index = text.indexOf("\u0000");
  if (index !== -1) {
    const line = text.slice(0, index).split("\n").length;
    errors.push(`${rel(file)}:${line}: contains a NUL byte, which makes the file binary to grep and diff`);
  }
}

// ---------------------------------------------------------------------------------------------
// Frontmatter: a strict subset of YAML, so any YAML parser reads exactly what this script reads.
// One `key: value` per line; values are plain scalars, double-quoted strings, or flow lists.

const PLAIN_FIRST = new Set([..."-?:,[]{}#&*!|>'\"%@`"]);

function parseScalar(raw, inList) {
  if (raw.startsWith('"')) {
    if (raw.length < 2 || !raw.endsWith('"') || !/^(?:[^"\\]|\\["\\])*$/.test(raw.slice(1, -1)))
      return { error: `malformed double-quoted string ${raw}; only \\" and \\\\ escapes are allowed` };
    return { value: raw.slice(1, -1).replace(/\\(["\\])/g, "$1") };
  }
  if (raw !== raw.trim() || raw === "") return { error: "empty value or surrounding whitespace" };
  if (PLAIN_FIRST.has(raw[0])) return { error: `value ${raw} starts with "${raw[0]}"; wrap it in double quotes` };
  if (raw.includes(": ") || raw.includes(" #") || raw.endsWith(":"))
    return { error: `value ${raw} contains ": " or " #"; wrap it in double quotes` };
  if (inList && /[,[\]{}]/.test(raw)) return { error: `list item ${raw} contains a flow indicator` };
  return { value: raw };
}

function parseValue(raw) {
  if (!raw.startsWith("[")) return parseScalar(raw, false);
  if (!raw.endsWith("]")) return { error: `unterminated list ${raw}` };
  const inner = raw.slice(1, -1).trim();
  if (inner === "") return { error: "empty list; omit the key instead" };
  const items = [];
  for (const part of inner.split(",")) {
    const item = parseScalar(part.trim(), true);
    if (item.error) return item;
    items.push(item.value);
  }
  return { value: items };
}

// Returns { data, bodyStart } for a document that starts with a frontmatter block, or null.
function readFrontmatter(file, lines) {
  if (lines[0] !== "---") return null;
  const close = lines.indexOf("---", 1);
  if (close === -1) {
    errors.push(`${rel(file)}:1: frontmatter is not closed by a "---" line`);
    return { data: {}, bodyStart: lines.length };
  }
  const data = {};
  for (let i = 1; i < close; i++) {
    const at = `${rel(file)}:${i + 1}`;
    const match = lines[i].match(/^([a-z][a-z0-9-]*):(?: (.*))?$/);
    if (!match) {
      errors.push(`${at}: frontmatter line is not "key: value" (no nesting, comments or multi-line values)`);
      continue;
    }
    const [, key, raw = ""] = match;
    if (key in data) errors.push(`${at}: duplicate key "${key}"`);
    const parsed = parseValue(raw);
    if (parsed.error) errors.push(`${at}: ${key}: ${parsed.error}`);
    else data[key] = parsed.value;
  }
  return { data, bodyStart: close + 1 };
}

// Status/Created/Updated lines before the first "## " heading: the pre-frontmatter header format.
function legacyHeaderLines(lines, from) {
  const found = [];
  for (let i = from; i < lines.length && !lines[i].startsWith("## "); i++) {
    const match = lines[i].match(/^(Status|Created|Updated):\s*(.+?)\s*$/);
    if (match) found.push({ index: i, key: match[1].toLowerCase(), value: match[2] });
  }
  return found;
}

// --fix: move legacy header lines into a frontmatter block. Values are copied verbatim; validation
// then reports anything the old format let through, plus the keys it never had (summary).
function upgradeHeader(file, text) {
  const lines = text.split("\n");
  const found = legacyHeaderLines(lines, 0);
  if (lines[0] === "---" || found.length === 0) return text;
  const drop = new Set(found.map((f) => f.index));
  // Remove the blank line the header block leaves behind, so the H1 is followed by one blank line.
  const last = found[found.length - 1].index;
  if (lines[last + 1] === "" && (found[0].index === 0 || lines[found[0].index - 1] === "")) drop.add(last + 1);
  const order = ["status", "created", "updated"];
  const front = ["---", ...found.sort((a, b) => order.indexOf(a.key) - order.indexOf(b.key)).map((f) => `${f.key}: ${f.value}`), "---"];
  const body = lines.filter((_, i) => !drop.has(i));
  while (body[0] === "") body.shift();
  console.log(`${rel(file)}: header lines moved into frontmatter`);
  return [...front, "", ...body].join("\n");
}

// ---------------------------------------------------------------------------------------------
// Validation of one document.

function validDate(value) {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const date = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(date.getTime()) && date.toISOString().startsWith(value);
}

function validateFrontmatter(file, kind, data) {
  const at = rel(file);
  const schema = SCHEMAS[kind];
  const allowed = [...schema.required, ...schema.optional];
  for (const key of Object.keys(data)) {
    if (allowed.includes(key)) continue;
    if (kind === "feature" && key === "status") errors.push(`${at}: feature.md must not carry status (reality has no status)`);
    else errors.push(`${at}: unknown frontmatter key "${key}" (allowed: ${allowed.join(", ")})`);
  }
  for (const key of schema.required) if (!(key in data)) errors.push(`${at}: missing frontmatter key "${key}"`);

  if ("status" in data && kind === "plan" && !PLAN_STATUSES.includes(data.status))
    errors.push(`${at}: status "${data.status}" is not one of: ${PLAN_STATUSES.join(", ")}`);
  for (const key of ["created", "updated"])
    if (key in data && !validDate(data[key])) errors.push(`${at}: ${key}: "${data[key]}" is not a YYYY-MM-DD date`);
  if (validDate(data.created) && validDate(data.updated) && data.updated < data.created)
    errors.push(`${at}: updated ${data.updated} is earlier than created ${data.created}`);

  if ("summary" in data) {
    const summary = data.summary;
    if (typeof summary !== "string") errors.push(`${at}: summary must be a single sentence, not a list`);
    else if (summary.length > SUMMARY_MAX) errors.push(`${at}: summary is ${summary.length} characters; the limit is ${SUMMARY_MAX}`);
    // Markdown that would leak into the index: code, links, emphasis (underscores only at a word
    // edge, so identifiers like HOSTY_PORT_KEY stay legal), strikethrough and inline HTML.
    else if (/[`[\]*~]|(^|[\s(])_|_($|[\s).,;:!?])|<[A-Za-z/!]/.test(summary))
      errors.push(`${at}: summary must be plain text (no code, links, emphasis or HTML)`);
    else if (/[.!?]["')]?\s+\S/.test(summary)) errors.push(`${at}: summary must be a single sentence`);
  }

  if ("components" in data) {
    const components = Array.isArray(data.components) ? data.components : [data.components];
    if (new Set(components).size !== components.length) errors.push(`${at}: components lists a directory twice`);
    for (const component of components) {
      const segments = component.split("/");
      if (!/^[A-Za-z0-9._/-]+$/.test(component) || segments.some((s) => s === "" || s === "." || s === ".."))
        errors.push(`${at}: component "${component}" is not a repository-relative directory like apps/core`);
      else if (!existsSync(join(repoRoot, component)) || !statSync(join(repoRoot, component)).isDirectory())
        errors.push(`${at}: component "${component}" is not a directory in this repository`);
    }
  }
}

// Lines outside fenced code blocks, with their 0-based index. As in CommonMark, a fence closes only on
// a bare run of the same character at least as long as the one that opened it, so a four-backtick
// fence can show a three-backtick example.
function proseLines(lines, from) {
  const out = [];
  let fence = null;
  for (let i = from; i < lines.length; i++) {
    if (fence === null) {
      const open = lines[i].match(/^\s*(`{3,}|~{3,})/);
      if (open) fence = open[1];
      else out.push({ index: i, line: lines[i] });
      continue;
    }
    const close = lines[i].match(/^\s*(`{3,}|~{3,})\s*$/);
    if (close && close[1][0] === fence[0] && close[1].length >= fence.length) fence = null;
  }
  return out;
}

function checkDeliverables(file, prose) {
  const at = (i) => `${rel(file)}:${i + 1}`;
  const ids = new Map();
  let section = null;
  let sections = 0;
  let done = 0;
  for (const { index, line } of prose) {
    if (/^## /.test(line)) {
      section = line.trim();
      if (section === "## Deliverables" && ++sections === 2) errors.push(`${at(index)}: a plan has one "## Deliverables" section`);
      continue;
    }
    if (/^#{1} /.test(line)) section = null;
    const box = line.match(/^(\s*)([-*+]) \[([ xX])\](.*)$/);
    if (!box) continue;
    const [, indent, bullet, mark, rest] = box;
    if (section !== "## Deliverables") {
      errors.push(`${at(index)}: checkbox outside "## Deliverables"; deliverables are the only checkboxes in a plan`);
      continue;
    }
    if (indent !== "") {
      errors.push(`${at(index)}: nested checkbox; each deliverable is a top-level item (use plain bullets for detail)`);
      continue;
    }
    if (bullet !== "-" || mark === "X") errors.push(`${at(index)}: write deliverables as "- [ ]" or "- [x]"`);
    const id = rest.match(/^ D([1-9]\d*)\. \S/);
    if (!id) {
      errors.push(`${at(index)}: deliverable without an ID; start it with "D<n>. " (IDs are never renumbered or reused)`);
      continue;
    }
    if (ids.has(id[1])) errors.push(`${at(index)}: duplicate deliverable ID D${id[1]} (first used on line ${ids.get(id[1]) + 1})`);
    ids.set(id[1], index);
    if (mark !== " ") done++;
  }
  if (sections === 0) errors.push(`${rel(file)}: a plan needs a "## Deliverables" section`);
  else if (ids.size === 0) errors.push(`${rel(file)}: a plan needs at least one deliverable, written as "- [ ] D1. Text"`);
  return { total: ids.size, done };
}

// Relative links must resolve. External URLs and pure anchors are not checked; neither is code.
function checkLinks(file, prose) {
  for (const { index, line } of prose) {
    const text = line.replace(/(`+)[^`]*?\1/g, "");
    const targets = [...text.matchAll(/\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g)].map((m) => m[1]);
    const definition = text.match(/^\s*\[[^\]]+\]:\s+<?([^\s>]+)>?/);
    if (definition) targets.push(definition[1]);
    for (const target of targets) {
      if (/^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith("#")) continue;
      let path = target.replace(/[#?].*$/, "");
      try {
        path = decodeURIComponent(path);
      } catch {
        // Keep the raw path; the existence check below reports it.
      }
      if (path.startsWith("/")) {
        errors.push(`${rel(file)}:${index + 1}: link "${target}" is absolute; use a relative path`);
        continue;
      }
      if (!existsSync(join(dirname(file), path))) errors.push(`${rel(file)}:${index + 1}: broken link "${target}"`);
    }
  }
}

function readDoc(file, kind) {
  let text = readFileSync(file, "utf8");
  checkPrintable(file, text);
  if (mode === "fix") {
    const upgraded = upgradeHeader(file, text);
    if (upgraded !== text) writeFileSync(file, (text = upgraded));
  }
  const lines = text.split("\n");
  const front = readFrontmatter(file, lines);
  let data = {};
  let bodyStart = 0;
  if (front) {
    ({ data, bodyStart } = front);
    if (legacyHeaderLines(lines, bodyStart).length)
      errors.push(`${rel(file)}: Status:/Created:/Updated: header lines alongside frontmatter; keep only the frontmatter`);
  } else {
    errors.push(`${rel(file)}: missing frontmatter${legacyHeaderLines(lines, 0).length ? " (run --fix to convert the header lines)" : ""}`);
  }
  validateFrontmatter(file, kind, data);

  const prose = proseLines(lines, bodyStart);
  const h1 = prose.find(({ line }) => line.trim() !== "");
  const title = h1?.line.match(/^# (.+?)\s*#*\s*$/)?.[1];
  if (!title) errors.push(`${rel(file)}: the first line after the frontmatter must be the "# Title" heading`);
  checkLinks(file, prose);
  if (kind === "feature") {
    const sections = prose.filter(({ line }) => /^## /.test(line));
    if (sections.at(-1)?.line.trim() !== "## Testing Expectations")
      errors.push(`${rel(file)}: feature.md must end with a "## Testing Expectations" section`);
  }
  const progress = kind === "plan" ? checkDeliverables(file, prose) : null;
  return { data, title: title ?? "Untitled", progress };
}

// ---------------------------------------------------------------------------------------------
// Walk docs/ and build the index.

const entries = [];
const statusCounts = new Map();

const featuresDir = join(docsDir, "features");
if (existsSync(featuresDir)) {
  for (const name of readdirSync(featuresDir).sort()) {
    const dir = join(featuresDir, name);
    if (statSync(dir).isDirectory()) {
      if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(name)) errors.push(`${rel(dir)}: feature folder names are kebab-case`);
      const featureMd = join(dir, "feature.md");
      const planMd = join(dir, "plan.md");
      for (const file of markdownUnder(dir))
        if (file !== featureMd && file !== planMd)
          errors.push(`${rel(file)}: a feature folder holds only feature.md and plan.md; move this content into one of them`);
      if (!existsSync(featureMd) && !existsSync(planMd)) {
        errors.push(`${rel(dir)}: contains neither feature.md nor plan.md`);
        continue;
      }
      const feature = existsSync(featureMd) ? readDoc(featureMd, "feature") : null;
      const plan = existsSync(planMd) ? readDoc(planMd, "plan") : null;
      const main = feature ?? plan;
      const link = `features/${name}/${feature ? "feature" : "plan"}.md`;
      let entry = `- [${main.title}](${link}) — ${main.data.summary ?? "?"}`;
      if (plan) {
        const status = plan.data.status ?? "?";
        statusCounts.set(status, (statusCounts.get(status) ?? 0) + 1);
        const facts = [status];
        if (plan.progress.total) facts.push(`${plan.progress.done}/${plan.progress.total}`);
        if (plan.data.updated) facts.push(`updated ${plan.data.updated}`);
        entry += feature ? ` · [plan](features/${name}/plan.md): ${facts.join(", ")}` : ` · ${facts.join(", ")}`;
      }
      entries.push(entry);
    } else if (name.endsWith(".md")) {
      errors.push(`${rel(dir)}: flat feature document; move it to docs/features/<name>/feature.md or plan.md`);
    }
  }
}

// Markdown anywhere else under docs/ is a legacy layout (docs/ideas/, docs/planning/, ...). Asset
// folders without Markdown, such as images referenced by store.md, are fine.
function markdownUnder(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return markdownUnder(path);
    return name.endsWith(".md") ? [path] : [];
  });
}

if (existsSync(docsDir)) {
  for (const name of readdirSync(docsDir).sort()) {
    const path = join(docsDir, name);
    if (statSync(path).isDirectory()) {
      if (name === "reviews") {
        for (const file of markdownUnder(path))
          if (dirname(file) !== path || !/^\d{4}-\d{2}-\d{2}-[a-z0-9]+(-[a-z0-9]+)*\.md$/.test(file.slice(path.length + 1)))
            errors.push(`${rel(file)}: reviews are named docs/reviews/YYYY-MM-DD-<name>.md`);
        continue;
      }
      if (WORKFLOW_DIRS.has(name)) continue;
      for (const file of markdownUnder(path))
        errors.push(`${rel(file)}: not a workflow location; documents live in docs/features/<name>/ (feature.md, plan.md)`);
    } else if (name.endsWith(".md") && !["root.md", "vision.md"].includes(name) && !TOOLING_FILES.has(name)) {
      errors.push(`${rel(path)}: not a workflow location; documents live in docs/features/<name>/ (feature.md, plan.md)`);
    }
  }
}

const visionMd = join(docsDir, "vision.md");
if (existsSync(visionMd)) readDoc(visionMd, "vision");

const counts = PLAN_STATUSES.filter((s) => statusCounts.has(s)).map((s) => `${statusCounts.get(s)} ${s}`);
if (statusCounts.has("?")) counts.push(`${statusCounts.get("?")} without a status`);

const lines = [
  BEGIN,
  "",
  "_Generated by `scripts/docs-index.mjs --fix` — do not edit this block by hand._",
  "",
  "### Features",
  "",
  ...(counts.length ? [`Plans: ${counts.join(" · ")}.`, ""] : []),
  ...(entries.length ? entries : ["_None yet._"]),
];
lines.push("", END);
const block = lines.join("\n");

if (mode === "print") {
  console.log(block);
} else {
  const text = existsSync(rootMd) ? readFileSync(rootMd, "utf8") : "";
  const begin = text.indexOf(BEGIN);
  const end = text.indexOf(END);
  const hasBlock = begin !== -1 && end > begin;
  const current = hasBlock ? text.slice(begin, end + END.length) : null;

  if (mode === "fix") {
    const next = hasBlock
      ? text.slice(0, begin) + block + text.slice(end + END.length)
      : `${text.replace(/\n*$/, "\n\n")}${block}\n`;
    if (next !== text) {
      writeFileSync(rootMd, next);
      console.log(`${rel(rootMd)}: index block ${hasBlock ? "updated" : "appended"}`);
    } else {
      console.log(`${rel(rootMd)}: index block already current`);
    }
  } else if (!hasBlock) {
    errors.push(`${rel(rootMd)}: docs-index markers not found — run with --fix to add the block`);
  } else if (current !== block) {
    errors.push(`${rel(rootMd)}: index block is stale — run: node scripts/docs-index.mjs --fix`);
  }
}

// root.md's own prose links are checked too; the generated block only links existing files.
if (existsSync(rootMd)) {
  const text = readFileSync(rootMd, "utf8");
  checkPrintable(rootMd, text);
  checkLinks(rootMd, proseLines(text.split("\n"), 0));
}

if (errors.length) {
  console.error(errors.map((e) => `error: ${e}`).join("\n"));
  process.exit(1);
}
