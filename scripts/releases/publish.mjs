import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomUUID } from 'node:crypto';
import { canonical, sha256, fullCommit, identity } from './context.mjs';
import { appRelease, platformRelease, feedDocument, promotionDecision, validatePublisher } from './model.mjs';
import { imageReference, verifyImage, bytes } from './registry.mjs';
import { verifyProvenance } from './evidence.mjs';
import { MetadataStore } from './metadata.mjs';
import { publishVersionAliases } from './version-aliases.mjs';
import { releaseFiles } from './assets.mjs';
import { validateCombination } from './validate.mjs';

const rootFor = target => target === 'platform' ? 'platform' : `apps/${target}`;
const raw = (repository, commit, path) => `https://raw.githubusercontent.com/${repository}/${commit}/${path}`;
export async function verifyMetadata(url, expected, request = fetch) {
  const response = await request(url, { signal: AbortSignal.timeout(60_000) });
  if (!response.ok || !response.url.startsWith('https://')) throw new Error(`Release metadata is not consumer-readable: ${response.status}`);
  if (sha256(await bytes(response, 4 * 1024 * 1024)) !== sha256(expected)) throw new Error('Published immutable metadata bytes differ.');
}

export async function verifyNativeAssets(descriptor, repository, { request = fetch, prove = verifyProvenance } = {}) {
  // A native promotion rechecks retained bytes and original signatures, never rebuilds them.
    for (const asset of descriptor.assets) {
      const response = await request(asset.url, { signal: AbortSignal.timeout(120_000) });
      if (!response.ok || !response.url.startsWith('https://')) throw new Error(`Native artifact unavailable: ${asset.fileName}`);
      const data = await bytes(response, asset.size);
      if (data.length !== asset.size || sha256(data) !== asset.sha256) throw new Error('Native artifact checksum/size mismatch.');
      const { mkdtempSync, rmSync } = await import('node:fs'); const { tmpdir } = await import('node:os');
      const temporary = mkdtempSync(join(tmpdir(), 'hosty-native-proof-'));
      try { const file = join(temporary, asset.fileName); writeFileSync(file, data); await prove(file, { ...asset, buildSourceCommit: descriptor.sourceCommit }, repository); }
      finally { rmSync(temporary, { recursive: true, force: true }); }
    }
}

export async function promote({ store, config, repository, target, release, metadataCommit, feedId, kind,
  expected, evidence, actor, promotionId, verify = verifyImage, prove = verifyProvenance, request = fetch }) {
  const prefix = rootFor(target), descriptor = release.descriptor;
  if (!fullCommit(metadataCommit) || !/^[a-f0-9]{64}$/.test(descriptor.releaseId) || !/^[a-f0-9-]{36}$/.test(promotionId ?? '')) throw new Error('Promotion needs exact retained identities.');
  await verifyMetadata(raw(repository, metadataCommit, `${prefix}/releases/${descriptor.releaseId}/release.json`), canonical(descriptor) + '\n', request);
  if (target !== 'platform') {
    await verifyMetadata(raw(repository, metadataCommit, `${prefix}/releases/${descriptor.releaseId}/manifest.json`), canonical(release.manifest) + '\n', request);
    for (const file of descriptor.files ?? []) {
      const response = await request(raw(repository, metadataCommit, `${prefix}/releases/${descriptor.releaseId}/${file.path}`), { signal: AbortSignal.timeout(60_000) });
      if (!response.ok || sha256(await bytes(response, file.size)) !== file.sha256) throw new Error(`Published release asset is unavailable or corrupt: ${file.path}`);
    }
    for (const artifact of descriptor.artifacts.filter(a => a.type === 'image')) {
      await verify(artifact.repository, artifact.digest, artifact.platforms);
      if (artifact.buildSourceCommit) await prove(`oci://${artifact.repository}@${artifact.digest}`, artifact, repository);
    }
  } else {
    await verifyNativeAssets(descriptor, repository, { request, prove });
  }
  const pointer = target === 'platform' ? { releaseRef: raw(repository, metadataCommit, `${prefix}/releases/${descriptor.releaseId}/release.json`), releaseSha256: sha256(canonical(descriptor) + '\n') }
    : { manifestRef: raw(repository, metadataCommit, `${prefix}/releases/${descriptor.releaseId}/manifest.json`) };
  return store.update(`promote ${target} to ${feedId}`, head => {
    const statePath = `${prefix}/feed-state.json`, state = store.json(head, statePath) ?? { feeds: {} };
    const prior = state.feeds[feedId];
    const recorded = store.json(head, `${prefix}/promotions/${promotionId}.json`);
    if (recorded) {
      if (recorded.target !== target || recorded.feedId !== feedId || recorded.releaseId !== descriptor.releaseId
        || recorded.kind !== kind || recorded.evidence !== (evidence ?? null)) throw new Error('Promotion request ID is already bound to another decision.');
      return {};
    }
    if (!promotionDecision({ config, feedId, kind, releaseId: descriptor.releaseId, current: prior?.releaseId, expected, evidence,
      sourceCommit: descriptor.sourceCommit, currentSource: prior?.sourceCommit, isAncestor: (a,b) => store.ancestor(a,b) })) return {};
    state.feeds[feedId] = { releaseId: descriptor.releaseId, sourceCommit: descriptor.sourceCommit, metadataCommit, pointer };
    // On first manual default promotion, seed automatic feeds to the same complete candidate.
    if (config.feeds.find(f => f.id === feedId)?.default && !store.json(head, `${prefix}/feeds.json`)) {
      for (const feed of config.feeds.filter(f => f.policy === 'validated-source')) state.feeds[feed.id] ??= state.feeds[feedId];
    }
    const index = feedDocument(config, target, Object.fromEntries(Object.entries(state.feeds).map(([id, value]) => [id, value.pointer])));
    const changes = { [statePath]: { value: state }, [`${prefix}/promotions/${promotionId}.json`]: { immutable: true, value: {
      schemaVersion: 'hosty-feed-promotion.0.1', promotionId, target, feedId, kind, previousReleaseId: prior?.releaseId ?? null,
      releaseId: descriptor.releaseId, actor, at: new Date().toISOString(), evidence: evidence ?? null } } };
    if (index) {
      changes[`${prefix}/feeds.json`] = { value: index };
      const defaultFeed = config.feeds.find(f => f.default) ?? (config.feeds.length === 1 ? config.feeds[0] : null);
      const selectedDefault = defaultFeed && state.feeds[defaultFeed.id];
      if (target !== 'platform' && selectedDefault) {
        changes[`${prefix}/install.json`] = { value: { schemaVersion: 'hosty-app-install-source.0.1', appId: target,
          feedsUrl: raw(repository, config.metadataBranch, `${prefix}/feeds.json`), feedId: defaultFeed.id,
          manifestRef: selectedDefault.pointer.manifestRef, releaseId: selectedDefault.releaseId } };
      }
    }
    return changes;
  });
}

export async function publishApps({ root, plan, artifacts, store, verify = verifyImage, prove = verifyProvenance, request = fetch, aliases = publishVersionAliases, validate = validateCombination }) {
  const receipts = new Map(), errors = new Map(), failures = []; 
  for (const image of plan.images.filter(i => i.decision !== 'unchanged-app')) {
    try {
    const receipt = image.decision === 'reuse' ? image.previous : JSON.parse(readFileSync(join(artifacts, `${image.recipe.id}.json`)));
    if (receipt.inputFingerprint !== image.inputFingerprint || receipt.repository !== image.recipe.repository
      || (image.decision === 'build' && receipt.buildSourceCommit !== plan.sourceCommit)) throw new Error('Image result does not belong to this qualified build plan.');
    await verify(receipt.repository, receipt.digest, image.recipe.platforms);
    await prove(`oci://${receipt.repository}@${receipt.digest}`, receipt, plan.repository); receipts.set(image.recipe.id, receipt);
    } catch (error) { errors.set(image.recipe.id, error); }
  }
  for (const selected of plan.apps.filter(a => a.selected)) {
    const app = selected.app, artifactsForApp = [];
    try {
    for (const image of plan.images.filter(i => i.recipe.appId === app.id)) if (errors.has(image.recipe.id)) throw errors.get(image.recipe.id);
    if (selected.recoveryOnly) {
      const descriptor = store.json(store.refresh(), `apps/${app.id}/releases/${selected.previous.releaseId}/release.json`);
      const manifest = store.json(store.refresh(), `apps/${app.id}/releases/${selected.previous.releaseId}/manifest.json`);
      if (!descriptor || !manifest) throw new Error('Retained completed release is missing.');
      for (const feed of plan.config.feeds.filter(f => f.policy === 'validated-source')) await promote({ store, config: plan.config, repository: plan.repository, target: app.id,
        release: { descriptor, manifest }, metadataCommit: selected.previous.metadataCommit, feedId: feed.id, kind: 'automatic',
        actor: process.env.GITHUB_ACTOR ?? 'publisher', promotionId: randomUUID(), verify, prove, request });
      continue;
    }
    for (const service of selected.manifest.services) for (const [profile, runtime] of Object.entries(service.runtimes)) {
      if ((!app.profiles.includes(profile) && !selected.manifest.runtimeProfiles.find(p => p.key === profile)?.development) || runtime.type !== 'docker' || !runtime.image) continue;
      const recipe = plan.config.images.find(i => i.appId === app.id && i.services.includes(service.key));
      if (recipe) artifactsForApp.push({ type: 'image', service: service.key, profile, ...receipts.get(recipe.id) });
      else {
        const reference = imageReference(runtime.image);
        const proof = await verify(reference.repository, reference.reference, ['linux/amd64','linux/arm64']);
        artifactsForApp.push({ type: 'image', service: service.key, profile, ...proof, fingerprintFormat: 'external-image-digest.0.1', inputFingerprint: proof.digest.slice(7) });
      }
    }
    const previous = selected.previous ? store.json(store.refresh(), `apps/${app.id}/releases/${selected.previous.releaseId}/release.json`) : null;
    const files = releaseFiles(root, app, selected.manifest);
    const release = appRelease({ files: files.map(({ data: _data, ...f }) => f), manifest: selected.manifest, app, sourceCommit: plan.sourceCommit,
      sourceFingerprint: selected.sourceFingerprint, artifacts: artifactsForApp, previous });
    await validate({ root, app, release, files });
    release.descriptor.validation = { sourceCommit: plan.sourceCommit, runId: plan.validationRunId,
      manifestSha256: release.descriptor.manifestSha256, profiles: app.profiles, contract: 'app.0.2' };
    await aliases({ store, app, version: release.manifest.version, artifacts: artifactsForApp.filter(a => a.buildSourceCommit), verify });
    const prefix = `apps/${app.id}`, releasePath = `${prefix}/releases/${release.descriptor.releaseId}`;
    // A retry retains the original completion record's validation/source evidence byte-for-byte.
    const retained = store.json(store.refresh(), `${releasePath}/release.json`);
    if (retained) release.descriptor = retained;
    const metadataCommit = await store.update(`retain ${app.id} ${release.descriptor.releaseId}`, () => {
      const changes = { [`${releasePath}/manifest.json`]: { immutable: true, value: release.manifest }, [`${releasePath}/release.json`]: { immutable: true, value: release.descriptor } };
      for (const file of files) changes[`${releasePath}/${file.path}`] = { immutable: true, value: file.data, mode: file.mode };
      for (const image of plan.images.filter(i => i.recipe.appId === app.id && receipts.has(i.recipe.id))) {
        const receipt = receipts.get(image.recipe.id);
        changes[`artifacts/images/${image.recipe.id}/${receipt.inputFingerprint}/${receipt.digest.slice(7)}.json`] = { immutable: true, value: receipt };
      }
      return changes;
    });
    await verifyMetadata(raw(plan.repository, metadataCommit, `${releasePath}/manifest.json`), canonical(release.manifest) + '\n', request);
    await store.update(`record completed candidate ${app.id}`, head => {
      const current = store.json(head, `${prefix}/candidate.json`);
      if (current?.sourceCommit && current.sourceCommit !== plan.sourceCommit && !store.ancestor(current.sourceCommit, plan.sourceCommit)) return {};
      const changes = { [`${prefix}/candidate.json`]: { value: { releaseId: release.descriptor.releaseId, metadataCommit,
        sourceCommit: plan.sourceCommit, inputFingerprint: selected.inputFingerprint } } };
      for (const image of plan.images.filter(i => i.recipe.appId === app.id && receipts.has(i.recipe.id))) changes[`artifacts/images/${image.recipe.id}/current.json`] = { value: receipts.get(image.recipe.id) };
      return changes;
    });
    for (const feed of plan.config.feeds.filter(f => f.policy === 'validated-source')) {
      await promote({ store, config: plan.config, repository: plan.repository, target: app.id, release, metadataCommit, feedId: feed.id,
        kind: 'automatic', actor: process.env.GITHUB_ACTOR ?? 'publisher', promotionId: randomUUID(), verify, prove, request });
    }
    } catch (error) { failures.push(new Error(`${app.id}: ${error.message}`, { cause: error })); }
  }
  if (failures.length) throw new AggregateError(failures, failures.map(e => e.message).join('\n'));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const root = process.cwd(), artifacts = resolve(process.env.ARTIFACT_OUTPUT ?? 'release-artifacts');
  const plan = JSON.parse(readFileSync(process.env.RELEASE_PLAN ?? 'release-plan/release-plan.json'));
  const store = new MetadataStore(root, plan.config.metadataBranch);
  await publishApps({ root, plan, artifacts, store });
}
