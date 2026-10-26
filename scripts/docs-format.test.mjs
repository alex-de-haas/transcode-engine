import { test } from "node:test";
import assert from "node:assert/strict";
import {
  headings,
  documentLinks,
  ideaReservations,
  ideaEvolution,
  openQuestions,
  documentIndex,
} from "./docs-format.mjs";
const ideas = (counter, content, retired = "") =>
  `---\ncreated: 2026-10-25\nupdated: 2026-10-26\n---\n# Ideas\n<!-- idea-id-high-water: ${counter} -->\n${retired ? `<!-- retired-idea-ids: ${retired} -->` : ""}\n${content}`;
test("same-version anchors retain Unicode, duplicate heading suffixes and exclude fenced examples", () => {
  const source =
    "# Title\n## A **linked** [heading](file.md)\n## A linked heading\n## Пример\n```md\n## Hidden\n[bad](gone.md)\n```\n[read](other.md#a-linked-heading)\n[here](#пример)";
  assert.deepEqual(
    headings(source).map((item) => item.id),
    ["title", "a-linked-heading", "a-linked-heading-1", "пример"],
  );
  assert.deepEqual(
    documentLinks("docs/ideas.md", source)
      .slice(-2)
      .map((item) => [item.path, item.anchor]),
    [
      ["docs/other.md", "a-linked-heading"],
      ["docs/ideas.md", "пример"],
    ],
  );
  assert.equal(
    documentLinks("docs/root.md", "[escape](../../etc/passwd)")[0].path,
    null,
  );
});
test("idea reservations reject duplicate, retired and out-of-counter identifiers", () => {
  assert.equal(
    ideaReservations(ideas(3, "## I1. First\n## I3. Third", "I2")).errors
      .length,
    0,
  );
  assert.match(
    ideaReservations(
      ideas(2, "## I1. First\n## I1. Copy\n## I3. Third", "I1"),
    ).errors.join(" "),
    /Duplicate idea ID I1/,
  );
  assert.match(
    ideaReservations(ideas(2, "## I3. Third")).errors.join(" "),
    /below/,
  );
  assert.match(
    ideaReservations(ideas(3, "## I2. Reused", "I2")).errors.join(" "),
    /cannot be reused/,
  );
});
test("deleted IDs stay reserved after workspaces disappear and concurrent allocations collide", () => {
  const before = ideas(2, "## I1. First\n## I2. Second");
  assert.deepEqual(
    ideaEvolution(ideas(3, "## I1. First\n## I3. Third", "I2"), before),
    [],
  );
  assert.match(
    ideaEvolution(ideas(2, "## I1. First"), before).join(" "),
    /I2 must stay reserved/,
  );
  assert.match(
    ideaEvolution(ideas(1, "## I1. First", "I2"), before).join(" "),
    /never decrease/,
  );
  assert.match(
    ideaEvolution(
      ideas(3, "## I3. Local"),
      ideas(3, "## I3. Remote"),
      ideas(2, ""),
    ).join(" "),
    /Concurrent allocation/,
  );
});
test("authored questions distinguish settled sections and keep ambiguous prose without inventing counts", () => {
  assert.equal(
    openQuestions(
      "# Plan\n## Open Questions\nNone.\n## Deliverables\n- [ ] D1. Work",
    ).length,
    0,
  );
  assert.equal(
    openQuestions("# Plan\n## Open Decisions\n- Resolved: use Git.").length,
    0,
  );
  assert.equal(
    openQuestions(
      "# Plan\n## Open Questions And Decisions\nShould we support release branches?",
    ).length,
    1,
  );
  assert.equal(
    openQuestions("# Plan\n```md\n## Open Questions\nExample?\n```").length,
    0,
  );
});
test("derived index distinguishes current features from remaining plans without a completion history", () => {
  const block = documentIndex([
    { path: "docs/features/a/feature.md", title: "A", summary: "Current A." },
    {
      path: "docs/features/b/plan.md",
      title: "B",
      summary: "Build B.",
      status: "Ready",
      updated: "2026-10-26",
      progress: { done: 1, total: 3 },
    },
  ]);
  assert.match(block, /Plans: 1 Ready/);
  assert.match(block, /A.*Current A/);
  assert.match(block, /Ready, 1\/3, updated 2026-10-26/);
  assert.doesNotMatch(block, /Shipped|History/);
});

test("empty registries retain valid reservation metadata", () => {
  assert.match(
    ideaReservations("<!-- idea-id-high-water: invalid -->").errors.join(" "),
    /exactly one/,
  );
  assert.match(
    ideaReservations("<!-- retired-idea-ids: I1 -->").errors.join(" "),
    /exactly one/,
  );
  assert.match(
    ideaEvolution("", ideas(2, "## I2. Old")).join(" "),
    /never decrease or disappear/,
  );
});

test('independent allocations conflict even when their headings happen to match', () => {
  assert.match(ideaEvolution(ideas(3, '## I3. Same title\nLocal idea'), ideas(3, '## I3. Same title\nOther idea'), ideas(2, '')).join(' '), /Concurrent allocation/);
});


test("merged target-only ideas are not independent proposal allocations", () => {
  const ancestor = ideas(1, "## I1. Existing");
  const target = ideas(2, "## I1. Existing\n## I2. Target allocation");
  assert.deepEqual(ideaEvolution(target, target, ancestor, ancestor), []);
  assert.match(ideaEvolution(target, target, ancestor, target).join(" "), /Concurrent allocation of I2/);
});

test("heading anchors retain code and identifier underscores while stripping actual emphasis", () => {
  const outline = headings([
    "## `DOCS_BASE_REF`", "## foo_bar", "## `work_item.assign`",
    "## _Important_ __details__", "## `__literal__`", "## escaped\\_identifier",
    "## ``with`an_underscore``", "## `a<b>_c`", "## _some_identifier_",
  ].join("\n"));
  assert.deepEqual(outline.map(item => item.id), [
    "docs_base_ref", "foo_bar", "work_itemassign", "important-details",
    "__literal__", "escaped_identifier", "withan_underscore", "ab_c", "some_identifier",
  ]);
  assert.equal(outline[0].text, "DOCS_BASE_REF");
  assert.equal(outline[4].text, "__literal__");
  assert.equal(outline[7].text, "a<b>_c");
});
