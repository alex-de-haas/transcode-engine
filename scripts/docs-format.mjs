// Browser-safe documentation semantics shared by the repository validator and Documents.
// Keep this file byte-identical alongside docs-index.mjs in repositories using this workflow.
export function proseLines(content) {
  const lines = Array.isArray(content) ? content : content.split("\n");
  const result = [];
  let fence = null;
  let frontmatter = lines[0] === "---";
  for (let index = frontmatter ? 1 : 0; index < lines.length; index++) {
    const line = lines[index];
    if (frontmatter) {
      if (line === "---") frontmatter = false;
      continue;
    }
    const marker = line.match(/^\s*(`{3,}|~{3,})/);
    if (fence) {
      if (
        marker &&
        marker[1][0] === fence[0] &&
        marker[1].length >= fence.length &&
        line.trim() === marker[1]
      )
        fence = null;
    } else if (marker) fence = marker[1];
    else result.push({ index, line });
  }
  return result;
}
export function plainText(text) {
  const literals = [];
  const keep = (value) => `\u0000${literals.push(value) - 1}\u0000`;
  // Code span contents are literal: underscores, escaped punctuation and HTML-like text
  // inside them must not be consumed as formatting by the surrounding heading.
  let prose = text
    .replace(/(?<![\\`])(`+)(?!`)([\s\S]*?)(?<!`)\1(?!`)/g, (_match, _ticks, value) =>
      keep(/^ .+ $/.test(value) && /\S/.test(value) ? value.slice(1, -1) : value))
    .replace(/\\([_*~`])/g, (_match, value) => keep(value))
    .replace(/!?(?:\[([^\]]*)\])\([^)]*\)/g, "$1")
    .replace(/<[^>]*>/g, "");
  // Underscores within identifiers are not emphasis delimiters. Strip only paired,
  // word-boundary delimiters; repeat for nested emphasis without touching code spans.
  for (let depth = 0; depth < 3; depth++) {
    prose = prose.replace(/(?<![\p{L}\p{N}_])(_{1,3})(?=\S)(.+?)(?<!\s)\1(?![\p{L}\p{N}_])/gu, "$2");
  }
  return prose
    .replace(/[`*~]/g, "")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/\u0000(\d+)\u0000/g, (match, index) => literals[Number(index)] ?? match);
}
export function headingSlug(text) {
  return plainText(text)
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\p{M}\s_-]/gu, "")
    .replace(/\s/g, "-");
}
export function headings(content) {
  const used = new Set();
  const result = [];
  for (const { index, line } of proseLines(content)) {
    const match = line.match(/^(#{1,6})\s+(.+?)\s*#*\s*$/);
    if (!match) continue;
    const text = plainText(match[2]);
    const base = headingSlug(match[2]);
    let id = base,
      suffix = 0;
    while (used.has(id)) id = `${base}-${++suffix}`;
    used.add(id);
    result.push({ id, text, level: match[1].length, line: index + 1 });
  }
  return result;
}
export function documentKind(path) {
  if (/^docs\/features\/[^/]+\/plan\.md$/.test(path)) return "plan";
  if (/^docs\/features\/[^/]+\/feature\.md$/.test(path)) return "feature";
  if (path === "docs/root.md") return "overview";
  if (path === "docs/vision.md") return "vision";
  if (path === "docs/ideas.md") return "ideas";
  if (/^docs\/reviews\/[^/]+\.md$/.test(path)) return "review";
  if (/^docs\/(?:store|agent)\.md$/.test(path)) return "tooling";
  return "other";
}
export function resolvePath(from, destination) {
  if (!destination) return from;
  const parts = from.split("/").slice(0, -1);
  for (const part of destination.split("/")) {
    if (!part || part === ".") continue;
    if (part === "..") {
      if (!parts.length) return null;
      parts.pop();
    } else parts.push(part);
  }
  return parts.join("/");
}
export function documentLinks(path, content) {
  const result = [];
  for (const { index, line } of proseLines(content)) {
    const text = line.replace(/(`+)[^`]*?\1/g, "");
    const targets = [
      ...text.matchAll(/\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)/g),
    ].map((match) => match[1]);
    const definition = text.match(/^\s*\[[^\]]+\]:\s+<?([^\s>]+)>?/);
    if (definition) targets.push(definition[1]);
    for (const href of targets) {
      if (/^[a-z][a-z0-9+.-]*:/i.test(href)) continue;
      const rawPath = href.split(/[?#]/)[0];
      let destination = rawPath,
        anchor = href.includes("#") ? href.slice(href.indexOf("#") + 1) : "";
      try {
        destination = decodeURIComponent(destination);
        anchor = decodeURIComponent(anchor);
      } catch {
        /* Validate the literal destination. */
      }
      const resolved = destination.startsWith("/")
        ? null
        : resolvePath(path, destination);
      result.push({ href, path: resolved, anchor, line: index + 1 });
    }
  }
  return result;
}
export function ideaReservations(content) {
  const prose = proseLines(content);
  const text = prose.map((item) => item.line).join("\n");
  const items = headings(content).flatMap((item) => {
    const match = item.text.match(/^I([1-9]\d*)\b[.:\s—–-]*/);
    return match
      ? [{ ...item, number: Number(match[1]), ideaId: `I${match[1]}` }]
      : [];
  });
  const counters = [
    ...text.matchAll(/<!--\s*idea-id-high-water:\s*(\d+)\s*-->/g),
  ];
  const retiredMarkers = [
    ...text.matchAll(/<!--\s*retired-idea-ids:\s*([^>]*?)\s*-->/g),
  ];
  const highWater = counters.length === 1 ? Number(counters[0][1]) : null;
  const rawRetired = retiredMarkers[0]?.[1]?.trim() ?? "";
  const retired = rawRetired
    ? rawRetired.split(",").map((value) => value.trim())
    : [];
  const errors = [];
  if (
    (items.length ||
      retired.length ||
      /<!--\s*idea-id-high-water:/.test(text)) &&
    highWater === null
  )
    errors.push(
      "Ideas need exactly one idea-id-high-water reservation comment.",
    );
  if (counters.length > 1 || retiredMarkers.length > 1)
    errors.push("Duplicate idea reservation metadata.");
  if (retired.some((id) => !/^I[1-9]\d*$/.test(id)))
    errors.push(
      "Retired idea IDs must be a comma-separated list of I<n> identifiers.",
    );
  if (new Set(retired).size !== retired.length)
    errors.push("Duplicate retired idea ID.");
  const seen = new Set();
  for (const item of items) {
    if (seen.has(item.ideaId)) errors.push(`Duplicate idea ID ${item.ideaId}.`);
    seen.add(item.ideaId);
    if (retired.includes(item.ideaId))
      errors.push(`Retired idea ID ${item.ideaId} cannot be reused.`);
  }
  if (
    highWater !== null &&
    (!Number.isSafeInteger(highWater) ||
      [
        ...items.map((item) => item.number),
        ...retired.map((id) => Number(id.slice(1))),
      ].some((id) => id > highWater))
  )
    errors.push(
      "Idea reservation counter is below an allocated or retired ID.",
    );
  return { items, highWater, retired, errors };
}
export function ideaEvolution(content, previous, ancestor = previous, proposal = content) {
  const now = ideaReservations(content),
    before = ideaReservations(previous),
    base = ideaReservations(ancestor);
  const errors = [];
  if (
    before.highWater !== null &&
    (now.highWater === null || now.highWater < before.highWater)
  )
    errors.push(
      "The idea high-water counter must never decrease or disappear.",
    );
  const active = new Set(now.items.map((item) => item.ideaId));
  const old = new Set(before.items.map((item) => item.ideaId));
  const initial = new Set(base.items.map((item) => item.ideaId));
  const proposed = new Set(ideaReservations(proposal).items.map((item) => item.ideaId));
  for (const id of new Set([...before.retired, ...old])) {
    if (!active.has(id) && !now.retired.includes(id))
      errors.push(`Removed idea ${id} must stay reserved in retired-idea-ids.`);
    if (before.retired.includes(id) && active.has(id))
      errors.push(`Retired idea ${id} cannot be reused.`);
  }
  for (const item of now.items) {
    if (
      !old.has(item.ideaId) &&
      before.highWater !== null &&
      item.number <= before.highWater
    )
      errors.push(
        `New idea ${item.ideaId} must be above the previous high-water counter.`,
      );
    const competing = before.items.find(
      (other) => other.ideaId === item.ideaId,
    );
    if (!initial.has(item.ideaId) && proposed.has(item.ideaId) && competing)
      errors.push(
        `Concurrent allocation of ${item.ideaId}; assign the unpublished idea a new ID before merging.`,
      );
  }
  return errors;
}
export function openQuestions(content) {
  const lines = content.split("\n");
  const sections = headings(content);
  return sections.flatMap((heading, index) => {
    if (!/^open (?:questions|decisions)(?:\b|$)/i.test(heading.text)) return [];
    const next = sections
      .slice(index + 1)
      .find((item) => item.level <= heading.level);
    const text = proseLines(
      lines.slice(heading.line, next ? next.line - 1 : lines.length),
    )
      .map((item) => item.line)
      .join("\n")
      .trim();
    if (
      !text ||
      /^(?:none\b|no (?:open|remaining|unresolved) (?:questions|decisions)\b|all (?:questions|decisions) (?:are |have been )?(?:resolved|settled|closed)\b)/i.test(
        text,
      )
    )
      return [];
    const unresolved = text
      .split("\n")
      .filter((line) => line.trim())
      .filter(
        (line) =>
          !/^\s*(?:[-*]\s*)?(?:resolved|settled|closed|decided)\b/i.test(line),
      );
    return unresolved.length
      ? [
          {
            anchor: heading.id,
            title: heading.text,
            excerpt: text.slice(0, 700),
          },
        ]
      : [];
  });
}
export function indexEntry(name, feature, plan) {
  const main = feature ?? plan;
  let entry = `- [${main.title}](features/${name}/${feature ? "feature" : "plan"}.md) — ${main.summary ?? "?"}`;
  if (plan) {
    const facts = [plan.status ?? "?"];
    if (plan.progress?.total)
      facts.push(`${plan.progress.done}/${plan.progress.total}`);
    if (plan.updated) facts.push(`updated ${plan.updated}`);
    entry += feature
      ? ` · [plan](features/${name}/plan.md): ${facts.join(", ")}`
      : ` · ${facts.join(", ")}`;
  }
  return entry;
}
export function indexBlock(entries, statuses) {
  const counts = ["In Progress", "Ready", "Blocked", "Draft", "On Hold"]
    .filter((status) => statuses[status])
    .map((status) => `${statuses[status]} ${status}`);
  if (statuses["?"]) counts.push(`${statuses["?"]} without a status`);
  return [
    "<!-- docs-index:begin -->",
    "",
    "_Generated by `scripts/docs-index.mjs --fix` — do not edit this block by hand._",
    "",
    "### Features",
    "",
    ...(counts.length ? [`Plans: ${counts.join(" · ")}.`, ""] : []),
    ...(entries.length ? entries : ["_None yet._"]),
    "",
    "<!-- docs-index:end -->",
  ].join("\n");
}
export function documentIndex(documents) {
  const groups = new Map(),
    statuses = {};
  for (const document of documents) {
    const match = document.path.match(
      /^docs\/features\/([^/]+)\/(feature|plan)\.md$/,
    );
    if (!match) continue;
    const pair = groups.get(match[1]) ?? {};
    pair[match[2]] = document;
    groups.set(match[1], pair);
    if (match[2] === "plan")
      statuses[document.status ?? "?"] =
        (statuses[document.status ?? "?"] ?? 0) + 1;
  }
  return indexBlock(
    [...groups.keys()].sort().map((name) => {
      const pair = groups.get(name);
      return indexEntry(name, pair.feature, pair.plan);
    }),
    statuses,
  );
}
