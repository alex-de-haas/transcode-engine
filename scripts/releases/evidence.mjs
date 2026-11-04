import { execFileSync } from 'node:child_process';
import { mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fullCommit, canonical } from './context.mjs';

export const predicateType = 'https://hosty.dev/attestations/release-artifact/v1';
export function artifactPredicate({ sourceCommit, inputFingerprint, repository, validationRunId, artifactId, platforms }) {
  if (!fullCommit(sourceCommit) || !/^[a-f0-9]{64}$/.test(inputFingerprint) || !/^[1-9]\d*$/.test(String(validationRunId)) || !/^[\w.-]+\/[\w.-]+$/.test(repository)) throw new Error('Incomplete artifact provenance.');
  return { schemaVersion: 'hosty-artifact-provenance.0.1', repository, sourceCommit, inputFingerprint,
    artifactId, platforms, validation: { workflow: '.github/workflows/ci.yml', runId: String(validationRunId) } };
}
export function verifyProvenance(subject, receipt, repository, { run = execFileSync } = {}) {
  const options = { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 };
  const image = subject.startsWith('oci://');
  const artifact = image ? subject : resolve(subject);
  const directory = image ? null : mkdtempSync(join(tmpdir(), 'hosty-attestations-'));
  let results;
  try {
    let bundle = ['--bundle-from-oci'];
    if (!image) {
      // Fetch without a custom predicate query; enforce the predicate during signature verification.
      run('gh', ['attestation', 'download', artifact, '--repo', repository], { ...options, cwd: directory });
      const files = readdirSync(directory).filter(name => /^sha256[:-][a-f0-9]{64}\.jsonl$/.test(name));
      if (files.length !== 1) throw new Error('Expected one downloaded artifact attestation bundle.');
      bundle = ['--bundle', join(directory, files[0])];
    }
    const output = run('gh', ['attestation', 'verify', artifact, '--repo', repository, ...bundle,
      '--signer-workflow', `${repository}/.github/workflows/ready-releases.yml`, '--source-ref', 'refs/heads/main',
      '--predicate-type', predicateType, '--deny-self-hosted-runners', '--format', 'json'], options);
    results = JSON.parse(output);
  } finally {
    if (directory) rmSync(directory, { recursive: true, force: true });
  }
  const match = results.find(result => {
    const predicate = result.verificationResult?.statement?.predicate;
    return predicate?.schemaVersion === 'hosty-artifact-provenance.0.1' && predicate.repository === repository
      && predicate.artifactId === receipt.artifactId
      && canonical(predicate.platforms?.slice().sort()) === canonical((receipt.platforms ?? [receipt.rid]).slice().sort())
      && predicate.sourceCommit === receipt.buildSourceCommit && predicate.inputFingerprint === receipt.inputFingerprint
      && predicate.validation?.workflow === '.github/workflows/ci.yml' && /^[1-9]\d*$/.test(predicate.validation?.runId ?? '');
  });
  if (!match) throw new Error('Signed artifact provenance does not match the retained source/input fingerprint.');
  return match.verificationResult.statement.predicate;
}
