import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, dirname, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { canonical, relativePath } from './context.mjs';

/** Validate the complete mixed-artifact manifest through Core before any candidate/pointer write. */
export function validateCombination({ root, app, release, files }, { run = execFileSync } = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'hosty-release-validation-'));
  try {
    const path = join(directory, 'manifest.json');
    writeFileSync(path, canonical(release.manifest) + '\n');
    for (const file of files) {
      relativePath(file.path); const destination = join(directory, file.path);
      mkdirSync(dirname(destination), { recursive: true }); writeFileSync(destination, file.data);
    }
    const validator = process.env.HOSTY_RELEASE_VALIDATOR ?? resolve(root, 'apps/core/tests/Haas.Hosty.ReleaseValidation/bin/Release/net10.0/Haas.Hosty.ReleaseValidation.dll');
    run('dotnet', [validator, path, ...app.profiles], { cwd: root, stdio: 'inherit', timeout: 120_000 });
  } finally { rmSync(directory, { recursive: true, force: true }); }
}
