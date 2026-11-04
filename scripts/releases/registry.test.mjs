import test from 'node:test';
import assert from 'node:assert/strict';
import { sha256 } from './context.mjs';
import { verifyImage } from './registry.mjs';
function graph() {
  const documents = new Map(), layer = Buffer.from('compressed image layer');
  const put = value => { const bytes = Buffer.from(JSON.stringify(value)), digest = 'sha256:' + sha256(bytes); documents.set(digest, bytes); return { digest, size: bytes.length }; };
  documents.set('sha256:' + sha256(layer), layer);
  const platforms = ['amd64','arm64'].map(architecture => {
    const config = put({ os: 'linux', architecture });
    const manifest = put({ schemaVersion: 2, mediaType: 'application/vnd.oci.image.manifest.v1+json', config,
      layers: [{ digest: 'sha256:' + sha256(layer), size: layer.length }] });
    return { ...manifest, platform: { os: 'linux', architecture } };
  });
  const index = put({ schemaVersion: 2, mediaType: 'application/vnd.oci.image.index.v1+json', manifests: platforms });
  const request = async (url, options) => {
    const reference = url.slice(url.lastIndexOf('/') + 1), data = documents.get(reference === 'candidate' ? index.digest : reference);
    return data ? new Response(options.method === 'HEAD' ? null : data, { headers: { 'content-length': String(data.length) } }) : new Response('', { status: 404 });
  };
  return { documents, index, layer, request };
}
test('consumer proof verifies both index children, their configs and every layer', async () => {
  const f = graph(); const proof = await verifyImage('example.test/app', 'candidate', ['linux/amd64','linux/arm64'], f);
  assert.equal(proof.digest, f.index.digest); assert.equal(proof.platforms.length, 2);
  await assert.rejects(verifyImage('example.test/app', 'candidate', ['linux/s390x'], f), /no unique/);
  f.documents.delete('sha256:' + sha256(f.layer));
  await assert.rejects(verifyImage('example.test/app', f.index.digest, ['linux/amd64'], f), /404/);
});
test('a corrupt manifest and denied/rate-limited access never prove readiness', async () => {
  const f = graph(); f.documents.set(f.index.digest, Buffer.from('{}'));
  await assert.rejects(verifyImage('example.test/app', f.index.digest, ['linux/amd64'], f), /mismatch/);
  for (const status of [403,429]) await assert.rejects(verifyImage('example.test/app', 'candidate', ['linux/amd64'], { request: async () => new Response('', { status }) }), new RegExp(String(status)));
});
test('registry redirects never forward bearer authorization to another origin', async () => {
  const f = graph(); let stripped = false;
  const request = async (url, options) => {
    if (url === 'https://auth.test/token?scope=repository%3Aapp%3Apull') return new Response(JSON.stringify({ token: 'test-token' }));
    if (url.startsWith('https://storage.test/')) { assert.equal(options.headers.Authorization, undefined); stripped = true; return f.request(url, options); }
    if (!options.headers.Authorization) return new Response('', { status: 401, headers: { 'www-authenticate': 'Bearer realm="https://auth.test/token"' } });
    return new Response(null, { status: 307, headers: { location: url.replace('example.test', 'storage.test') } });
  };
  await verifyImage('example.test/app', 'candidate', ['linux/amd64'], { request }); assert.equal(stripped, true);
});
