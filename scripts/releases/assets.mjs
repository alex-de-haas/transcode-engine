import { readFileSync, lstatSync } from 'node:fs';
import { join, dirname, posix } from 'node:path';
import { trackedPaths, relativePath, sha256 } from './context.mjs';

/** Assets/skills are part of the immutable release, not references back to moving source. */
export function releaseFiles(root, app, manifest) {
  const directory = dirname(app.manifest).replaceAll('\\', '/'), all = trackedPaths(root), selected = new Set();
  function add(path) {
    if (!path || /^https?:\/\//.test(path)) return;
    relativePath(path); const full = posix.join(directory, path);
    if (!all.includes(full)) throw new Error(`Declared release asset is missing or untracked: ${full}`);
    const stat = lstatSync(join(root, full)); if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('Release assets must be regular files.');
    selected.add(path);
  }
  for (const item of manifest.ui?.navigation ?? []) add(item.iconAsset);
  const metadata = manifest.catalogMetadata;
  add(metadata?.icon); for (const path of metadata?.screenshots ?? []) add(path);
  if (metadata?.descriptionFile) {
    add(metadata.descriptionFile);
    const markdown = readFileSync(join(root, directory, metadata.descriptionFile), 'utf8');
    const references = [];
    for (const match of markdown.matchAll(/!\[[^\]]*\]\(\s*(?:<([^>]+)>|([^\s)]+))/g)) references.push(match[1] ?? match[2]);
    // Reference-style Markdown and HTML images are rendered by catalog clients too.
    const definitions = new Map([...markdown.matchAll(/^ {0,3}\[([^\]]+)\]:\s*(?:<([^>]+)>|([^\s]+))/gm)]
      .map(match => [match[1].trim().replace(/\s+/g, ' ').toLowerCase(), match[2] ?? match[3]]));
    for (const match of markdown.matchAll(/!\[([^\]]*)\](?:\[([^\]]*)\])?(?![\[(])/g)) {
      const reference = definitions.get((match[2] || match[1]).trim().replace(/\s+/g, ' ').toLowerCase());
      if (reference) references.push(reference);
    }
    for (const match of markdown.matchAll(/<img\b[^>]*?\bsrc\s*=\s*(?:"([^"]+)"|'([^']+)'|([^\s>]+))/gi)) references.push(match[1] ?? match[2] ?? match[3]);
    for (const reference of references) {
      if (/^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(reference)) continue;
      const path = decodeURIComponent(reference.split(/[?#]/, 1)[0]);
      add(posix.normalize(posix.join(posix.dirname(metadata.descriptionFile), path)));
    }
  }
  for (const skill of manifest.agent?.skills ?? []) {
    relativePath(skill.path); const files = all.filter(p => p.startsWith(posix.join(directory, skill.path) + "/"));
    if (!files.length || files.length > 512 || !files.includes(posix.join(directory, skill.path, "SKILL.md"))) throw new Error('Invalid or incomplete release skill package.');
    for (const file of files) add(directory === '.' ? file : file.slice(directory.length + 1));
  }
  let total = 0;
  return [...selected].sort().map(path => {
    const full = join(root, directory, path), data = readFileSync(full); total += data.length;
    if (data.length > 16 * 1024 * 1024 || total > 32 * 1024 * 1024) throw new Error('Release assets exceed the bounded package size.');
    return { path, mode: lstatSync(full).mode & 0o111 ? '100755' : '100644', sha256: sha256(data), size: data.length, data };
  });
}
