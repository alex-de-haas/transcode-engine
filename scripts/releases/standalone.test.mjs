import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { prepareRelease } from './prepare.mjs';
import { releaseFiles } from './assets.mjs';
import { platformReceipt, mergeImage } from './merge-images.mjs';
import { validatePublisher, appRelease } from './model.mjs';
const commit = 'a'.repeat(40), hash = 'b'.repeat(64);
const write = (root, path, value) => { mkdirSync(dirname(join(root,path)), { recursive:true }); writeFileSync(join(root,path), value); };

test('root manifests retain catalog assets and complete skill packages', () => {
  const root = mkdtempSync(join(tmpdir(),'hosty-root-assets-'));
  try {
    execFileSync('git',['init','-q',root]);
    write(root,'assets/icon.svg','icon'); write(root,'skills/app/SKILL.md','skill');
    const files = releaseFiles(root,{manifest:'manifest.json'},{catalogMetadata:{icon:'assets/icon.svg'},agent:{skills:[{path:'skills/app'}]}});
    assert.deepEqual(files.map(f=>f.path),['assets/icon.svg','skills/app/SKILL.md']);
  } finally { rmSync(root,{recursive:true,force:true}); }
});

test('standalone nested contexts omit platform builds and isolate sibling changes', async () => {
  const root=mkdtempSync(join(tmpdir(),'hosty-standalone-')), values=new Map();
  const store={refresh:()=>commit,json:(_head,path)=>values.get(path)??null};
  try {
    execFileSync('git',['init','-q',root]);
    const config={schemaVersion:'hosty-publisher.0.1',metadataBranch:'app-releases',feeds:[{id:'daily',policy:'validated-source',default:true}],
      apps:[{id:'example.app',manifest:'manifest.json',profiles:['docker'],sourceInputs:['assets']}],
      images:['api','web'].map(id=>({id,appId:'example.app',services:[id],repository:'example/'+id,context:'src/'+id,dockerfile:'Dockerfile',inputs:[],platforms:['linux/amd64','linux/arm64'],reusable:true,toolchain:{}}))};
    write(root,'distribution/release-publisher.json',JSON.stringify(config));
    write(root,'manifest.json',JSON.stringify({id:'example.app',version:'1.0.0'}));
    write(root,'.github/workflows/ready-releases.yml','workflow');
    for(const name of ['context','docker-inputs']) write(root,'scripts/releases/'+name+'.mjs',readFileSync(new URL('./'+name+'.mjs',import.meta.url)));
    for(const id of ['api','web']) { write(root,`src/${id}/Dockerfile`,`FROM base@sha256:${hash}\nCOPY code /app\n`); write(root,`src/${id}/code`,'original'); }
    let seq=0;
    const prepare=()=>prepareRelease({root,output:join(root,'.git',String(seq++)),store,sourceCommit:commit,validationRunId:'42',repository:'example/repo',resolveExternal:false,verify:async()=>({}),prove:async()=>({})});
    const first=await prepare(); assert.equal(first.platform,null); assert.equal(first.images.length,2);
    assert.equal(readFileSync(join(root,'.git/0/contexts/api/code'),'utf8'),'original');
    for(const image of first.images) values.set(`artifacts/images/${image.recipe.id}/current.json`,{schemaVersion:'hosty-image-artifact.0.1',repository:image.recipe.repository,digest:'sha256:'+hash,inputFingerprint:image.inputFingerprint,buildSourceCommit:commit,provenance:'https://example.test/proof',platforms:image.recipe.platforms});
    values.set('apps/example.app/candidate.json',{inputFingerprint:first.apps[0].inputFingerprint,releaseId:hash});
    values.set('apps/example.app/feed-state.json',{feeds:{daily:{releaseId:hash}}});
    assert.ok((await prepare()).images.every(i=>i.decision==='unchanged-app'));
    write(root,'src/api/code','changed');
    assert.deepEqual((await prepare()).images.map(i=>i.decision),['build','reuse']);
    write(root,'src/api/code','original'); values.delete('apps/example.app/feed-state.json');
    const recovery=await prepare(); assert.equal(recovery.apps[0].recoveryOnly,true); assert.ok(recovery.images.every(i=>i.decision==='unchanged-app'));
  } finally { rmSync(root,{recursive:true,force:true}); }
});

test('native platform merge refuses missing or cross-source results before creating any tag', async () => {
  const root=mkdtempSync(join(tmpdir(),'hosty-platform-merge-'));
  const plan={sourceCommit:commit,repository:'example/repo',validationRunId:'42',images:[{recipe:{id:'api',repository:'example/api',platforms:['linux/amd64','linux/arm64']},decision:'build',inputFingerprint:hash}]};
  try {
    const receipt=platformReceipt(plan,'api','linux/amd64','sha256:'+hash);
    write(root,'api-linux-amd64.platform.json',JSON.stringify(receipt));
    const options={verify:async()=>({}),run:()=>assert.fail('Incomplete candidate must never get an index tag')};
    await assert.rejects(mergeImage(plan,'api',root,options),/ENOENT/);
    write(root,'api-linux-arm64.platform.json',JSON.stringify({...receipt,platform:'linux/arm64',sourceCommit:'c'.repeat(40)}));
    await assert.rejects(mergeImage(plan,'api',root,options),/differs/);
    assert.throws(()=>platformReceipt(plan,'api','linux/ppc64le','sha256:'+hash),/Unexpected/);
  } finally { rmSync(root,{recursive:true,force:true}); }
});

test('repository publication config preserves production runtimes and emits digest-pinned contracts', () => {
  const root=new URL('../../',import.meta.url), config=validatePublisher(JSON.parse(readFileSync(new URL('distribution/release-publisher.json',root))));
  const manifest=JSON.parse(readFileSync(new URL('manifest.json',root))), app=config.apps[0];
  assert.equal(app.id,manifest.id); assert.deepEqual(app.profiles,manifest.runtimeProfiles.filter(p=>!p.development).map(p=>p.key));
  const artifacts=[];
  for(const service of manifest.services) for(const [profile,runtime] of Object.entries(service.runtimes)) if(runtime.type==='docker'&&runtime.image) artifacts.push({type:'image',service:service.key,profile,repository:runtime.image.repository,digest:'sha256:'+hash,platforms:['linux/amd64','linux/arm64']});
  const release=appRelease({manifest,app,sourceCommit:commit,sourceFingerprint:hash,artifacts});
  assert.equal(release.manifest.schemaVersion,'app.0.2');
  if(app.profiles.includes('local')) assert.equal(release.manifest.source.commit,commit);
  for(const service of release.manifest.services) for(const runtime of Object.values(service.runtimes)) if(runtime.image) { assert.equal(runtime.image.digest,'sha256:'+hash); assert.equal(runtime.image.tag,undefined); }
  const ready=readFileSync(new URL('.github/workflows/ready-releases.yml',root),'utf8');
  assert.match(ready,/workflow_run:/); assert.match(ready,/node scripts\/releases\/ci-gate.mjs/); assert.match(ready,/matrix\.runner/); assert.doesNotMatch(ready,/setup-qemu/);
});
