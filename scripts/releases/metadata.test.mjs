import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, mkdirSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { MetadataStore } from './metadata.mjs';
import { canonical } from './context.mjs';

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'hosty-metadata-test-')), repository = join(root, 'source'), remote = join(root, 'remote.git');
  execFileSync('git', ['init', '--bare', remote], { stdio: 'ignore' });
  execFileSync('git', ['clone', remote, repository], { stdio: 'ignore' });
  return { root, repository, store: new MetadataStore(repository, 'app-releases') };
}
test('concurrent app writes retry with a fresh tree and preserve both releases', async () => {
  const f = fixture();
  try {
    const other = new MetadataStore(f.repository, 'app-releases'); let raced = false, attempts = 0;
    await f.store.update('retain A', async () => {
      attempts++;
      if (!raced) { raced = true; await other.update('retain B', () => ({ 'apps/b/release.json': { value: { id: 'b' }, immutable: true } })); }
      return { 'apps/a/release.json': { value: { id: 'a' }, immutable: true } };
    });
    assert.equal(attempts, 2);
    const head = f.store.refresh(); assert.equal(f.store.json(head, 'apps/a/release.json').id, 'a'); assert.equal(f.store.json(head, 'apps/b/release.json').id, 'b');
    const original = head;
    assert.equal(await f.store.update('idempotent retry', () => ({ 'apps/a/release.json': { value: { id: 'a' }, immutable: true } })), original);
    await assert.rejects(f.store.update('collision', () => ({ 'apps/a/release.json': { value: { id: 'different' }, immutable: true } })), /Immutable release collision/);
    assert.equal(f.store.refresh(), original);
  } finally { rmSync(f.root, { recursive: true, force: true }); }
});
test('an uncertain successful push is recovered without creating another publication', async () => {
  const f = fixture(); let interrupted = false, pushes = 0;
  try {
    const run = f.store.run;
    f.store.run = (args, options) => {
      const result = run(args, options);
      if (args[0] === 'push') { pushes++; if (!interrupted) { interrupted = true; throw new Error('Lost transport response after acceptance.'); } }
      return result;
    };
    const head = await f.store.update('retain', () => ({ 'platform/release.json': { value: { id: 'one' }, immutable: true } }));
    assert.equal(f.store.refresh(), head); assert.equal(pushes, 1);
  } finally { rmSync(f.root, { recursive: true, force: true }); }
});
