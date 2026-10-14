---
created: 2026-10-14
updated: 2026-10-14
summary: Ordinary encode and remux jobs accept stable client IDs and retain acceptance across provider restarts.
components: [src/TranscodeEngine.Api, src/TranscodeEngine.Api.Tests]
---

# Retryable encode submissions

`POST /jobs` accepts an optional nonempty UUID `clientJobId` for composed jobs.
The engine returns that UUID in N format as its job ID. `/hardware` advertises
`idempotentTranscodes: true`; consumers use the flag before relying on this contract.
Extraction rejects this option. Requests without an ID retain their existing behavior.

Admission is serialized before and after probing. Identical retries return the existing
job; a changed input, output, selection or encoder option is refused. Client-owned
outputs cannot overwrite existing files or an active writer's output.

The engine persists accepted requests and terminal states under `client-jobs` in app data.
After restart, completed/cancelled/failed jobs retain their outcomes; interrupted jobs
become failed and their temporary outputs are removed. A retry does not enqueue another
writer. Input files remain unchanged. Corrupt acceptance records fail closed.

In-memory terminal retention does not delete these durable records. Direct lookup and
retry can restore an evicted record. Explicit removal clears the journal and optionally
the output; active client jobs must first be cancelled and reach a terminal state. Once
explicitly removed, their ID is released. Consumers retain their own job/result ownership
until provider cleanup succeeds.

## Testing Expectations

Cover UUID validation, ordinary HTTP field mapping, concurrent identical submissions,
changed-request conflicts, queue rejection, cancellation, interrupted and terminal
restart, journal removal and temporary output cleanup. Run real ffmpeg encode/remux,
decode both results and compare input bytes across retry, restart and deletion.
