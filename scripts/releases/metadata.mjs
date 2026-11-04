import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { canonical, fullCommit, relativePath } from './context.mjs';

/** A normal Git fast-forward is the only write primitive; every retry starts from the new head. */
export class MetadataStore {
  constructor(root, branch, { run } = {}) {
    this.root = root; this.branch = branch;
    this.run = run ?? ((args, { trim = true, ...options } = {}) => { const result = execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024, ...options }); return trim ? result.trim() : result; });
  }
  refresh() {
    const line = this.run(['ls-remote', '--heads', 'origin', `refs/heads/${this.branch}`]);
    if (!line) return null;
    const sha = line.split(/\s/)[0]; if (!fullCommit(sha)) throw new Error('Invalid metadata remote head.');
    this.run(['fetch', '--no-tags', 'origin', sha]); return sha;
  }
  read(head, path) {
    relativePath(path); if (!head) return null;
    // Missing files are expected on first publication; repository and object errors are not.
    const found = this.run(['ls-tree', head, '--', path]); if (!found) return null;
    return this.run(['show', `${head}:${path}`], { trim: false });
  }
  json(head, path) { const value = this.read(head, path); return value ? JSON.parse(value) : null; }
  ancestor(before, after) {
    if (!fullCommit(before) || !fullCommit(after)) throw new Error('Source ancestry requires full commits.');
    try { this.run(['merge-base', '--is-ancestor', before, after]); return true; }
    catch (error) { if (error.status === 1) return false; throw error; }
  }
  async update(message, changesForHead, { attempts = 8 } = {}) {
    for (let attempt = 0; attempt < attempts; attempt++) {
      const head = this.refresh(), changes = await changesForHead(head);
      if (!changes || !Object.keys(changes).length) return head;
      const directory = mkdtempSync(join(tmpdir(), 'hosty-metadata-'));
      const env = { ...process.env, GIT_INDEX_FILE: join(directory, 'index'),
        GIT_AUTHOR_NAME: 'github-actions[bot]', GIT_AUTHOR_EMAIL: '41898282+github-actions[bot]@users.noreply.github.com',
        GIT_COMMITTER_NAME: 'github-actions[bot]', GIT_COMMITTER_EMAIL: '41898282+github-actions[bot]@users.noreply.github.com' };
      try {
        this.run(['read-tree', ...(head ? [head] : ['--empty'])], { env });
        let changed = false;
        for (const [path, change] of Object.entries(changes)) {
          relativePath(path); const value = Buffer.isBuffer(change.value) || typeof change.value === 'string' ? change.value : canonical(change.value) + '\n';
          const mode = change.mode ?? '100644';
          if (!['100644','100755'].includes(mode)) throw new Error('Unsupported release file mode.');
          const blob = this.run(['hash-object', '-w', '--stdin'], { input: value });
          const previous = head ? this.run(['ls-tree', head, '--', path]) : '';
          if (previous.startsWith(`${mode} blob ${blob}\t`)) continue;
          if (change.immutable && previous) throw new Error(`Immutable release collision: ${path}`);
          this.run(['update-index', '--add', '--cacheinfo', `${mode},${blob},${path}`], { env }); changed = true;
        }
        if (!changed) return head;
        const tree = this.run(['write-tree'], { env });
        const commit = this.run(['commit-tree', tree, ...(head ? ['-p', head] : [])], { env,
          input: `chore(releases): ${message}\n\nGenerated immutable release metadata and guarded feed state.\n\nCo-Authored-By: Codex <noreply@openai.com>\n` });
        try { this.run(['push', 'origin', `${commit}:refs/heads/${this.branch}`]); return commit; }
        catch (error) {
          const current = this.refresh();
          if (current === commit || (current && this.ancestor(commit, current))) return commit;
          if (current === head) throw error; // Permissions/network failure, not a concurrent update.
        }
      } finally { rmSync(directory, { recursive: true, force: true }); }
    }
    throw new Error('Concurrent metadata publications exceeded the bounded retry limit; retry the same candidate.');
  }
}
