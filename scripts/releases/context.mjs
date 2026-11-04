import { dockerInputs } from './docker-inputs.mjs';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, lstatSync, readlinkSync, symlinkSync, chmodSync } from 'node:fs';
import { dirname, join, posix, resolve } from 'node:path';

export const canonical = value => JSON.stringify(sort(value));
function sort(value) {
  if (Array.isArray(value)) return value.map(sort);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k => [k, sort(value[k])]));
  return value;
}
export const sha256 = value => createHash('sha256').update(value).digest('hex');
export const identity = value => sha256(canonical(value));
export const fullCommit = value => /^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(value ?? '');
export const digest = value => /^sha256:[a-f0-9]{64}$/.test(value ?? '');
export function relativePath(value) {
  if (typeof value !== 'string' || !value || value.startsWith('/') || value.includes('\\')
    || value.split('/').some(x => !x || x === '.' || x === '..') || /[\x00-\x1f\x7f]/.test(value)) throw new Error(`Unsafe repository path: ${value}`);
  return value;
}

/** Keep the lock's actual installation paths, optional platforms and peer closure. Nothing is re-resolved. */
export function projectNpm(rootPackage, lock, initialWorkspaces) {
  if (lock.lockfileVersion !== 3 || !lock.packages?.['']) throw new Error('A complete npm lockfileVersion 3 is required.');
  const packages = lock.packages, included = new Set(['']), workspaces = new Set(initialWorkspaces), queue = [...initialWorkspaces];
  const resolveDependency = (from, name) => {
    if (!/^(?:@[^/]+\/)?[^/]+$/.test(name)) throw new Error(`Invalid dependency name ${name}`);
    let directory = from;
    while (true) {
      const candidate = posix.join(directory, 'node_modules', name);
      if (Object.hasOwn(packages, candidate)) return candidate;
      if (!directory) return null;
      directory = posix.dirname(directory); if (directory === '.') directory = '';
    }
  };
  // Root dependencies/overrides/engines can influence every workspace. Do not discard them.
  queue.push('');
  const visited = new Set();
  while (queue.length) {
    const path = queue.shift(); if (visited.has(path)) continue; visited.add(path); included.add(path);
    const entry = packages[path]; if (!entry) throw new Error(`Missing locked workspace/dependency: ${path}`);
    if (entry.link) {
      relativePath(entry.resolved); workspaces.add(entry.resolved); queue.push(entry.resolved); continue;
    }
    if (entry.inBundle) {
      const parent = path.slice(0, path.lastIndexOf('/node_modules/'));
      if (!packages[parent]?.integrity && !packages[parent]?.inBundle) throw new Error(`Unpinned bundled package: ${path}`);
      queue.push(parent);
    }
    if (path.includes('node_modules/') && !entry.inBundle && (!entry.integrity || !/^https:\/\//.test(entry.resolved ?? ''))) {
      throw new Error(`Dependency has no integrity-pinned HTTPS archive: ${path}`);
    }
    const dependencies = { ...entry.dependencies, ...entry.optionalDependencies, ...entry.peerDependencies,
      ...(!path.includes('node_modules/') ? entry.devDependencies : {}) };
    for (const name of Object.keys(dependencies)) {
      const target = resolveDependency(path, name);
      if (target) queue.push(target);
      else if (!Object.hasOwn(entry.optionalDependencies ?? {}, name) && !entry.peerDependenciesMeta?.[name]?.optional) {
        throw new Error(`Cannot prove locked dependency closure for ${path || '<root>'}: ${name}`);
      }
    }
  }
  // npm links each selected workspace, even when it is only an explicit --workspace target.
  for (const [path, entry] of Object.entries(packages)) if (entry.link && workspaces.has(entry.resolved)) included.add(path);
  const projectedPackage = { ...rootPackage, workspaces: [...workspaces].sort() };
  delete projectedPackage.scripts; // All release Dockerfiles invoke workspace scripts directly.
  const projectedLock = { ...lock, packages: Object.fromEntries([...included].sort().map(path => [path, packages[path]])) };
  projectedLock.packages[''] = { ...packages[''], workspaces: projectedPackage.workspaces };
  return { package: projectedPackage, lock: projectedLock, workspaces: [...workspaces].sort() };
}

export function trackedPaths(root) {
  return [...new Set(execFileSync('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'], { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).split('\0').filter(Boolean))].sort();
}

/** The exact returned bytes are materialized as the Docker context, including the projected lock. */
export function buildContext(root, recipe, { paths = trackedPaths(root) } = {}) {
  const docker = /(?:^|\/)Dockerfile(?:\.[^/]+)?$/.test(recipe.dockerfile)
    ? dockerInputs(readFileSync(join(root, recipe.dockerfile), 'utf8'), recipe, paths) : { inputs: [], reusable: recipe.reusable, fallback: null };
  recipe = { ...recipe, ...(docker.fallback ? { inputFallback: docker.fallback } : {}), ...(recipe.reusable !== undefined ? { reusable: docker.reusable } : {}) };
  const inputs = [...recipe.inputs, ...docker.inputs, recipe.dockerfile, '.dockerignore'];
  const generated = new Map();
  if (recipe.workspaces?.length && !docker.fallback) {
    const projection = projectNpm(JSON.parse(readFileSync(join(root, 'package.json'))), JSON.parse(readFileSync(join(root, 'package-lock.json'))), recipe.workspaces);
    inputs.push(...projection.workspaces);
    generated.set('package.json', Buffer.from(canonical(projection.package) + '\n'));
    generated.set('package-lock.json', Buffer.from(canonical(projection.lock) + '\n'));
  }
  inputs.forEach(relativePath);
  const selected = docker.fallback ? paths : paths.filter(path => inputs.some(input => path === input || path.startsWith(input + '/')));
  const files = [];
  for (const path of selected) {
    relativePath(path); if (generated.has(path)) continue;
    let stat; try { stat = lstatSync(join(root, path)); } catch (error) { if (error.code === 'ENOENT') continue; throw error; }
    if (stat.isSymbolicLink()) {
      const target = readlinkSync(join(root, path));
      const resolved = posix.normalize(posix.join(posix.dirname(path), target));
      if (target.startsWith('/') || resolved.startsWith('../') || !selected.includes(resolved)) throw new Error(`Unproven build-context symlink: ${path}`);
      files.push({ path, mode: '120000', bytes: Buffer.from(target) });
    } else if (stat.isFile()) files.push({ path, mode: stat.mode & 0o111 ? '100755' : '100644', bytes: path === recipe.dockerfile && recipe.toolchain?.frontend ? Buffer.from(`# syntax=${recipe.toolchain.frontend}\n` + readFileSync(join(root, path), 'utf8').replace(/^# syntax=.*\n/m, '')) : readFileSync(join(root, path)) });
    else throw new Error(`Unsupported build input: ${path}`);
  }
  for (const [path, bytes] of generated) files.push({ path, bytes, mode: '100644' });
  files.sort((a, b) => a.path.localeCompare(b.path, 'en'));
  // Recipe/toolchain versions are explicit inputs; secrets and runner IDs never enter this record.
  const record = { schemaVersion: 'hosty-build-inputs.0.1', recipe,
    files: files.map(f => ({ path: f.path, mode: f.mode, sha256: sha256(f.bytes) })) };
  return { files, record, recipe, fingerprint: identity(record) };
}

export function materializeContext(context, destination) {
  mkdirSync(destination, { recursive: true });
  for (const file of context.files) {
    relativePath(file.path); const target = join(destination, file.path); mkdirSync(dirname(target), { recursive: true });
    if (file.mode === '120000') symlinkSync(file.bytes.toString(), target);
    else { writeFileSync(target, file.bytes); chmodSync(target, file.mode === '100755' ? 0o755 : 0o644); }
  }
}

export function reusableArtifact(receipt, recipe, fingerprint) {
  return recipe.reusable === true && receipt?.schemaVersion === 'hosty-image-artifact.0.1'
    && receipt.inputFingerprint === fingerprint && receipt.repository === recipe.repository && digest(receipt.digest)
    && fullCommit(receipt.buildSourceCommit) && typeof receipt.provenance === 'string' && receipt.provenance.startsWith('https://')
    && canonical(receipt.platforms?.slice().sort()) === canonical(recipe.platforms.slice().sort());
}
