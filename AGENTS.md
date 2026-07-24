# Agent Instructions for Transcode Engine project

## Versioning

This app uses semantic versioning `major.minor.patch`, bumped per release. When a
change ships, bump the app version in the **same commit**:

- **patch** — bug fix or small enhancement to existing functionality.
- **minor** — new functionality, or a large/breaking change (while the app is in `0.x`).
- **major** — reserved until the app declares a stable `1.0.0`; after that, breaking
  changes for the app's users.

Documentation-only changes (`docs/`, `README.md`, `AGENTS.md`) are the exception —
merge them without a version bump.

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
  description lists the deliverables this PR completes and links the feature
  folder. Always state the version outcome ("0.4.2 → 0.5.0" or "No version
  change — documentation-only").

## Documentation

Development is document-driven: every non-trivial change starts and ends in `docs/`.

### Layout

```text
docs/
├── root.md              — prose overview + generated status index
└── features/
    └── <feature-name>/  — kebab-case; the feature's stable, permanent home
        ├── feature.md   — current reality only
        └── plan.md      — remaining work only
```

- There are no other documentation folders. A large or cross-cutting feature is
  an ordinary feature whose docs cross-link the features it spans; its `plan.md`
  never duplicates their deliverables — it links to them and keeps only the work
  that belongs to the umbrella itself.
- Migration is lazy: legacy flat docs (`docs/features/*.md`, `docs/ideas/`,
  `docs/planning/`) move into feature folders with `git mv` whenever work
  touches them; never in bulk.

### feature.md — reality

- Describes current behavior only: present tense, verifiable against the code.
  Words like "will", "planned", or "future" do not belong here — that content
  goes to `plan.md`.
- Created in the PR that first ships behavior, never earlier. When
  implementation diverges from the plan, this file follows the code.
- Starts with `Created:` / `Updated:` lines (no `Status:`), ends with a
  `## Testing Expectations` section for required coverage.

### plan.md — intent

- The single artifact for unbuilt work, from first idea to last deliverable:
  goal, target behavior (written as a diff against `feature.md` when the
  feature already exists), deliverables checklist, phases, open questions,
  verification steps.
- Starts with `Status:` / `Created:` / `Updated:`. Statuses:
  - **Draft** — being shaped; open questions allowed.
  - **On Hold** — deliberately parked.
  - **Ready** — no open questions left; set only after explicit user approval
    in chat, never on the agent's own judgment.
  - **In Progress** — implementation started.
  - **Blocked** — cannot proceed; the blocker is recorded in the document.
- Never implement a plan that is not Ready. A plan the user abandons is deleted
  (git history preserves it) — there is no Rejected status.
- Trivial work (bug fixes, small refactors, doc edits) needs no `plan.md`:
  ship it and update `feature.md` in the same PR. If mid-work the change turns
  out to be larger than expected, stop and write the plan.

### Status discipline

Statuses and checkboxes change in the same commit as the work they describe:

- the first implementation commit sets `Status: In Progress`;
- the commit that completes a deliverable checks it off;
- the PR that completes the last deliverable also updates `feature.md`, deletes
  `plan.md`, and regenerates the index — completion is never deferred to a
  later PR, and scope is never silently narrowed to force completion.

Unfinished work exists only as unchecked deliverables — never hidden in notes,
"future work" sections, or follow-up remarks. Bump `Updated:` on every
meaningful change to a document.

### Index

`docs/root.md` holds the prose overview plus a generated per-feature status
index. `node scripts/docs-index.mjs --fix` rewrites the block between the
`docs-index` markers (and validates headers); `--check` is the CI mode. Never
edit the generated block by hand; regenerate it in any change that adds,
renames, deletes, or changes the status of a doc.

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
