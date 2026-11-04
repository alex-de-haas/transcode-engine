---
status: In Progress
created: 2026-11-04
updated: 2026-11-04
summary: Publish immutable verified app releases and migrate the existing main installation to daily.
---

# Ready app releases

## Goal

Replace source-branch updates with complete digest-pinned releases. The operator approved this rollout on 2026-11-04 for this app and four sibling repositories; development installations are excluded.

## Target behavior

Successful exact-source main CI triggers independent image build selection. Native amd64 and arm64 builders produce immutable candidates. Publication verifies the complete image graph, signed input provenance, assets and Core manifest validation before advancing daily. Metadata lives on app-releases, separately from source branches. Stable remains a manual promotion of a verified candidate. Feed identifiers are configuration, not platform policy.

Use the established Hosty publisher with a documented upstream baseline and bounded adaptations for standalone repositories. Preserve all production runtime profiles. Reuse artifacts only when their full input fingerprint, platform graph and provenance can be verified; otherwise rebuild conservatively. Unchanged complete candidates do not rebuild.

## Deliverables

- [x] D1. Implement tested standalone ready-release publication, exact-source CI gating, immutable metadata, daily advancement and manual stable promotion; retire the premature latest publisher.
- [x] D2. Verify the consumer manifest and install/update path with isolated Core-managed test data, including missing-artifact rejection and repeat/no-op checks.
- [ ] D3. Publish a complete daily release, migrate the existing non-development installation from main, verify updates and preserve its lifecycle/settings state.

## Verification

Run publisher regression tests, repository manifest/document checks and affected builds/tests. Require full PR and main CI, then successful publication with both architectures. Exercise the Core-managed sandbox and finish its resources. On the live instance, inspect the update-source plan, apply the reviewed selection and update, and verify the following check is a no-op. Preserve all dev installations and unrelated running apps.

## Open questions

None. This plan records the approved rollout; implementation choices reuse the established release contract.
