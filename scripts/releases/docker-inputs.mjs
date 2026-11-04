/** Discover literal Docker inputs; unsupported syntax retains the complete context and disables reuse. */
export function dockerInputs(dockerfile, recipe, paths) {
  const inputs = new Set(), stages = new Set(); let fallback = null, reusable = recipe.reusable;
  for (const original of dockerfile.replace(/\\\r?\n/g, ' ').split(/\r?\n/)) {
    const line = original.trim();
    if (!line || line.startsWith('#')) continue;
    const split = /^(\w+)\s+(.+)$/.exec(line); if (!split) { fallback = 'Unsupported Dockerfile instruction syntax.'; continue; }
    const command = split[1].toUpperCase(); let body = split[2];
    if (command === 'FROM') {
      const parts = body.split(/\s+/).filter(p => !p.startsWith('--platform='));
      const from = parts[0];
      const variable = /^\$\{([A-Z_][A-Z_0-9]*)\}$/.exec(from)?.[1];
      if (from !== 'scratch' && !stages.has(from) && !/@sha256:[a-f0-9]{64}$/.test(variable ? recipe.buildArgs?.[variable] ?? '' : from)) reusable = false;
      if (parts[1]?.toUpperCase() === 'AS') stages.add(parts[2]);
      continue;
    }
    if (command === 'RUN' && [...body.matchAll(/--mount=([^\s]+)/g)].some(m => !/^type=(?:cache|secret|ssh)(?:,|$)/.test(m[1]))) { fallback = 'RUN mounts require full-context qualification.'; reusable = false; }
    if (command === 'ADD') { fallback = 'ADD inputs require full-context qualification.'; continue; }
    if (command !== 'COPY') continue;
    const flags = [];
    while (body.startsWith('--')) {
      const option = /^(--[^\s]+)\s+/.exec(body);
      if (!option) { fallback = 'Unrecognized COPY flags.'; break; }
      flags.push(option[1]); body = body.slice(option[0].length);
    }
    const from = flags.find(f => f.startsWith('--from='))?.slice(7);
    if (from) { if (!stages.has(from) && !/^\d+$/.test(from)) reusable = false; continue; }
    if (flags.some(f => !/^--(?:chown|chmod)=/.test(f))) fallback = 'Unrecognized COPY option semantics.';
    let values;
    try { values = body.startsWith('[') ? JSON.parse(body) : body.split(/\s+/); }
    catch { fallback = 'Unrecognized COPY input syntax.'; continue; }
    if (!Array.isArray(values) || values.length < 2 || values.some(v => typeof v !== 'string')) { fallback = 'Unrecognized COPY input list.'; continue; }
    for (const value of values.slice(0, -1)) {
      const input = value.replace(/^\.\//, '').replace(/\/$/, '');
      if (!input || input === '.' || /[$*?\[\]'"\\]/.test(input) || input.startsWith('/') || input.split('/').includes('..')) {
        fallback = 'Dynamic or broad COPY inputs require the full repository context.'; continue;
      }
      if (!paths.some(p => p === input || p.startsWith(input + '/'))) throw new Error(`Missing declared Docker COPY input: ${input}`);
      inputs.add(input);
    }
  }
  return { inputs: [...inputs], reusable: fallback ? false : reusable, fallback };
}
