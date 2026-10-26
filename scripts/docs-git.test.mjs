// Validate idea allocation against actual Git PR histories, including synthetic merge checkouts.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

function fixture(allocateInProposal) {
  const dir = mkdtempSync(join(tmpdir(), "docs-git-"));
  const scripts = dirname(fileURLToPath(import.meta.url));
  mkdirSync(join(dir, "scripts")); mkdirSync(join(dir, "docs"));
  for (const name of ["docs-index.mjs", "docs-format.mjs"]) copyFileSync(join(scripts, name), join(dir, "scripts", name));
  const git = (...args) => {
    const r = spawnSync("git", args, { cwd: dir, encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr); return r.stdout.trim();
  };
  const validate = (base = "", head = "", mode = "--check") => spawnSync(process.execPath, ["scripts/docs-index.mjs", mode], {
    cwd: dir, encoding: "utf8", env: { ...process.env, DOCS_BASE_REF: base, DOCS_HEAD_REF: head },
  });
  const ideas = n => `---\ncreated: 2026-10-26\nupdated: 2026-10-26\n---\n# Ideas\n<!-- idea-id-high-water: ${n} -->\n## I1. Existing\n${n > 1 ? "## I2. Same heading\n" : ""}`;
  const commit = message => { git("add", "."); git("commit", "-qm", message); return git("rev-parse", "HEAD"); };
  try {
    git("init", "-q"); git("config", "user.name", "Fixture"); git("config", "user.email", "fixture@example.test");
    writeFileSync(join(dir, "docs/root.md"), "# Docs\n\n<!-- docs-index:begin -->\n<!-- docs-index:end -->\n");
    writeFileSync(join(dir, "docs/ideas.md"), ideas(1));
    assert.equal(validate("", "", "--fix").status, 0);
    const ancestor = commit("Initial reservations");
    git("checkout", "-qb", "target");
    writeFileSync(join(dir, "docs/ideas.md"), ideas(2));
    writeFileSync(join(dir, "target.txt"), "target work");
    const target = commit("Target allocates I2");
    git("checkout", "-qb", "proposal", ancestor);
    if (allocateInProposal) writeFileSync(join(dir, "docs/ideas.md"), ideas(2));
    writeFileSync(join(dir, "proposal.txt"), "proposal work");
    const proposal = commit("Proposal changes");
    git("merge", "--no-ff", "--no-edit", "target");
    assert.equal(git("merge-base", "HEAD", target), target, "fixture must reproduce the synthetic merge baseline");
    return { target, proposal, validate, done: () => rmSync(dir, { recursive: true, force: true }) };
  } catch (error) { rmSync(dir, { recursive: true, force: true }); throw error; }
}

test("a merged PR still rejects identical IDs independently allocated on its original branches", () => {
  const r = fixture(true);
  try {
    const result = r.validate(r.target, r.proposal);
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.stderr, /Concurrent allocation of I2/);
  } finally { r.done(); }
});

test("a merged PR accepts IDs inherited only from the target", () => {
  const r = fixture(false);
  try {
    const result = r.validate(r.target, r.proposal);
    assert.equal(result.status, 0, result.stderr);
  } finally { r.done(); }
});

test("an unavailable PR head fails explicitly instead of weakening allocation checks", () => {
  const r = fixture(false);
  try {
    const result = r.validate(r.target, "missing-proposal-head");
    assert.equal(result.status, 1);
    assert.match(result.stderr, /Cannot resolve idea proposal head/);
  } finally { r.done(); }
});
