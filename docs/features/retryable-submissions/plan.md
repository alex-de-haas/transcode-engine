---
status: In Progress
created: 2026-10-14
updated: 2026-10-14
summary: Durable client IDs for ordinary encode and remux submissions used by Media Server exports.
components: [src/TranscodeEngine.Api, src/TranscodeEngine.Api.Tests]
---

# Retryable encode submissions

Approved in chat on 2026-10-14 as the dependency fix for Media Server PR 338.
Ordinary composed jobs accept an optional client UUID. Identical retries reuse the job;
changed requests conflict. Accepted client jobs retain their state across provider restart,
so a lost response cannot create a second writer. Legacy jobs and extraction stay unchanged.

## Deliverables

- [x] D1. Add the wire contract, capability flag, admission deduplication and durable client-job journal.
- [ ] D2. Verify concurrency, conflict, cancellation, restart, cleanup and real encode behavior; publish the linked dependency PR with passing CI.

## Verification

Run the .NET suite, real ffmpeg fixtures, manifest and documentation checks. Verify Media
Server forwards IDs and recovers unconfirmed exports using the same persisted request.
