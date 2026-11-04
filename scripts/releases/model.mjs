import { readFileSync } from 'node:fs';
import { canonical, identity, sha256, fullCommit, digest, relativePath } from './context.mjs';

export function validatePublisher(config) {
  if (config?.schemaVersion !== 'hosty-publisher.0.1' || !Array.isArray(config.feeds) || !config.feeds.length) throw new Error('Invalid publisher configuration.');
  const ids = new Set(); let defaults = 0;
  for (const feed of config.feeds) {
    if (typeof feed.id !== 'string' || !feed.id.trim() || feed.id !== feed.id.trim() || feed.id.length > 128 || /\p{Cc}/u.test(feed.id) || ids.has(feed.id)) throw new Error('Invalid or duplicate publisher feed ID.');
    ids.add(feed.id); if (feed.default && ++defaults > 1) throw new Error('Multiple publisher defaults.');
    if (!['validated-source', 'manual-promotion'].includes(feed.policy)) throw new Error('Unknown publication policy.');
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(config.metadataBranch ?? '') || config.metadataBranch.includes('..')) throw new Error('Invalid metadata branch.');
  const apps = new Set();
  for (const app of config.apps ?? []) {
    if (!/^[a-z0-9][a-z0-9._-]{0,62}$/.test(app.id ?? '') || apps.has(app.id)) throw new Error('Invalid or duplicate app target.');
    apps.add(app.id); relativePath(app.manifest);
    if (!app.profiles?.length) throw new Error('Every app needs production profiles.');
  }
  const images = new Set();
  for (const image of config.images ?? []) {
    if (!/^[a-z0-9][a-z0-9-]*$/.test(image.id ?? '') || images.has(image.id) || !apps.has(image.appId)) throw new Error('Invalid image target.');
    images.add(image.id); relativePath(image.dockerfile);
    if (!image.platforms?.length || new Set(image.platforms).size !== image.platforms.length) throw new Error('Image platforms are required.');
  }
  return config;
}

export function appRelease({ manifest, app, sourceCommit, sourceFingerprint, artifacts, previous, files = [] }) {
  if (!fullCommit(sourceCommit)) throw new Error('A qualified full source commit is required.');
  if (manifest.id !== app.id) throw new Error('Manifest identity differs from its publication target.');
  const emitted = structuredClone(manifest); emitted.schemaVersion = 'app.0.2';
  if (emitted.source) emitted.source.manifestPath = app.manifest;
  const keep = new Set(emitted.runtimeProfiles.filter(p => p.development || app.profiles.includes(p.key)).map(p => p.key));
  for (const profile of app.profiles) if (!emitted.runtimeProfiles.some(p => p.key === profile && !p.development)) throw new Error(`Missing production profile ${profile}.`);
  emitted.runtimeProfiles = emitted.runtimeProfiles.filter(p => keep.has(p.key));
  const identities = [];
  let hasSource = false;
  for (const service of emitted.services) {
    for (const [profile, runtime] of Object.entries(service.runtimes)) {
      if (!keep.has(profile)) { delete service.runtimes[profile]; continue; }
      if (runtime.type === 'docker' && runtime.image) {
        const artifact = artifacts.find(a => a.service === service.key && a.profile === profile);
        if (!artifact || !digest(artifact.digest) || !artifact.platforms?.length) throw new Error(`Missing verified image: ${service.key}/${profile}.`);
        runtime.image = { repository: artifact.repository, digest: artifact.digest };
        identities.push(artifact);
      } else if (runtime.type === 'localCommand' && app.profiles.includes(profile)) hasSource = true;
    }
  }
  if (hasSource) {
    const prior = previous?.source;
    const pin = app.sourceReusable === true && prior?.inputFingerprint === sourceFingerprint && fullCommit(prior.commit) ? prior.commit : sourceCommit;
    emitted.source = { ...emitted.source, commit: pin };
    identities.push({ type: 'source', commit: pin, inputFingerprint: sourceFingerprint, fingerprintFormat: 'hosty-source-inputs.0.1' });
  }
  identities.sort((a,b) => canonical(a).localeCompare(canonical(b), 'en'));
  // Source HEAD, validation-run identity and publication time do not manufacture a new release.
  const releaseId = identity({ manifest: emitted, artifacts: identities, files });
  return { manifest: emitted, descriptor: { schemaVersion: 'app-release.0.1', releaseId, appId: app.id,
    version: emitted.version, sourceCommit, manifestSha256: sha256(canonical(emitted) + '\n'), profiles: app.profiles,
    artifacts: identities, files, ...(hasSource ? { source: identities.find(a => a.type === 'source') } : {}) } };
}

export function platformRelease({ version, sourceCommit, assets, repository, rids }) {
  if (!fullCommit(sourceCommit) || !/^\d+\.\d+\.\d+(?:[-+][\w.-]+)?$/.test(version)) throw new Error('Invalid platform version/source.');
  const sorted = assets.slice().sort((a,b) => `${a.rid}/${a.role}`.localeCompare(`${b.rid}/${b.role}`, 'en'));
  if (sorted.length !== rids.length * 2 || new Set(sorted.map(a => `${a.rid}/${a.role}`)).size !== sorted.length) throw new Error('Incomplete native release matrix.');
  for (const rid of rids) for (const role of ['core','cli']) {
    const asset = sorted.find(a => a.role === role && a.rid === rid);
    if (!asset || !/^[a-f0-9]{64}$/.test(asset.sha256) || !/^[a-f0-9]{64}$/.test(asset.inputFingerprint) || !asset.provenance?.startsWith('https://') || !Number.isSafeInteger(asset.size) || asset.size <= 0 || !/^[\w.-]+$/.test(asset.fileName)) throw new Error(`Invalid native asset ${role}/${rid}.`);
  }
  // URLs contain the release ID and are deliberately excluded from its canonical input.
  const identityAssets = sorted.map(({ url: _url, ...a }) => a);
  const releaseId = identity({ version, sourceCommit, assets: identityAssets });
  const tag = `platform-v${version}-${releaseId}`;
  return { tag, descriptor: { schemaVersion: 'hosty-platform-release.0.1', releaseId, version, sourceCommit,
    assets: identityAssets.map(a => ({ ...a, url: `https://github.com/${repository}/releases/download/${tag}/${a.fileName}` })) } };
}

export function feedDocument(config, target, entries) {
  // The default must exist before any automatic feed becomes discoverable in a new distribution.
  const declaredDefault = config.feeds.find(f => f.default);
  if (declaredDefault && !entries[declaredDefault.id]) return null;
  const feeds = config.feeds.filter(f => entries[f.id]).map(f => ({ id: f.id, ...entries[f.id], ...(f.default ? { default: true } : {}) }));
  if (!feeds.length) return null;
  return target === 'platform' ? { schemaVersion: 'hosty-platform-feeds.0.1', feeds }
    : { schemaVersion: 'app-feeds.0.1', appId: target, feeds };
}

export function promotionDecision({ config, feedId, kind, releaseId, current, expected, evidence, sourceCommit, currentSource, isAncestor }) {
  const feed = config.feeds.find(f => f.id === feedId); if (!feed) throw new Error('Feed is not declared by this publisher.');
  if (kind === 'automatic' && feed.policy !== 'validated-source') throw new Error('This feed requires manual promotion.');
  if (kind !== 'automatic' && kind !== 'manual') throw new Error('Unknown promotion event.');
  if (kind === 'manual' && (typeof evidence !== 'string' || !evidence.trim())) throw new Error('Manual promotion needs stabilization evidence.');
  if (current === releaseId) return false;
  if (kind === 'manual' && expected !== (current ?? 'first')) throw new Error('The reviewed feed pointer changed; select again.');
  if (currentSource && sourceCommit !== currentSource && !isAncestor(currentSource, sourceCommit)) throw new Error('Promotion would regress or diverge from the selected feed.');
  return true;
}
