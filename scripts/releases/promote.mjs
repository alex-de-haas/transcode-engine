import { readFileSync } from 'node:fs';
import { MetadataStore } from './metadata.mjs';
import { validatePublisher } from './model.mjs';
import { promote } from './publish.mjs';

const config = validatePublisher(JSON.parse(readFileSync('distribution/release-publisher.json')));
const { RELEASE_TARGET: target, RELEASE_ID: releaseId, RELEASE_FEED: feedId, EXPECTED_RELEASE: expected,
  STABILIZATION_EVIDENCE: evidence, GITHUB_REPOSITORY: repository, PROMOTION_ID: promotionId } = process.env;
if (target !== 'platform' && !config.apps.some(a => a.id === target)) throw new Error('Unknown release target.');
if (!/^[a-f0-9]{64}$/.test(releaseId ?? '')) throw new Error('Select an exact completed release ID.');
const store = new MetadataStore(process.cwd(), config.metadataBranch), head = store.refresh();
const prefix = target === 'platform' ? 'platform' : `apps/${target}`, path = `${prefix}/releases/${releaseId}/release.json`;
const descriptor = store.json(head, path); if (!descriptor) throw new Error('The selected completed release does not exist.');
const manifest = target === 'platform' ? null : store.json(head, `${prefix}/releases/${releaseId}/manifest.json`);
const metadataCommit = store.run(['log', '--format=%H', '--diff-filter=A', head, '--', path]).split('\n').at(-1);
await promote({ store, config, repository, target, release: { descriptor, manifest }, metadataCommit, feedId, kind: 'manual',
  expected, evidence, actor: process.env.GITHUB_ACTOR, promotionId });
