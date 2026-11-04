import { appendFileSync, readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const runningStatuses = new Set(['queued', 'in_progress', 'waiting', 'pending', 'requested']);
const jobEvidenceTimeoutMs = 120_000;

export function publicationContext(eventName, event, env) {
  if (eventName === 'workflow_run') return { ref: 'refs/heads/main', event: eventName,
    sha: event.workflow_run?.head_sha, repository: env.GITHUB_REPOSITORY, trigger: event.workflow_run };
  return { ref: env.GITHUB_REF, event: eventName, sha: env.GITHUB_SHA, repository: env.GITHUB_REPOSITORY };
}

export function validateContext(context) {
  if (context.ref !== 'refs/heads/main' || !['push', 'workflow_dispatch', 'workflow_run'].includes(context.event)) {
    throw new Error('Publication requires a push or manual dispatch on main; non-main publication is rejected.');
  }
  if (!/^[0-9a-f]{40}$/.test(context.sha ?? '') || !/^[\w.-]+\/[\w.-]+$/.test(context.repository ?? '')) {
    throw new Error('Publication requires a full commit SHA and repository identity.');
  }
  if (context.event === 'workflow_run') {
    validateRun(context.trigger ?? {}, context);
    if (context.trigger.status !== 'completed' || context.trigger.conclusion !== 'success') {
      throw new Error('Publication requires a successful completed main push CI trigger.');
    }
  }
}

function validateRun(run, context) {
  if (run.head_sha !== context.sha || run.head_branch !== 'main' || run.event !== 'push'
    || run.path !== '.github/workflows/ci.yml'
    || run.head_repository?.full_name?.toLowerCase() !== context.repository.toLowerCase()
    || !Number.isSafeInteger(run.id) || run.id <= 0
    || !Number.isSafeInteger(run.run_attempt) || run.run_attempt <= 0) {
    throw new Error('CI returned a run with a mismatched SHA, branch, workflow, event, or repository.');
  }
}

export async function waitForValidation({ context, request, timeoutMs = 1_800_000,
  pollMs = 15_000, now = Date.now, sleep = ms => new Promise(resolve => setTimeout(resolve, ms)),
  log = console.log }) {
  validateContext(context);
  if (!Number.isFinite(timeoutMs) || timeoutMs < 1 || timeoutMs > 3_600_000
    || !Number.isFinite(pollMs) || pollMs < 1) {
    throw new Error('CI validation wait must be bounded to at most one hour.');
  }
  const deadline = now() + timeoutMs;
  const repository = context.repository.split('/').map(encodeURIComponent).join('/');
  const runsPath = `/repos/${repository}/actions/workflows/ci.yml/runs?event=push&branch=main&head_sha=${context.sha}&per_page=100`;
  let waitingFor = 'CI run registration';
  let evidenceWait;
  while (now() < deadline) {
    const response = context.event === 'workflow_run'
      ? { workflow_runs: [await request(`/repos/${repository}/actions/runs/${context.trigger.id}`, deadline - now())] }
      : await request(runsPath, deadline - now());
    if (!Array.isArray(response.workflow_runs)) throw new Error('Malformed CI workflow run response.');
    for (const run of response.workflow_runs) validateRun(run, context);
    // A newer failed run must never be hidden by an older successful run for the same SHA.
    const run = response.workflow_runs.toSorted((a, b) => b.id - a.id)[0];
    if (run) {
      waitingFor = `CI run ${run.id} (${run.status})`;
      if (evidenceWait?.runId !== run.id || evidenceWait?.attempt !== run.run_attempt) evidenceWait = undefined;
      if (run.status === 'completed') {
        if (run.conclusion !== 'success') {
          throw new Error(`CI run ${run.id} concluded ${run.conclusion}; publication is blocked.`);
        }
        const jobs = [];
        for (let page = 1; page <= 10; page++) {
          const result = await request(`/repos/${repository}/actions/runs/${run.id}/attempts/${run.run_attempt}/jobs?per_page=100&page=${page}`, deadline - now());
          if (!Array.isArray(result.jobs)) throw new Error('Malformed CI jobs response.');
          jobs.push(...result.jobs);
          if (result.jobs.length < 100) break;
          if (page === 10) throw new Error('CI job list exceeds the validation bound.');
        }
        const required = jobs.filter(job => job.name === 'ci-required');
        const job = required[0];
        const evidence = `${required.length} matching jobs among ${jobs.length}; status ${job?.status ?? 'missing'}, conclusion ${job?.conclusion ?? 'none'}`;
        const ready = required.length === 1 && job.status === 'completed' && job.conclusion === 'success';
        // Run completion and job-list updates can become visible at different times. Retry only
        // absent or unfinished evidence; terminal failures and ambiguity still fail closed.
        if (required.length > 1 || job && !ready && !runningStatuses.has(job.status)) {
          throw new Error(`The exact CI attempt must contain one successful ci-required job. Observed ${evidence}.`);
        }
        // Re-read after inspecting jobs: a concurrent rerun must finish its current attempt too.
        const current = await request(`/repos/${repository}/actions/runs/${run.id}`, deadline - now());
        validateRun(current, context);
        if (current.id !== run.id) throw new Error('CI run identity changed during validation.');
        if (current.run_attempt !== run.run_attempt || current.status !== 'completed') {
          if (context.event === 'workflow_run') throw new Error('The triggering CI run is being rerun; wait for its next successful completion.');
          evidenceWait = undefined;
          waitingFor = `CI rerun ${run.id}`;
        } else if (current.conclusion !== 'success') {
          throw new Error(`CI run ${run.id} no longer succeeds; publication is blocked.`);
        } else if (!ready) {
          evidenceWait ??= { runId: run.id, attempt: run.run_attempt,
            deadline: Math.min(deadline, now() + jobEvidenceTimeoutMs) };
          waitingFor = `one successful ci-required job in CI run ${run.id}, attempt ${run.run_attempt} (${evidence})`;
          if (now() >= evidenceWait.deadline) {
            throw new Error(`Timed out waiting for ${waitingFor}; publication is blocked.`);
          }
        } else {
          log(`Validated CI run ${run.id}, attempt ${run.run_attempt}, for main commit ${context.sha}.`);
          return { runId: String(run.id), sourceSha: context.sha };
        }
      } else if (context.event === 'workflow_run') {
        throw new Error('The triggering CI run is no longer completed successfully; publication is blocked.');
      } else if (!runningStatuses.has(run.status)) {
        throw new Error(`Unrecognized CI status ${run.status}; publication is blocked.`);
      }
    }
    const remaining = Math.min(deadline, evidenceWait?.deadline ?? deadline) - now();
    if (remaining > 0) {
      log(`Waiting for ${waitingFor} for ${context.sha}.`);
      await sleep(Math.min(pollMs, remaining));
    } else {
      throw new Error(`Timed out waiting for ${waitingFor}; publication is blocked.`);
    }
  }
  throw new Error(`Timed out waiting for ${waitingFor} for exact main commit ${context.sha}; publication is blocked.`);
}

export function githubRequest({ token, apiUrl = 'https://api.github.com', fetchImpl = fetch }) {
  if (!token) throw new Error('The CI validation gate requires an actions:read token.');
  return async (path, remainingMs) => {
    if (remainingMs <= 0) throw new Error('CI validation deadline expired.');
    const response = await fetchImpl(`${apiUrl}${path}`, {
      headers: { Accept: 'application/vnd.github+json', Authorization: `Bearer ${token}`,
        'X-GitHub-Api-Version': '2022-11-28' },
      signal: AbortSignal.timeout(Math.max(1, Math.min(30_000, Math.ceil(remainingMs)))),
    });
    if (!response.ok) throw new Error(`GitHub CI lookup failed with HTTP ${response.status}; publication is blocked.`);
    return response.json();
  };
}

async function main() {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  const context = publicationContext(process.env.GITHUB_EVENT_NAME, event, process.env);
  const result = await waitForValidation({
    context,
    request: githubRequest({ token: process.env.CI_GATE_TOKEN, apiUrl: process.env.GITHUB_API_URL }),
    timeoutMs: Number(process.env.CI_GATE_TIMEOUT_SECONDS ?? '1800') * 1000,
  });
  if (!process.env.GITHUB_OUTPUT) throw new Error('Missing GitHub Actions output file.');
  appendFileSync(process.env.GITHUB_OUTPUT, `run-id=${result.runId}\nsource-sha=${result.sourceSha}\n`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
