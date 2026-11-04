import { readFileSync, writeFileSync, mkdirSync, statSync, readdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { canonical, identity, sha256, digest } from './context.mjs';
import { artifactPredicate } from './evidence.mjs';
import { nativeToolchain } from './native-toolchain.mjs';

export function imageReceipt(plan, imageId, imageDigest) {
  const image = plan.images.find(i => i.recipe.id === imageId && i.decision === 'build');
  if (!image || !digest(imageDigest)) throw new Error('Unexpected image build result.');
  const receipt = { schemaVersion: 'hosty-image-artifact.0.1', artifactId: imageId, repository: image.recipe.repository, digest: imageDigest,
    inputFingerprint: image.inputFingerprint, fingerprintFormat: 'hosty-build-inputs.0.1', platforms: image.recipe.platforms,
    buildSourceCommit: plan.sourceCommit, provenance: `https://github.com/${plan.repository}/attestations` };
  const predicate = artifactPredicate({ ...receipt, sourceCommit: plan.sourceCommit, repository: plan.repository,
    validationRunId: plan.validationRunId, artifactId: imageId });
  return { receipt, predicate };
}

export function nativeReceipts(plan, rid, directory, { run = execFileSync } = {}) {
  if (!plan.platform.selected || !plan.config.platform.rids.includes(rid)) throw new Error('Unexpected native build result.');
  const suffix = { 'osx-arm64': 'darwin-arm64', 'linux-arm64': 'linux-arm64', 'linux-x64': 'linux-x64', 'win-x64': 'windows-x64.exe' }[rid];
  if (!suffix) throw new Error('This builder has no recipe for the requested RID.');
  const toolchain = nativeToolchain({ run });
  return ['cli','core'].map(role => {
    const fileName = `hosty${role === 'core' ? '-core' : ''}-${suffix}`, path = join(directory, fileName);
    const project = `apps/${role}/src/Haas.Hosty.${role === 'core' ? 'Core' : 'Cli'}/obj/project.assets.json`;
    const resolved = JSON.parse(readFileSync(project));
    const dependencies = Object.fromEntries(Object.entries(resolved.libraries).map(([name, library]) => [name, { type: library.type, sha512: library.sha512 }]));
    const inputFingerprint = identity({ format: 'hosty-native-inputs.0.1', qualifiedInputs: plan.platform.inputFingerprint,
      sourceCommit: plan.sourceCommit, rid, role, toolchain, dependencies,
      command: 'dotnet publish --configuration Release --self-contained true --runtime <rid>' });
    const receipt = { artifactId: `${role}/${rid}`, role, rid, fileName, size: statSync(path).size, sha256: sha256(readFileSync(path)), inputFingerprint,
      toolchain, qualifiedInputs: plan.platform.inputFingerprint, buildSourceCommit: plan.sourceCommit, provenance: `https://github.com/${plan.repository}/attestations` };
    return { receipt, predicate: artifactPredicate({ ...receipt, sourceCommit: plan.sourceCommit, repository: plan.repository,
      validationRunId: plan.validationRunId, artifactId: `${role}/${rid}`, platforms: [rid] }) };
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const plan = JSON.parse(readFileSync(process.env.RELEASE_PLAN ?? 'release-plan/release-plan.json'));
  const output = resolve(process.env.ARTIFACT_OUTPUT ?? 'release-artifacts'); mkdirSync(output, { recursive: true });
  if (process.argv[2] === 'image') {
    const result = imageReceipt(plan, process.env.IMAGE_ID, process.env.IMAGE_DIGEST);
    writeFileSync(join(output, `${process.env.IMAGE_ID}.json`), canonical(result.receipt) + '\n');
    writeFileSync(join(output, 'predicate.json'), canonical(result.predicate) + '\n');
  } else if (process.argv[2] === 'native') {
    for (const result of nativeReceipts(plan, process.env.RID, output)) {
      writeFileSync(join(output, `${result.receipt.fileName}.json`), canonical(result.receipt) + '\n');
      writeFileSync(join(output, `${result.receipt.role}-predicate.json`), canonical(result.predicate) + '\n');
    }
  } else throw new Error('Expected image or native artifact recording.');
}
