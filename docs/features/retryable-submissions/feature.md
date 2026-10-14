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
outputs cannot overwrite existing files or an active writer's output, including a file
created while encoding is in progress. IDs use canonical lowercase UUIDs and share one
namespace with joins and Blu-ray jobs, including evicted durable records.

The engine persists accepted requests and terminal states under `client-jobs` in app data.
After restart, completed/cancelled/failed jobs retain their outcomes; interrupted jobs
become failed and their temporary outputs, including Dolby Vision intermediates, are removed. A retry does not enqueue another
writer. Input files remain unchanged. Corrupt acceptance records fail closed.
Terminal journal I/O errors are logged without reversing an already-published in-process
success. If that terminal write never succeeds, restart treats the retained acceptance
record as interrupted rather than claiming a durable success.

Startup bounds the restored terminal cache and leaves already-terminal journals unchanged.
In-memory terminal retention does not delete these durable records. Direct lookup and
retry can restore an evicted record. Explicit removal clears the journal and optionally
the output; active client jobs must first be cancelled, reach a terminal state and finish worker cleanup. Once
explicitly removed, their ID is released. Consumers retain their own job/result ownership
until provider cleanup succeeds. Queue entries retain admission identity, so an old entry
cannot run a replacement job after the same ID is reused. Admission, journal restoration
and removal share a lock; late saves cannot resurrect a removed or replaced job.

## Testing Expectations

Cover UUID validation, ordinary HTTP field mapping, concurrent identical submissions,
changed-request conflicts, queue rejection, cancellation, interrupted and terminal
restart, journal removal and temporary output cleanup. Exercise case-insensitive IDs after eviction,
cross-operation conflicts, late terminal saves, queued ID reuse, no-overwrite publication
and staged Dolby Vision persistence and cleanup. Run real ffmpeg encode/remux,
decode both results and compare input bytes across retry, restart and deletion.
