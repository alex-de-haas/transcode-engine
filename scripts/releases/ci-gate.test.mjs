import assert from 'node:assert/strict';
import test from 'node:test';
import { githubRequest, waitForValidation, publicationContext } from './ci-gate.mjs';

const context = { ref: 'refs/heads/main', event: 'push', sha: 'a'.repeat(40), repository: 'owner/repo' };
const successfulRun = { id: 123, run_attempt: 2, head_sha: context.sha, head_branch: 'main', event: 'push',
  path: '.github/workflows/ci.yml', head_repository: { full_name: context.repository },
  status: 'completed', conclusion: 'success' };
const requiredJob = { name: 'ci-required', status: 'completed', conclusion: 'success' };

function fixture({ runs = [successfulRun], jobs = [requiredJob], current = successfulRun, context: caller = context } = {}) {
  const calls = [];
  let time = 0;
  const options = { context: caller, timeoutMs: 30, pollMs: 10, now: () => time,
    sleep: async ms => { time += ms; }, log: () => {}, request: async path => {
      calls.push(path);
      if (path.includes('/workflows/ci.yml/runs?')) return { workflow_runs: typeof runs === 'function' ? runs(calls) : runs };
      if (path.includes('/jobs?')) return { jobs: typeof jobs === 'function' ? jobs(calls) : jobs };
      return current;
    } };
  return { options, calls };
}

test('accepts only the exact main push SHA and successful required job of the current attempt', async () => {
  const { options, calls } = fixture();
  assert.deepEqual(await waitForValidation(options), { runId: '123', sourceSha: context.sha });
  assert.match(calls[0], new RegExp(`event=push&branch=main&head_sha=${context.sha}`));
  assert.match(calls[1], /runs\/123\/attempts\/2\/jobs/);
  assert.match(calls[2], /runs\/123$/);
});

for (const caller of [
  { ...context, ref: 'refs/heads/feature', event: 'workflow_dispatch' },
  { ...context, ref: 'refs/tags/release' },
  { ...context, event: 'pull_request' },
  { ...context, sha: 'short-sha' },
]) test(`rejects invalid publication context before API access: ${caller.ref}/${caller.event}/${caller.sha}`, async () => {
  const { options, calls } = fixture({ context: caller });
  await assert.rejects(waitForValidation(options), /Publication requires/);
  assert.equal(calls.length, 0);
});

test('allows manual main dispatch only after its exact push CI succeeds', async () => {
  const { options } = fixture({ context: { ...context, event: 'workflow_dispatch' } });
  assert.equal((await waitForValidation(options)).runId, '123');
});

for (const conclusion of ['failure', 'cancelled', 'timed_out', 'skipped', 'neutral', 'action_required', null]) {
  test(`blocks completed CI conclusion ${conclusion}`, async () => {
    const { options, calls } = fixture({ runs: [{ ...successfulRun, conclusion }] });
    await assert.rejects(waitForValidation(options), /publication is blocked/);
    assert.equal(calls.length, 1);
  });
}

for (const mismatch of [
  { head_sha: 'b'.repeat(40) }, { head_branch: 'feature' }, { event: 'pull_request' },
  { path: '.github/workflows/other.yml' }, { head_repository: { full_name: 'other/repo' } },
]) test(`blocks mismatched CI identity: ${JSON.stringify(mismatch)}`, async () => {
  const { options } = fixture({ runs: [{ ...successfulRun, ...mismatch }] });
  await assert.rejects(waitForValidation(options), /mismatched SHA, branch/);
});

test('does not accept an older success while the newest same-SHA run failed', async () => {
  const { options } = fixture({ runs: [successfulRun, { ...successfulRun, id: 124, conclusion: 'failure' }] });
  await assert.rejects(waitForValidation(options), /CI run 124 concluded failure/);
});

test('waits for registration and running CI with a bounded deadline', async () => {
  let poll = 0;
  const { options } = fixture({ runs: () => ++poll === 1 ? [] : poll === 2 ? [{ ...successfulRun, status: 'in_progress' }] : [successfulRun] });
  assert.equal((await waitForValidation(options)).runId, '123');
  assert.equal(poll, 3);
});

for (const runs of [[], [{ ...successfulRun, status: 'queued' }]]) {
  test(`missing or unfinished CI cannot pass at the deadline: ${runs.length}`, async () => {
    const { options, calls } = fixture({ runs });
    await assert.rejects(waitForValidation(options), /Timed out waiting/);
    assert.equal(calls.length, 3);
  });
}

for (const jobs of [[], [{ ...requiredJob, conclusion: 'skipped' }], [{ ...requiredJob, conclusion: 'failure' }],
  [{ ...requiredJob, status: 'in_progress' }], [requiredJob, requiredJob]]) {
  test(`workflow success alone cannot bypass required job: ${JSON.stringify(jobs)}`, async () => {
    await assert.rejects(waitForValidation(fixture({ jobs }).options), /one successful ci-required/);
  });
}

test('a new run attempt while validating jobs must finish before publication', async () => {
  const { options, calls } = fixture({ current: { ...successfulRun, run_attempt: 3, status: 'in_progress', conclusion: null } });
  await assert.rejects(waitForValidation(options), /Timed out waiting/);
  assert.equal(calls.length, 9);
});

test('paginates jobs instead of treating the first page as a missing required job', async () => {
  const { options } = fixture();
  const request = options.request;
  options.request = async path => path.includes('/jobs?')
    ? { jobs: path.endsWith('page=1') ? Array.from({ length: 100 }, () => ({ name: 'other' })) : [requiredJob] }
    : request(path);
  assert.equal((await waitForValidation(options)).runId, '123');
});

test('rejects malformed API responses and unknown statuses', async () => {
  const { options } = fixture();
  await assert.rejects(waitForValidation({ ...options, request: async () => ({}) }), /Malformed/);
  await assert.rejects(waitForValidation(fixture({ runs: [{ ...successfulRun, status: 'unknown' }] }).options), /Unrecognized/);
});

test('rejects unbounded or invalid wait settings', async () => {
  for (const timeoutMs of [0, -1, NaN, Infinity, 3_600_001]) {
    await assert.rejects(waitForValidation({ ...fixture().options, timeoutMs }), /bounded/);
  }
});

test('API errors fail closed without retries or leaking response/token content', async () => {
  let count = 0;
  const token = 'private-token-value';
  const request = githubRequest({ token, fetchImpl: async (_url, options) => {
    count++;
    assert.equal(options.headers.Authorization, `Bearer ${token}`);
    return { ok: false, status: 403, text: async () => token };
  } });
  await assert.rejects(waitForValidation({ ...fixture().options, request }), error => {
    assert.match(error.message, /HTTP 403/);
    assert.ok(!error.message.includes(token));
    return true;
  });
  assert.equal(count, 1);
  assert.throws(() => githubRequest({ token: '' }), /actions:read/);
});

test('completed-CI publication validates trigger source instead of latest default-branch execution SHA', async () => {
  const caller = publicationContext('workflow_run', { workflow_run: successfulRun }, {
    GITHUB_REPOSITORY: context.repository, GITHUB_SHA: 'b'.repeat(40), GITHUB_REF: 'refs/heads/main',
  });
  assert.equal(caller.sha, context.sha);
  const { options, calls } = fixture({ context: caller });
  const sleeps = [];
  options.sleep = async ms => sleeps.push(ms);
  assert.deepEqual(await waitForValidation(options), { runId: '123', sourceSha: context.sha });
  assert.equal(calls.length, 3);
  assert.match(calls[0], /runs\/123$/);
  assert.equal(calls.some(path => path.includes('/workflows/ci.yml/runs?')), false);
  assert.deepEqual(sleeps, []);
});

for (const mismatch of [{ event: 'pull_request' }, { event: 'schedule' }, { event: 'workflow_dispatch' },
  { head_branch: 'feature' }, { conclusion: 'failure' }, { conclusion: 'cancelled' },
  { head_repository: { full_name: 'fork/repo' } }]) {
  test(`rejects unqualified workflow_run trigger before API lookup: ${JSON.stringify(mismatch)}`, async () => {
    const caller = publicationContext('workflow_run', { workflow_run: { ...successfulRun, ...mismatch } }, { GITHUB_REPOSITORY: context.repository });
    const { options, calls } = fixture({ context: caller });
    await assert.rejects(waitForValidation(options), /mismatched|successful completed/);
    assert.equal(calls.length, 0);
  });
}

test('automatic publication never waits while a completed trigger has since been rerun', async () => {
  const caller = publicationContext('workflow_run', { workflow_run: successfulRun }, { GITHUB_REPOSITORY: context.repository });
  const { options, calls } = fixture({ context: caller, current: { ...successfulRun, status: 'in_progress', conclusion: null } });
  options.sleep = async () => assert.fail('automatic publication must not poll');
  await assert.rejects(waitForValidation(options), /no longer completed successfully/);
  assert.equal(calls.length, 1);
});

for (const event of ['push', 'workflow_dispatch', 'workflow_run']) {
  test(`${event} waits for delayed required-job evidence before accepting completed CI`, async () => {
    let reads = 0;
    const caller = { ...context, event, ...(event === 'workflow_run' ? { trigger: successfulRun } : {}) };
    const { options, calls } = fixture({ context: caller, jobs: () => {
      reads++;
      return reads === 1 ? [] : reads === 2 ? [{ ...requiredJob, status: 'in_progress', conclusion: null }] : [requiredJob];
    } });
    assert.deepEqual(await waitForValidation(options), { runId: '123', sourceSha: context.sha });
    assert.equal(reads, 3);
    assert.equal(calls.filter(path => path.endsWith('/runs/123')).length, event === 'workflow_run' ? 6 : 3);
  });
}

test('missing required-job evidence expires within two minutes even with a longer CI deadline', async () => {
  const { options, calls } = fixture({ jobs: [] });
  options.timeoutMs = 1_800_000;
  options.pollMs = 15_000;
  await assert.rejects(waitForValidation(options), /one successful ci-required/);
  assert.equal(options.now(), 120_000);
  assert.equal(calls.filter(path => path.includes('/jobs?')).length, 9);
});

for (const jobs of [[{ ...requiredJob, conclusion: 'failure' }], [{ ...requiredJob, conclusion: 'skipped' }],
  [{ ...requiredJob, status: 'unknown' }], [requiredJob, requiredJob]]) {
  test(`invalid required-job evidence never polls: ${JSON.stringify(jobs)}`, async () => {
    const { options } = fixture({ jobs });
    options.sleep = async () => assert.fail('terminal or ambiguous evidence must fail immediately');
    await assert.rejects(waitForValidation(options), /ci-required/);
  });
}

test('delayed job evidence never hides a subsequently failed CI run', async () => {
  let reads = 0;
  const { options } = fixture({ jobs: [], runs: () => [{ ...successfulRun, conclusion: ++reads === 1 ? 'success' : 'failure' }] });
  await assert.rejects(waitForValidation(options), /CI run 123 concluded failure/);
  assert.equal(reads, 2);
});

test('automatic publication rejects a rerun discovered while required-job evidence is missing', async () => {
  const caller = { ...context, event: 'workflow_run', trigger: successfulRun };
  const { options } = fixture({ context: caller, jobs: [] });
  const request = options.request;
  let reads = 0;
  options.request = async path => path.endsWith('/runs/123') && ++reads === 2
    ? { ...successfulRun, run_attempt: 3, status: 'in_progress', conclusion: null } : request(path);
  options.sleep = async () => assert.fail('a new CI attempt requires a new completed trigger');
  await assert.rejects(waitForValidation(options), /being rerun/);
});

for (const conclusion of ['cancelled', 'timed_out', 'neutral', 'action_required', null]) {
  test(`completed required-job conclusion ${conclusion} cannot be retried into success`, async () => {
    const { options } = fixture({ jobs: [{ ...requiredJob, conclusion }] });
    options.sleep = async () => assert.fail('terminal evidence must fail immediately');
    await assert.rejects(waitForValidation(options), /ci-required/);
  });
}

test('a rerun discovered after delayed job evidence clears its expired evidence deadline', async () => {
  const { options } = fixture({ jobs: [] });
  options.timeoutMs = 150_000;
  options.pollMs = 60_000;
  const request = options.request;
  options.request = async path => path.endsWith('/runs/123') && options.now() >= 120_000
    ? { ...successfulRun, run_attempt: 3, status: 'in_progress', conclusion: null } : request(path);
  await assert.rejects(waitForValidation(options), /Timed out waiting for CI rerun/);
  assert.equal(options.now(), 150_000);
});

for (const event of ['push', 'workflow_run']) {
  test(`${event} missing evidence also respects the shorter overall deadline`, async () => {
    const caller = { ...context, event, ...(event === 'workflow_run' ? { trigger: successfulRun } : {}) };
    const { options } = fixture({ context: caller, jobs: [] });
    await assert.rejects(waitForValidation(options), /Timed out waiting for one successful ci-required/);
    assert.equal(options.now(), 30);
  });
}

test('an empty run list after missing job evidence cannot keep polling past the evidence deadline', async () => {
  const { options } = fixture({ jobs: [] });
  options.timeoutMs = 1_800_000;
  options.pollMs = 15_000;
  const request = options.request;
  options.request = async path => path.includes('/workflows/ci.yml/runs?') && options.now() >= 120_000
    ? { workflow_runs: [] } : request(path);
  await assert.rejects(waitForValidation(options), /Timed out waiting/);
  assert.equal(options.now(), 120_000);
});
