import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { buildContext, materializeContext, canonical, identity, sha256, fullCommit, reusableArtifact, trackedPaths, relativePath } from './context.mjs';
import { validatePublisher } from './model.mjs';
import { MetadataStore } from './metadata.mjs';
import { verifyImage } from './registry.mjs';
import { verifyProvenance } from './evidence.mjs';

export async function prepareRelease({ root, output, sourceCommit, validationRunId, repository, force = false,
  store, verify = verifyImage, prove = verifyProvenance, resolveExternal = true }) {
  if (!fullCommit(sourceCommit) || !/^[1-9]\d*$/.test(String(validationRunId)) || !/^[\w.-]+\/[\w.-]+$/.test(repository ?? '')) throw new Error('Preparation requires a qualified source and successful validation run.');
  const config = validatePublisher(JSON.parse(readFileSync(join(root, 'distribution/release-publisher.json'))));
  store ??= new MetadataStore(root, config.metadataBranch);
  const head = store.refresh(), paths = trackedPaths(root);
  const plan = { schemaVersion: 'hosty-release-plan.0.1', sourceCommit, validationRunId: String(validationRunId), repository,
    config, metadataHead: head, images: [], apps: [], platform: null, force };
  mkdirSync(output, { recursive: true });
  // A resolved builder image is also passed to setup-buildx, so it describes the actual builder.
  let builder, frontend;
  if (resolveExternal) {
    builder = await verify('moby/buildkit', 'buildx-stable-1', ['linux/amd64','linux/arm64']);
    frontend = await verify('docker/dockerfile', '1', ['linux/amd64','linux/arm64']);
  }
  for (const configured of config.images) {
    const recipe = structuredClone(configured);
    recipe.toolchain.contextImplementation = identity(['context.mjs','docker-inputs.mjs'].map(file => sha256(readFileSync(join(root, 'scripts/releases', file)))));
    recipe.toolchain.workflow = sha256(readFileSync(join(root, '.github/workflows/ready-releases.yml')));
    if (frontend) recipe.toolchain.frontend = `${frontend.repository}@${frontend.digest}`;
    if (builder) recipe.toolchain.builder = `${builder.repository}@${builder.digest}`;
    if (recipe.resolveBases) {
      recipe.buildArgs = {};
      for (const [arg, image] of Object.entries(recipe.resolveBases)) {
        const resolved = resolveExternal ? await verify(image.repository, image.tag, recipe.platforms) : image;
        recipe.buildArgs[arg] = `${resolved.repository}@${resolved.digest}`;
      }
    }
    const contextDirectory = recipe.context ?? '.';
    if (contextDirectory !== '.') relativePath(contextDirectory);
    const contextPaths = contextDirectory === '.' ? paths : paths.filter(p => p.startsWith(contextDirectory + '/')).map(p => p.slice(contextDirectory.length + 1));
    const context = buildContext(join(root, contextDirectory), recipe, { paths: contextPaths });
    const previous = store.json(head, `artifacts/images/${recipe.id}/current.json`);
    plan.images.push({ recipe: context.recipe, inputFingerprint: context.fingerprint, previous, decision: 'unchanged-app', reason: 'No effective release change.' });
    materializeContext(context, join(output, 'contexts', recipe.id));
    writeFileSync(join(output, `${recipe.id}.inputs.json`), canonical(context.record) + '\n');
  }
  for (const app of config.apps) {
    const manifest = JSON.parse(readFileSync(join(root, app.manifest)));
    const sourceContext = buildContext(root, { inputs: app.sourceInputs, dockerfile: app.manifest, platforms: [], reusable: false }, { paths });
    const images = plan.images.filter(i => i.recipe.appId === app.id);
    const inputFingerprint = identity({ manifest, profiles: app.profiles, sourceFingerprint: sourceContext.fingerprint,
      images: images.map(i => ({ id: i.recipe.id, inputFingerprint: i.inputFingerprint })) });
    const previous = store.json(head, `apps/${app.id}/candidate.json`);
    const changed = force || previous?.inputFingerprint !== inputFingerprint;
    const feedState = store.json(head, `apps/${app.id}/feed-state.json`);
    const recoveryOnly = !changed && config.feeds.some(f => f.policy === 'validated-source' && feedState?.feeds?.[f.id]?.releaseId !== previous.releaseId);
    const selected = changed || recoveryOnly;
    plan.apps.push({ app, manifest, inputFingerprint, sourceFingerprint: sourceContext.fingerprint, previous, selected, recoveryOnly,
      reason: force ? 'Explicit forced build/recovery.' : selected ? 'Effective manifest, source/setup or artifact inputs changed.' : 'Complete release inputs match the retained candidate.' });
    if (!selected || recoveryOnly) continue;
    for (const image of images) {
      image.decision = 'build'; image.reason = force ? 'Explicit forced rebuild.' : 'No verified matching artifact.';
      if (!force && reusableArtifact(image.previous, image.recipe, image.inputFingerprint)) {
        try {
          await verify(image.previous.repository, image.previous.digest, image.recipe.platforms);
          await prove(`oci://${image.previous.repository}@${image.previous.digest}`, image.previous, repository);
          image.decision = 'reuse'; image.reason = 'Exact input fingerprint, platform graph and original signed provenance verified.';
        } catch (error) { image.reason = `Retained artifact failed verification; rebuild: ${error.message}`; }
      } else if (!image.recipe.reusable) image.reason = image.recipe.inputFallback ?? image.recipe.reuseConstraint ?? 'An unpinned Docker input prevents verified reuse.';
    }
  }
  if (config.platform) {
  const nativeInputs = config.platform.inputs.flatMap(path => path === 'shared/*.cs' ? paths.filter(p => /^shared\/[^/]+\.cs$/.test(p)) : [path]);
  const nativeContext = buildContext(root, { inputs: nativeInputs, dockerfile: 'Directory.Build.props', rids: config.platform.rids }, { paths });
  const previousNative = store.json(head, 'platform/candidate.json');
  const nativeChanged = force || previousNative?.inputFingerprint !== nativeContext.fingerprint;
  const nativeFeedState = store.json(head, 'platform/feed-state.json');
  const nativeRecovery = !nativeChanged && config.feeds.some(f => f.policy === 'validated-source' && nativeFeedState?.feeds?.[f.id]?.releaseId !== previousNative.releaseId);
  plan.platform = { inputFingerprint: nativeContext.fingerprint, previous: previousNative,
    selected: nativeChanged || nativeRecovery, recoveryOnly: nativeRecovery,
    version: /<Version>([^<]+)<\/Version>/.exec(readFileSync(join(root, 'Directory.Build.props'), 'utf8'))?.[1] };
  }
  writeFileSync(join(output, 'release-plan.json'), canonical(plan) + '\n');
  execFileSync('tar', ['-cf', join(output, 'contexts.tar'), '-C', output, 'contexts']);
  return plan;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  if (head !== process.env.PUBLISH_SOURCE_SHA || execFileSync('git', ['status', '--porcelain', '--untracked-files=normal'], { encoding: 'utf8' }).trim())
    throw new Error('Publication must use the clean exact source qualified by CI.');
  const plan = await prepareRelease({ root: process.cwd(), output: resolve(process.env.RELEASE_OUTPUT ?? 'release-plan'),
    sourceCommit: process.env.PUBLISH_SOURCE_SHA, validationRunId: process.env.VALIDATED_CI_RUN_ID,
    repository: process.env.GITHUB_REPOSITORY, force: process.env.FORCE_REBUILD === 'true' });
  const builds = plan.images.filter(i => i.decision === 'build').map(i => ({ id: i.recipe.id, dockerfile: i.recipe.dockerfile,
    repository: i.recipe.repository, platforms: i.recipe.platforms.join(','), builder: i.recipe.toolchain.builder,
    buildArgs: Object.entries(i.recipe.buildArgs ?? {}).map(([k,v]) => `${k}=${v}`).join('\n') }));
  if (process.env.GITHUB_OUTPUT) {
    const { appendFileSync } = await import('node:fs');
    const nativeBuilds = builds.flatMap(image => image.platforms.split(',').map(platform => ({ ...image, platform, slug: platform.replaceAll('/', '-'), runner: platform === 'linux/arm64' ? 'ubuntu-24.04-arm' : 'ubuntu-24.04' })));
    appendFileSync(process.env.GITHUB_OUTPUT, `native-images=${JSON.stringify({ include: nativeBuilds })}\n`);
    appendFileSync(process.env.GITHUB_OUTPUT, `images=${JSON.stringify({ include: builds })}\nhas-images=${builds.length > 0}\nplatform=${plan.platform?.selected && !plan.platform.recoveryOnly}\nplatform-publish=${plan.platform?.selected}\n`);
  }
  console.log(JSON.stringify({ checks: `Qualified CI ${plan.validationRunId}`, images: plan.images.map(i => ({ id: i.recipe.id, decision: i.decision, reason: i.reason })), apps: plan.apps.map(a => ({ id: a.app.id, selected: a.selected, reason: a.reason })), platform: plan.platform }, null, 2));
}
