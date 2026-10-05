# Agent Instructions for Transcode Engine project

## Versioning

This app uses semantic versioning `major.minor.patch`, bumped per release. When a
change ships, bump the app version in the **same commit**:

- **patch** — bug fix or small enhancement to existing functionality.
- **minor** — new functionality, or a large/breaking change (while the app is in `0.x`).
- **major** — reserved until the app declares a stable `1.0.0`; after that, breaking
  changes for the app's users.

Documentation-only changes (`docs/`, `README.md`, `AGENTS.md`) are the exception —
merge them without a version bump. The same goes for Dependabot PRs: merge them
as-is, without adding a version bump; the updated dependencies ship with the
next versioned change.

Where the version lives:

- `version` in `manifest.json` is the app's release version and the source of truth —
  bump it in the same change that ships the work.
- Do **not** bump `schemaVersion` (`app.0.1`) for ordinary changes — it tracks the Hosty
  manifest *contract* format, not this app.

Each runtime app versions independently from Hosty Core/CLI and from the other apps.

## Pull Requests

- **Do not squash-merge PRs.** Parallel PRs are common, and squash merges rewrite the
  merged branch's history — the other in-flight branches can no longer rebase cleanly
  onto main. Use a regular merge commit instead.
- **One PR per feature, not per phase.** When a feature plan is split into phases,
  implement all phases on one branch and open a single PR. Individual phases rarely
  deliver complete functionality on their own, and under the versioning rules above
  each per-phase PR would pointlessly bump the version.
- **PR descriptions track the plan.** When the work is driven by a `plan.md`, the
  description lists the deliverables this PR completes by ID (`D3`, `D5`) and links the feature
  folder. Always state the version outcome ("0.4.2 → 0.5.0" or "No version
  change — documentation-only").

## Documentation

Development is document-driven: every non-trivial change starts and ends in `docs/`.

### Layout

```text
docs/
├── root.md              — prose overview + generated status index
├── vision.md            — optional: the direction the project is built toward (living, no status)
├── features/
│   └── <feature-name>/  — kebab-case; the feature's stable, permanent home
│       ├── feature.md   — current reality only
│       └── plan.md      — remaining work only
└── reviews/             — dated review archives, outside the status workflow
```

- `docs/reviews/` holds point-in-time review reports, named
  `YYYY-MM-DD-<name>.md`. A review is an archive, not tracked work: it records
  what was true at its stated baseline commit, is never edited afterwards to
  follow the code, and stays outside the status workflow and the generated
  index. A finding becomes tracked work only once it is triaged into the
  relevant feature's `plan.md` as a deliverable — the review itself never
  carries status. A later review may supersede earlier ones: it re-verifies
  their findings against its own baseline and restates only what is still
  open; the superseded archives are deleted in the same PR (git history keeps
  their full text), so the folder holds only reviews whose findings are still
  current.
- `docs/vision.md`, where a repository has one, holds the direction the project
  is built toward: the thesis, dated owner decisions, open strategic questions,
  and links to the features it spans. It is a living document — edited whenever
  the direction changes, with `created` / `updated` frontmatter and no `status`
  — and it sits outside the status workflow and the generated index, like
  `docs/reviews/`. It authorizes no implementation and owns no deliverables:
  work it names is tracked in the owning feature's `plan.md`. Feature and plan
  documents may cite its decisions by number and date.
- Beyond that there are no other documentation folders. A large or cross-cutting
  feature is an ordinary feature whose docs cross-link the features it spans;
  its `plan.md` never duplicates their deliverables — it links to them and keeps
  only the work that belongs to the umbrella itself.
- Two top-level files belong to other tooling and are not workflow documents:
  `docs/store.md` (the Marketplace store page) and `docs/agent.md` (the app's
  agent skill file). Any other Markdown under `docs/` — flat
  `docs/features/*.md`, `docs/ideas/`, `docs/planning/` — is rejected by the
  validator.

### Frontmatter

Every `feature.md`, `plan.md` and `vision.md` starts at its first line with a
frontmatter block, followed by the `# Title` heading:

```markdown
---
status: In Progress
created: 2026-09-24
updated: 2026-10-05
summary: One plain-text sentence saying what this document covers.
components: [apps/core, apps/shell]
---

# Title
```

- `status` — `plan.md` only, and required there; one of the statuses below.
- `created` / `updated` — required, `YYYY-MM-DD`.
- `summary` — required in `feature.md` and `plan.md`: one plain-text sentence of
  at most 200 characters, without Markdown. The index and other tools show it.
- `components` — optional: the repository-relative directories the document
  concerns. Each must exist. Omit it when the document concerns the whole
  repository.

The block is a strict subset of YAML: one `key: value` per line, plain values or
`[a, b]` lists, no nesting, comments or multi-line values. Wrap a value that
contains `: ` or starts with a YAML indicator character in double quotes.
Unknown keys are rejected. The H1 is the title; there is no `title` key.

### feature.md — reality

- Describes current behavior only: present tense, verifiable against the code.
  Words like "will", "planned", or "future" do not belong here — that content
  goes to `plan.md`.
- Created in the PR that first ships behavior, never earlier. When
  implementation diverges from the plan, this file follows the code.
- Its frontmatter has no `status`. It ends with a `## Testing Expectations`
  section for required coverage.

### plan.md — intent

- The single artifact for unbuilt work, from first idea to last deliverable:
  goal, target behavior (written as a diff against `feature.md` when the
  feature already exists), deliverables checklist, phases, open questions,
  verification steps.
- Its frontmatter `status` is one of:
  - **Draft** — being shaped; open questions allowed.
  - **On Hold** — deliberately parked.
  - **Ready** — no open questions left; set only after explicit user approval
    in chat, never on the agent's own judgment.
  - **In Progress** — implementation started.
  - **Blocked** — cannot proceed; the blocker is recorded in the document.
- Deliverables live in one `## Deliverables` section, which may contain `###`
  subsections such as phases, and they are the only checkboxes in a plan. Each
  is a top-level item that starts with a stable ID: `- [ ] D3. Text`. IDs are
  never renumbered or reused: a new deliverable takes the next free number, and
  a removed deliverable's ID stays retired. Use plain bullets, not nested
  checkboxes, for detail.
- Never implement a plan that is not Ready. A plan the user abandons is deleted
  (git history preserves it) — there is no Rejected status.
- Trivial work (bug fixes, small refactors, doc edits) needs no `plan.md`:
  ship it and update `feature.md` in the same PR. If mid-work the change turns
  out to be larger than expected, stop and write the plan.

### Status discipline

Statuses and checkboxes change in the same commit as the work they describe:

- the first implementation commit sets `status: In Progress`;
- the commit that completes a deliverable checks it off;
- the PR that completes the last deliverable also updates `feature.md`, deletes
  `plan.md`, and regenerates the index — completion is never deferred to a
  later PR, and scope is never silently narrowed to force completion.

Unfinished work exists only as unchecked deliverables — never hidden in notes,
"future work" sections, or follow-up remarks. Bump `updated` on every
meaningful change to a document.

### Index and validation

`docs/root.md` holds the prose overview plus a generated index that lists every
feature folder with its summary and, for a plan, its status, deliverable
progress and `updated` date. `node scripts/docs-index.mjs --fix` rewrites the
block between the `docs-index` markers and converts old `Status:` / `Created:` /
`Updated:` header lines into frontmatter. `--check` is the CI mode: it also
validates frontmatter, deliverable IDs and relative links. Never edit the
generated block by hand; run `--fix` after changing any document.

`scripts/docs-index.mjs` is shared by every Hosty repository. The canonical copy
lives in docker-host and the others carry a byte-identical copy, so change it
there first.

## Unit Testing
- Use `xUnit` for backend unit tests.
- Use `Imposter` for mocking dependencies in tests.
- Endpoint tests host `MapTranscodeEndpoints` on an in-memory
  `Microsoft.AspNetCore.TestHost` server with a mocked `ITranscodeEngine`, so no
  ffmpeg process ever starts.
- Ensure all new features have corresponding unit tests.

## ffmpeg and hardware
- The engine shells out to `ffmpeg` / `ffprobe`; it never links them. Argument
  construction (`FfmpegTranscodeEngine.BuildArguments`) is pure and unit-tested — add
  a case there for any new encode option rather than only exercising it end-to-end.
- Hardware acceleration is best-effort and always degrades to software: an explicit
  encoder the host cannot satisfy falls back to `libx264`/`libx265` with a warning,
  never a hard failure. `GET /hardware` and a job's `effectiveHardware` report what
  was actually selected — keep those honest.
- The default `docker` profile carries **no** device and must start on any host
  (incl. macOS Docker Desktop). Hardware lives behind the opt-in `docker-vaapi`
  profile (`/dev/dri` passthrough) and the `local` native runtime (VideoToolbox / AMF).
