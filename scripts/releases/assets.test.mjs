import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { releaseFiles } from './assets.mjs';

test('immutable catalogs retain inline, reference and HTML image assets and full skill packages', () => {
  const root = mkdtempSync(join(tmpdir(), 'hosty-release-assets-'));
  try {
    execFileSync('git', ['init', '-q', root]);
    const write = (path, data) => { mkdirSync(join(root, path, '..'), { recursive: true }); writeFileSync(join(root, path), data); };
    write('app/docs/store.md', `![inline](../assets/one.png?rev=2)\n![Ref][]\n[ref]: ../assets/two.png\n<img src="../assets/three%20words.png">\n![external](https://publisher.test/image.png)`);
    for (const path of ['one.png','two.png','three words.png']) write(`app/assets/${path}`, path);
    write('app/skills/example/SKILL.md', 'Skill'); write('app/skills/example/references/details.md', 'Details');
    execFileSync('git', ['-C', root, 'add', '.']);
    const manifest = { catalogMetadata: { descriptionFile: 'docs/store.md' }, agent: { skills: [{ path: 'skills/example' }] } };
    const files = releaseFiles(root, { manifest: 'app/manifest.json' }, manifest);
    assert.deepEqual(files.map(file => file.path), ['assets/one.png', 'assets/three words.png', 'assets/two.png', 'docs/store.md', 'skills/example/SKILL.md', 'skills/example/references/details.md']);
    write('app/docs/store.md', '![missing](../assets/missing.png)');
    assert.throws(() => releaseFiles(root, { manifest: 'app/manifest.json' }, manifest), /missing or untracked/);
    write('app/docs/store.md', '<img src="../../../outside.png">');
    assert.throws(() => releaseFiles(root, { manifest: 'app/manifest.json' }, manifest), /path/i);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
