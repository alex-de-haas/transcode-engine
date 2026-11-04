import { execFileSync } from 'node:child_process';
import { identity } from './context.mjs';

/** Claim each first version alias durably. Same-version rebuilds retain its original digest. */
export async function publishVersionAliases({ store, app, version, artifacts, verify, run = execFileSync }) {
  if (!/^[\w][\w.-]{0,127}$/.test(version ?? '')) throw new Error('Version cannot be represented by a registry alias.');
  const unique = new Map(artifacts.map(a => [a.repository, a]));
  for (const [repository, artifact] of unique) {
    const path = `artifacts/version-aliases/${identity({ repository, version })}.json`;
    await store.update(`retain image version alias ${repository}:${version}`, head => {
      if (store.json(head, path)) return {};
      return { [path]: { immutable: true, value: { repository, version, digest: artifact.digest, platforms: artifact.platforms, appId: app.id } } };
    });
    const claim = store.json(store.refresh(), path);
    let current;
    try { current = await verify(repository, version, artifact.platforms); }
    catch (error) {
      // An access error is not proof of absence and must never authorize an overwrite.
      if (!/HTTP 404\b/.test(error.message)) throw error;
    }
    if (current) continue; // Includes old aliases created before metadata adoption.
    await verify(repository, claim.digest, claim.platforms);
    run('docker', ['buildx', 'imagetools', 'create', '--prefer-index=false', '--tag', `${repository}:${version}`, `${repository}@${claim.digest}`], { stdio: 'inherit' });
    const published = await verify(repository, version, artifact.platforms);
    if (published.digest !== claim.digest) throw new Error('Published version alias differs from its retained claim.');
  }
}
