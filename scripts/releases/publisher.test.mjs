import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, mkdirSync, rmSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { projectNpm, buildContext, materializeContext, identity, reusableArtifact } from './context.mjs';
import { validatePublisher, appRelease, platformRelease, promotionDecision, feedDocument } from './model.mjs';
import { imageReceipt } from './artifacts.mjs';
const hash = 'a'.repeat(64), commit = 'b'.repeat(40);
const config = count => ({ schemaVersion: 'hosty-publisher.0.1', metadataBranch: 'artifacts', feeds: Array.from({ length: count }, (_, i) => ({ id: `feed-${i}`, policy: i ? 'manual-promotion' : 'validated-source', default: i === count - 1 })), apps: [], images: [] });

test('publisher names and counts are data; duplicate/default/control IDs fail', () => {
  for (const count of [1,3,10]) assert.equal(validatePublisher(config(count)).feeds.length, count);
  const c = config(2); c.feeds[1].id = c.feeds[0].id; assert.throws(() => validatePublisher(c), /duplicate/);
  c.feeds[1].id = 'bad\u0085id'; assert.throws(() => validatePublisher(c), /Invalid/);
});
test('first automatic pointer is retained but not advertised ahead of the configured default', () => {
  const c = config(2); assert.equal(feedDocument(c, 'example.app', { 'feed-0': { manifestRef: 'https://example.test/1' } }), null);
  assert.equal(feedDocument(c, 'example.app', { 'feed-0': { manifestRef: 'https://example.test/1' }, 'feed-1': { manifestRef: 'https://example.test/2' } }).feeds.length, 2);
  assert.equal(feedDocument(config(1), 'example.app', { 'feed-0': { manifestRef: 'https://example.test/1' } }).feeds[0].default, true);
});
test('manual promotion is exact, guarded and cannot be bypassed by automatic callers', () => {
  const options = { config: config(2), feedId: 'feed-1', kind: 'manual', releaseId: hash, current: 'old', expected: 'old', evidence: 'Verified acceptance', sourceCommit: commit, currentSource: 'c'.repeat(40), isAncestor: () => true };
  assert.equal(promotionDecision(options), true);
  assert.equal(promotionDecision({ ...options, current: hash, expected: 'stale' }), false);
  assert.throws(() => promotionDecision({ ...options, kind: 'automatic' }), /manual/);
  assert.throws(() => promotionDecision({ ...options, expected: 'stale' }), /pointer changed/);
  assert.throws(() => promotionDecision({ ...options, isAncestor: () => false }), /regress/);
});
function lockFixture() {
  const archive = (dependencies = {}) => ({ version: '1.0.0', resolved: 'https://registry.example.test/a.tgz', integrity: 'sha512-proof', dependencies });
  return { lockfileVersion: 3, packages: { '': { workspaces: ['apps/a','apps/b','packages/sdk'] },
    'apps/a': { name: 'a', dependencies: { sdk: '*', x: '^1' } }, 'apps/b': { name: 'b', dependencies: { unrelated: '^1' } },
    'packages/sdk': { name: 'sdk', peerDependencies: { peer: '^1' }, optionalDependencies: { optional: '^1' } },
    'node_modules/a': { link: true, resolved: 'apps/a' }, 'node_modules/b': { link: true, resolved: 'apps/b' },
    'node_modules/sdk': { link: true, resolved: 'packages/sdk' }, 'node_modules/x': archive({ nested: '^1' }),
    'node_modules/x/node_modules/nested': archive(), 'node_modules/peer': archive(), 'node_modules/optional': archive(), 'node_modules/unrelated': archive() } };
}
test('npm projection retains actual nested, peer, optional and workspace closure only', () => {
  const lock = lockFixture(), root = { workspaces: lock.packages[''].workspaces, overrides: { x: '^1' }, scripts: { unrelated: 'anything' } };
  const projection = projectNpm(root, lock, ['apps/a']);
  assert.deepEqual(projection.workspaces, ['apps/a','packages/sdk']);
  assert.ok(projection.lock.packages['node_modules/x/node_modules/nested']); assert.ok(projection.lock.packages['node_modules/peer']); assert.ok(projection.lock.packages['node_modules/optional']);
  assert.equal(projection.lock.packages['apps/b'], undefined); assert.equal(projection.lock.packages['node_modules/unrelated'], undefined);
  const before = identity(projection); lock.packages['apps/b'].version = '2.0.0'; root.scripts.unrelated = 'changed';
  assert.equal(identity(projectNpm(root, lock, ['apps/a'])), before);
  lock.packages['node_modules/peer'].version = '2.0.0'; assert.notEqual(identity(projectNpm(root, lock, ['apps/a'])), before);
  delete lock.packages['node_modules/peer'].integrity; assert.throws(() => projectNpm(root, lock, ['apps/a']), /integrity/);
});
test('fingerprints describe materialized bytes, modes, renames and deletions', () => {
  const root = mkdtempSync(join(tmpdir(), 'hosty-context-test-'));
  try {
    mkdirSync(join(root, 'apps/a'), { recursive: true }); writeFileSync(join(root, 'Dockerfile'), 'COPY apps/a apps/a\n'); writeFileSync(join(root, '.dockerignore'), 'docs\n'); writeFileSync(join(root, 'apps/a/a'), 'same');
    const recipe = { inputs: ['apps/a'], dockerfile: 'Dockerfile', platforms: ['linux/amd64'] }, paths = ['apps/a/a','Dockerfile','.dockerignore'];
    const original = buildContext(root, recipe, { paths }); materializeContext(original, join(root, 'output'));
    assert.equal(readFileSync(join(root, 'output/apps/a/a'), 'utf8'), 'same');
    rmSync(join(root, 'apps/a/a')); writeFileSync(join(root, 'apps/a/b'), 'same');
    assert.notEqual(buildContext(root, recipe, { paths: [...paths, 'apps/a/b'] }).fingerprint, original.fingerprint);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
test('digest reuse requires exact proof format, full build revision and complete platforms', () => {
  const recipe = { reusable: true, repository: 'example/app', platforms: ['linux/amd64','linux/arm64'] };
  const receipt = { schemaVersion: 'hosty-image-artifact.0.1', repository: recipe.repository, digest: `sha256:${hash}`, inputFingerprint: hash, buildSourceCommit: commit, provenance: 'https://example.test/proof', platforms: recipe.platforms };
  assert.equal(reusableArtifact(receipt, recipe, hash), true);
  for (const invalid of [{ ...receipt, platforms: ['linux/amd64'] }, { ...receipt, buildSourceCommit: 'abcd' }, { ...receipt, provenance: null }, { ...receipt, inputFingerprint: 'different' }]) assert.equal(reusableArtifact(invalid, recipe, hash), false);
  assert.equal(reusableArtifact(receipt, { ...recipe, reusable: false }, hash), false);
});
test('mixed-service releases retain original build revisions and ignore unrelated candidate HEAD', () => {
  const app = { id: 'example.telemetry', profiles: ['docker'] }, manifest = { id: app.id, version: '1.0.0', source: { repository: 'https://example.test/repo' }, runtimeProfiles: [{ key: 'docker', type: 'docker' }], services: ['backend','ui'].map(key => ({ key, runtimes: { docker: { type: 'docker', image: { repository: `example/${key}`, tag: 'latest' } } } })) };
  const artifacts = ['backend','ui'].map((service, i) => ({ type: 'image', service, profile: 'docker', repository: `example/${service}`, digest: `sha256:${String(i).repeat(64)}`, platforms: ['linux/arm64'], buildSourceCommit: i ? commit : 'c'.repeat(40), inputFingerprint: hash }));
  const first = appRelease({ manifest, app, sourceCommit: commit, sourceFingerprint: hash, artifacts });
  assert.equal(first.manifest.schemaVersion, 'app.0.2'); assert.equal(first.manifest.services[0].runtimes.docker.image.tag, undefined);
  assert.equal(first.descriptor.releaseId, appRelease({ manifest, app, sourceCommit: 'd'.repeat(40), sourceFingerprint: hash, artifacts }).descriptor.releaseId);
  assert.throws(() => appRelease({ manifest, app, sourceCommit: commit, sourceFingerprint: hash, artifacts: artifacts.slice(1) }), /Missing verified/);
});
test('native release requires every role/RID and derives immutable URLs after identity', () => {
  const assets = ['cli','core'].map(role => ({ role, rid: 'custom-rid', fileName: role, size: 5, sha256: hash, inputFingerprint: hash, provenance: 'https://example.test/proof' }));
  const options = { version: '1.0.0', sourceCommit: commit, assets, repository: 'example/repo', rids: ['custom-rid'] };
  const release = platformRelease(options); assert.ok(release.descriptor.assets[0].url.includes(release.descriptor.releaseId));
  assert.throws(() => platformRelease({ ...options, assets: assets.slice(1) }), /Incomplete/);
});
test('build results must belong to the selected plan', () => {
  const plan = { sourceCommit: commit, validationRunId: '42', repository: 'example/repo', images: [{ recipe: { id: 'api', repository: 'example/api', platforms: ['linux/amd64'] }, inputFingerprint: hash, decision: 'build' }] };
  assert.equal(imageReceipt(plan, 'api', `sha256:${hash}`).receipt.buildSourceCommit, commit);
  assert.throws(() => imageReceipt(plan, 'other', `sha256:${hash}`), /Unexpected/);
});
