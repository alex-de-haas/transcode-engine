import { readFileSync, writeFileSync, mkdirSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';
import { canonical, digest } from './context.mjs';
import { imageReceipt } from './artifacts.mjs';
import { verifyImage } from './registry.mjs';

export function platformReceipt(plan, id, platform, imageDigest) {
  const image = plan.images.find(i => i.recipe.id === id && i.decision === 'build');
  if (!image || !image.recipe.platforms.includes(platform) || !digest(imageDigest)) throw new Error('Unexpected platform result.');
  return { id, platform, digest: imageDigest, repository: image.recipe.repository,
    sourceCommit: plan.sourceCommit, inputFingerprint: image.inputFingerprint };
}

export async function mergeImage(plan, id, directory, { verify = verifyImage, run = execFileSync } = {}) {
  const image = plan.images.find(i => i.recipe.id === id && i.decision === 'build');
  if (!image) throw new Error('Image is not selected for a build.');
  const references = [];
  for (const platform of image.recipe.platforms) {
    const receipt = JSON.parse(readFileSync(join(directory, `${id}-${platform.replaceAll('/', '-')}.platform.json`)));
    const expected = platformReceipt(plan, id, platform, receipt.digest);
    if (canonical(receipt) !== canonical(expected)) throw new Error('Platform result differs from the exact build plan.');
    await verify(receipt.repository, receipt.digest, [platform]);
    references.push(`${receipt.repository}@${receipt.digest}`);
  }
  const tag = `candidate-${process.env.GITHUB_RUN_ID}-${process.env.GITHUB_RUN_ATTEMPT}`;
  if (!/^candidate-\d+-\d+$/.test(tag)) throw new Error('A qualified publication run identity is required.');
  run('docker', ['buildx','imagetools','create','--tag',`${image.recipe.repository}:${tag}`, ...references], { stdio: 'inherit' });
  const proof = await verify(image.recipe.repository, tag, image.recipe.platforms);
  return imageReceipt(plan, id, proof.digest);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const plan = JSON.parse(readFileSync('release-plan/release-plan.json')), directory = 'release-artifacts';
  mkdirSync(directory, { recursive: true });
  const id = process.env.IMAGE_ID;
  if (process.argv[2] === 'record') {
    const receipt = platformReceipt(plan, id, process.env.IMAGE_PLATFORM, process.env.IMAGE_DIGEST);
    writeFileSync(join(directory, `${id}-${receipt.platform.replaceAll('/', '-')}.platform.json`), canonical(receipt) + '\n');
  } else if (process.argv[2] === 'merge') {
    const result = await mergeImage(plan, id, directory);
    writeFileSync(join(directory, `${id}.json`), canonical(result.receipt) + '\n');
    writeFileSync(join(directory, 'predicate.json'), canonical(result.predicate) + '\n');
    appendFileSync(process.env.GITHUB_OUTPUT, `digest=${result.receipt.digest}\n`);
  } else throw new Error('Expected record or merge.');
}
