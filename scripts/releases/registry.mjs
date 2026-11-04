import { digest, sha256 } from './context.mjs';

const accept = 'application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json';
export function imageReference(value) {
  if (typeof value === 'object' && value) return { repository: value.repository, reference: value.digest ?? value.tag };
  if (typeof value !== 'string') throw new Error('Image reference is required.');
  const at = value.lastIndexOf('@'); if (at >= 0) return { repository: value.slice(0, at), reference: value.slice(at + 1) };
  const colon = value.lastIndexOf(':');
  return colon > value.lastIndexOf('/') ? { repository: value.slice(0, colon), reference: value.slice(colon + 1) } : { repository: value, reference: 'latest' };
}

/** Resolve and validate the complete platform graph through anonymous consumer access. */
export async function verifyImage(repository, reference, platforms, { request = fetch } = {}) {
  if (!/^[a-z0-9][a-z0-9._:/-]*$/.test(repository ?? '') || repository.includes('..') || repository.includes('//')
    || !(digest(reference) || /^[\w][\w.-]{0,127}$/.test(reference ?? ''))) throw new Error('Invalid image repository/reference.');
  const parts = repository.split('/');
  const explicitRegistry = parts.length > 1 && /[.:]/.test(parts[0]);
  const registry = explicitRegistry ? parts.shift() : 'registry-1.docker.io';
  const name = parts.length === 1 && !explicitRegistry ? 'library/' + parts[0] : parts.join('/');
  const base = `https://${registry}/v2/${name}/`;
  let token;
  async function send(url, method = 'GET', headers = {}, redirects = 0) {
    if (!url.startsWith('https://') || redirects > 5) throw new Error('Registry redirect must remain HTTPS and bounded.');
    const response = await request(url, { method, headers, redirect: 'manual', signal: AbortSignal.timeout(60_000) });
    if ([301,302,303,307,308].includes(response.status)) {
      const location = response.headers.get('location'); await response.body?.cancel();
      if (!location) throw new Error('Missing registry redirect location.');
      const next = new URL(location, url).href;
      if (new URL(next).origin !== new URL(url).origin) { headers = { ...headers }; delete headers.Authorization; }
      return send(next, method, headers, redirects + 1);
    }
    return response;
  }
  async function checked(path, method = 'GET') {
    const headers = { Accept: accept, ...(token ? { Authorization: `Bearer ${token}` } : {}) };
    let response = await send(base + path, method, headers);
    if (response.status === 401) {
      const challenge = response.headers.get('www-authenticate'); await response.body?.cancel();
      if (!challenge?.startsWith('Bearer ')) throw new Error('Registry requires credentials unavailable to release consumers.');
      const values = Object.fromEntries([...challenge.matchAll(/(realm|service|scope)="([^"]+)"/g)].map(m => [m[1], m[2]]));
      if (!values.realm?.startsWith('https://')) throw new Error('Registry token endpoint must use HTTPS.');
      const url = new URL(values.realm); if (values.service) url.searchParams.set('service', values.service);
      url.searchParams.set('scope', `repository:${name}:pull`);
      const auth = await send(url.href); if (!auth.ok) throw new Error(`Registry authentication failed: ${auth.status}`);
      const body = JSON.parse((await bytes(auth, 1024 * 1024)).toString()); token = body.token ?? body.access_token;
      if (!token) throw new Error('Registry returned no consumer token.');
      response = await send(base + path, method, { ...headers, Authorization: `Bearer ${token}` });
    }
    if (!response.ok) { await response.body?.cancel(); throw new Error(`Registry ${repository} ${path}: HTTP ${response.status}`); }
    return response;
  }
  async function document(path, expected, expectedSize) {
    const response = await checked(path), data = await bytes(response, 4 * 1024 * 1024), actual = 'sha256:' + sha256(data);
    if ((expected && expected !== actual) || (expectedSize !== undefined && expectedSize !== data.length)
      || (response.headers.get('docker-content-digest') && response.headers.get('docker-content-digest') !== actual)) throw new Error('Registry descriptor digest/size mismatch.');
    return { value: JSON.parse(data.toString()), digest: actual };
  }
  const root = await document('manifests/' + reference, digest(reference) ? reference : null);
  if (root.value.schemaVersion !== 2) throw new Error('Unsupported image manifest schema.');
  const results = [];
  for (const platform of platforms) {
    const [os, architecture, variant] = platform.split('/'); let manifest = root.value;
    if (manifest.manifests) {
      if (!Array.isArray(manifest.manifests) || manifest.manifests.length > 256) throw new Error('Invalid image index.');
      const matches = manifest.manifests.filter(m => m.platform?.os === os && m.platform?.architecture === architecture && (!variant || m.platform?.variant === variant));
      if (matches.length !== 1) throw new Error(`Image has no unique ${platform} manifest.`);
      descriptor(matches[0]); manifest = (await document('manifests/' + matches[0].digest, matches[0].digest, matches[0].size)).value;
    }
    if (manifest.schemaVersion !== 2 || !Array.isArray(manifest.layers) || manifest.layers.length > 256) throw new Error('Malformed image manifest.');
    descriptor(manifest.config);
    const config = (await document('blobs/' + manifest.config.digest, manifest.config.digest, manifest.config.size)).value;
    if (config.os !== os || config.architecture !== architecture || (variant && config.variant && config.variant !== variant)) throw new Error(`Image config does not support ${platform}.`);
    for (const layer of manifest.layers) {
      descriptor(layer); const response = await checked('blobs/' + layer.digest, 'HEAD');
      if (Number(response.headers.get('content-length')) !== layer.size) throw new Error('Image layer is missing or has the wrong size.');
      await response.body?.cancel();
    }
    results.push(platform);
  }
  return { repository, digest: root.digest, platforms: results.sort() };
}
function descriptor(value) {
  if (!digest(value?.digest) || !Number.isSafeInteger(value.size) || value.size <= 0) throw new Error('Invalid OCI descriptor.');
}
export async function bytes(response, limit) {
  if (Number(response.headers.get('content-length')) > limit) throw new Error('Response exceeds size limit.');
  const chunks = []; let length = 0;
  for await (const chunk of response.body) { length += chunk.length; if (length > limit) throw new Error('Response exceeds size limit.'); chunks.push(chunk); }
  return Buffer.concat(chunks);
}
