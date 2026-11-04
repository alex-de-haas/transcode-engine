---
created: 2026-11-04
updated: 2026-11-04
summary: Verified immutable app releases, independent image selection, automatic daily feeds and manual stable promotion.
---

# Ready app releases

## Publication

`ci.yml` runs checks on pull requests and main pushes. Its `ci-required` job requires every selected check to succeed. `ready-releases.yml` separately qualifies that exact main commit and CI attempt before selecting builds. Manual dispatch is recovery for an already validated main commit, not a validation bypass. The legacy mutable-latest publisher is removed.

`distribution/release-publisher.json` declares app profiles, images and arbitrary feed IDs. `daily` is the automatic default for this distribution. `stable` appears only after an explicit promotion with stabilization evidence. A main merge is not a stable release.

Each selected image builds on native Linux amd64 and arm64 runners. The merge step rejects missing or mismatched source/input/platform receipts before creating the candidate index. The publisher verifies the full anonymous-consumer image graph, configuration architecture, layer availability, signed input provenance, release assets and the complete manifest through the pinned Core admission validator. Version tags are non-overwriting compatibility aliases; released manifests use digests.

The generated manifest uses `app.0.2`. The authoring manifest keeps `app.0.1` and its development profiles. Every production profile remains available; source-based production runtimes use a full commit pin. Assets and skill packages are retained beside the immutable manifest.

## Metadata and feeds

The `app-releases` branch stores immutable `apps/com.haas.transcode-engine/releases/<release-id>/` records and a guarded `apps/com.haas.transcode-engine/feeds.json` index. The index references exact metadata commits. Feed updates are fast-forward commits with bounded retry and ancestry checks, so a failed or older candidate cannot replace a newer ready release. A partial failure leaves the previous feed usable. Retrying a completed candidate recovers its pointer without rebuilding.

The canonical index is [com.haas.transcode-engine feeds](https://raw.githubusercontent.com/alex-de-haas/transcode-engine/app-releases/apps/com.haas.transcode-engine/feeds.json). Source-branch `feeds.json` remains the explicit legacy main source for compatibility; it is not a ready-release channel. Existing production installations move to the canonical index, not merely to a renamed main feed.

`promote-release.yml` accepts a declared feed ID, exact retained release ID, expected current release (or `first`), stabilization evidence and a UUID retained across retries. Promotion rechecks the immutable artifacts and metadata and does not rebuild. A daily release does not advance stable.

## Build selection and reuse

Check selection and image selection are independent. Fingerprints cover materialized Docker context bytes, modes, symlinks, recipe, workflow and resolved builder/frontend. Nested contexts remain independent. A completely unchanged candidate produces no build. Artifact reuse additionally requires an eligible immutable recipe, exact input fingerprint, complete architecture graph and original signed provenance.

Recipes with unpinned package or operating-system inputs rebuild conservatively when the app candidate changes; an input match alone does not prove their dependency closure. Unsupported Docker input syntax retains the full context and disables reuse. Force rebuild is an explicit recovery option. The selected decision and reason are recorded in the release plan.

## Existing installations

Inspect runtime/development state first. Do not change active dev installations. For a production installation, use the supported source plan/apply operation:

```sh
hosty --data-root <instance-root> apps update-source plan com.haas.transcode-engine --index https://raw.githubusercontent.com/alex-de-haas/transcode-engine/app-releases/apps/com.haas.transcode-engine/feeds.json --feed daily
hosty --data-root <instance-root> apps update-source apply com.haas.transcode-engine --plan <reviewed-plan-digest> --request-id <32-hex-uuid>
hosty --data-root <instance-root> apps update-plan com.haas.transcode-engine
hosty --data-root <instance-root> apps update com.haas.transcode-engine --plan-digest <reviewed-update-digest>
```

Source selection and applying an app update are separate reviewed steps. Preserve the operation UUID after uncertainty and query `apps update-source operation`. Keep the existing autostart setting and stopped/running state. Verify the next update plan is a no-op and the selected image locks match the release. No registry-file edit, source override or provider credential transfer is involved.

## Publisher maintenance

`scripts/releases/` vendors Hosty's publisher and tests from docker-host commit `407dffa8f9dad4f9758624ed3e5860b9304549bc`, with its AGPL-3.0 license retained in that directory. The five standalone apps carry the same implementation. Standalone adaptations support root-level assets, nested Docker contexts, optional platform builds, an explicit consumer-validator path, native per-platform receipts and the aggregate CI gate. The validator checkout is pinned to the same full commit. These CI utilities are separate from the app's runtime and license.

## Testing Expectations

- Run `node --test scripts/releases/*.test.mjs` for admission, corrupt/missing artifact, provenance, immutable metadata, promotion, recovery, no-op and digest-reuse regression coverage.
- Run the repository's application checks and `node scripts/docs-index.mjs --check --base <target-ref>` for affected source and documentation.
- Validate generated manifests through the pinned Core consumer validator, including every production profile.
- Use a Core-managed child with separate data for installation/update acceptance; verify failed image resolution preserves the previous installation and a repeated update is a no-op. Finish the child through its owner.
- Require successful exact-source main CI and complete amd64/arm64 publication before migrating a live installation. Preserve dev installations and existing lifecycle state.
