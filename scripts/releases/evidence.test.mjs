import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, writeFileSync } from 'node:fs';
import { join, isAbsolute } from 'node:path';
import { verifyProvenance, artifactPredicate, predicateType } from './evidence.mjs';

const source = 'a'.repeat(40), fingerprint = 'b'.repeat(64);
const receipt = { artifactId: 'cli', buildSourceCommit: source, inputFingerprint: fingerprint, rid: 'win-x64' };
const result = JSON.stringify([{ verificationResult: { statement: { predicate: artifactPredicate({ sourceCommit: source,
  inputFingerprint: fingerprint, repository: 'example/repo', validationRunId: 42, artifactId: 'cli', platforms: ['win-x64'] }) } } }]);

for (const separator of [':', '-']) test(`native provenance downloads unfiltered bundles and verifies policy (${separator})`, () => {
  let directory, verified = false;
  const run = (_exe, args, options) => {
    if (args[1] === 'download') {
      assert.ok(isAbsolute(args[2]));
      assert.ok(!args.includes('--predicate-type'));
      directory = options.cwd;
      writeFileSync(join(directory, `sha256${separator}${fingerprint}.jsonl`), '{}');
      return '';
    }
    verified = true;
    assert.ok(existsSync(args[args.indexOf('--bundle') + 1]));
    assert.equal(args[args.indexOf('--predicate-type') + 1], predicateType);
    assert.ok(args.includes('--deny-self-hosted-runners'));
    assert.equal(args[args.indexOf('--signer-workflow') + 1], 'example/repo/.github/workflows/ready-releases.yml');
    assert.equal(args[args.indexOf('--source-ref') + 1], 'refs/heads/main');
    return result;
  };
  assert.equal(verifyProvenance('hosty.exe', receipt, 'example/repo', { run }).sourceCommit, source);
  assert.ok(verified);
  assert.ok(!existsSync(directory));
});

test('failed bundle verification removes temporary material without accepting provenance', () => {
  let directory;
  const run = (_exe, args, options) => {
    if (args[1] === 'download') {
      directory = options.cwd;
      writeFileSync(join(directory, `sha256-${fingerprint}.jsonl`), '{}');
      return '';
    }
    throw new Error('untrusted signer');
  };
  assert.throws(() => verifyProvenance('hosty.exe', receipt, 'example/repo', { run }), /untrusted signer/);
  assert.ok(!existsSync(directory));
});

test('OCI proof reads registry bundles with the same signing policy', () => {
  const run = (_exe, args) => {
    assert.equal(args[1], 'verify');
    assert.ok(args.includes('--bundle-from-oci'));
    assert.equal(args[args.indexOf('--predicate-type') + 1], predicateType);
    return result;
  };
  verifyProvenance(`oci://example.test/cli@sha256:${fingerprint}`, receipt, 'example/repo', { run });
});
