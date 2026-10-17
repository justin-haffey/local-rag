const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { parseProjectConfiguration, editProjectReference, selectedSourceIds } = require('../out/projectConfiguration.js');
const examples = path.resolve(__dirname, '../../../docs/project-configuration/examples');
const instanceId = '5b91d028-3978-4f40-8b78-b15dbecfb41b';
const config = ids => ({ schemaVersion: 1, solution: { name: 'Project', description: 'Text with "knowledgeBases": [] is plain text' }, instanceId, knowledgeBases: ids.map(sourceId => ({sourceId})) });

test('published schema examples have matching editor outcomes', () => {
  for (const filename of fs.readdirSync(examples)) {
    const text = fs.readFileSync(path.join(examples, filename), 'utf8');
    if (filename.startsWith('valid')) assert.doesNotThrow(() => parseProjectConfiguration(text), filename);
    else assert.throws(() => parseProjectConfiguration(text), undefined, filename);
  }
  assert.deepEqual(JSON.parse(fs.readFileSync(path.resolve(__dirname, '../schemas/localrag.schema.json'), 'utf8').replace(/^\uFEFF/, '')), JSON.parse(fs.readFileSync(path.resolve(examples, '../localrag.schema.json'), 'utf8').replace(/^\uFEFF/, '')));
});

test('invalid shapes versions and duplicate properties never produce broad scope', () => {
  for (const text of ['null','[]','{}','{broken', JSON.stringify({...config([]),schemaVersion:2}), JSON.stringify({...config([]),solution:null}), '{"schemaVersion":1,"schemaVersion":1}']) assert.throws(() => parseProjectConfiguration(text));
  assert.throws(() => selectedSourceIds(config([]), instanceId, ['any']), /No knowledge bases/);
  assert.throws(() => selectedSourceIds(config(['stale']), instanceId, ['any']), /stale/);
  assert.throws(() => selectedSourceIds(config(['any']), 'another-instance', ['any']), /instance mismatch/);
});

test('two projects select exact local and shared sources including explicit cross-project local reuse', () => {
  const known = ['local-a','local-b','global'];
  assert.deepEqual(selectedSourceIds(config(['local-a','global']),instanceId,known), ['local-a','global']);
  assert.deepEqual(selectedSourceIds(config(['local-b','global']),instanceId,known), ['local-b','global']);
  assert.deepEqual(selectedSourceIds(config(['local-b']),instanceId,known), ['local-b']);
});

test('reference edits preserve surrounding formatting and unrelated solution values, are idempotent, and removal only edits membership', () => {
  const original = JSON.stringify(config(['a']), null, 4) + '\r\n';
  const added = editProjectReference(original, 'b');
  assert.deepEqual(parseProjectConfiguration(added).knowledgeBases, [{sourceId:'a'},{sourceId:'b'}]);
  assert.equal(added.slice(0, added.indexOf('"knowledgeBases": [') + 18), original.slice(0,original.indexOf('"knowledgeBases": [') + 18));
  assert.ok(added.endsWith('\r\n'));
  assert.equal(editProjectReference(added, 'b'), added);
  assert.deepEqual(parseProjectConfiguration(editProjectReference(added, 'a', true)).knowledgeBases, [{sourceId:'b'}]);
  assert.equal(parseProjectConfiguration(added).solution.description, config([]).solution.description);
});

test('invalid configuration stays read-only, opaque historical IDs are supported', () => {
  assert.throws(() => editProjectReference('{"schemaVersion":2}', 'new'));
  assert.equal(parseProjectConfiguration(editProjectReference(JSON.stringify(config([])), 'historical-opaque')).knowledgeBases[0].sourceId, 'historical-opaque');
});

const { applyReferenceChange } = require('../out/projectConfiguration.js');
function editHost(original = JSON.stringify(config(['a']))) {
  const state = { content: original, revision: 'original', writes: 0 };
  return {
    state,
    read: async () => ({ content: state.content, revision: state.revision }),
    validate: async content => parseProjectConfiguration(content),
    save: async (content, revision) => {
      if (revision !== state.revision) throw Error('revision conflict');
      state.content = content; state.revision = 'new'; state.writes++;
    }
  };
}

test('cancelled proposed edits perform no writes and closed-file conflicts retain the external edit', async () => {
  const host = editHost();
  assert.equal(await applyReferenceChange(host,'b',false,async()=>'',undefined,async()=>false), 'cancelled');
  assert.equal(host.state.writes,0);
  await assert.rejects(()=>applyReferenceChange(host,'b',false,async()=>'',undefined,async()=>{ host.state.content='external edit'; host.state.revision='external'; return true; }), /conflict/);
  assert.equal(host.state.content,'external edit');
  assert.equal(host.state.writes,0);
});

test('unsaved buffers retain unrelated changes, are never disk-saved, and concurrent buffer changes stop edits', async () => {
  const host = editHost();
  let text = JSON.stringify({...config(['a']),solution:{name:'Unsaved project rename',description:'Unsaved text'}});
  let version = 3;
  const buffer = {text:()=>text,version:()=>version,apply:async(proposed,expected)=>{assert.equal(expected,version);text=proposed;version++;return true;}};
  assert.equal(await applyReferenceChange(host,'b',false,async()=>'',buffer), 'buffer');
  assert.equal(parseProjectConfiguration(text).solution.name,'Unsaved project rename');
  assert.equal(host.state.writes,0);
  await assert.rejects(()=>applyReferenceChange(host,'c',false,async()=>'',buffer,async()=>{version++;return true;}), /changed/);
  assert.equal(parseProjectConfiguration(text).knowledgeBases.length,2);
});

test('external modification while a buffer is open is detected before applying editor edits', async () => {
  const host = editHost(); let applied = false;
  const buffer = {text:()=>host.state.content,version:()=>1,apply:async()=>{applied=true;return true;}};
  await assert.rejects(()=>applyReferenceChange(host,'b',false,async()=>'',buffer,async()=>{host.state.revision='external';return true;}), /changed/);
  assert.equal(applied,false);
});

test('index-success/file-failure retries only the membership write and invalid content stays untouched', async () => {
  const host = editHost(); const realSave=host.save; let attempts=0;
  host.save=async(...args)=>{if(attempts++===0)throw Error('disk unavailable');return realSave(...args);};
  await assert.rejects(()=>applyReferenceChange(host,'already-indexed-source',false,async()=>''), /disk unavailable/);
  assert.equal(host.state.writes,0);
  assert.equal(await applyReferenceChange(host,'already-indexed-source',false,async()=>''), 'saved');
  assert.equal(parseProjectConfiguration(host.state.content).knowledgeBases.filter(kb=>kb.sourceId==='already-indexed-source').length,1);
  host.state.content='{broken';
  await assert.rejects(()=>applyReferenceChange(host,'another',false,async()=>''));
  assert.equal(host.state.content,'{broken');
});

const { selectProjectConfigurationPath } = require('../out/projectConfiguration.js');
test('single multi-root empty and saved-workspace targets never union or walk parent roots', async () => {
  const a=path.resolve('project-a'); const b=path.resolve('project-b'); let prompts=0;
  const choose=async roots=>{prompts++;assert.deepEqual(roots,[a,b]);return b;};
  assert.equal(await selectProjectConfigurationPath([a],undefined,choose),path.join(a,'.localrag.json'));
  assert.equal(prompts,0);
  assert.equal(await selectProjectConfigurationPath([a,b],undefined,choose),path.join(b,'.localrag.json'));
  assert.equal(await selectProjectConfigurationPath([],undefined,async roots=>{assert.deepEqual(roots,[]);return a;}),path.join(a,'.localrag.json'));
  assert.equal(await selectProjectConfigurationPath([a,b],undefined,async()=>undefined),undefined);
  assert.equal(await selectProjectConfigurationPath([a,b],path.join(a,'.localrag.json'),async()=>{throw Error('must not discover');}),path.join(a,'.localrag.json'));
  await assert.rejects(()=>selectProjectConfigurationPath([a,b],'../.localrag.json',choose),/absolute/);
  await assert.rejects(()=>selectProjectConfigurationPath([a,b],undefined,async()=>path.resolve('not-an-open-root')),/exactly one/);
});

test('real browser-exported config round-trips through hand-author and extension membership paths', async () => {
  const browserText = fs.readFileSync(path.join(examples,'valid-browser-export.json'),'utf8');
  const browser = parseProjectConfiguration(browserText);
  assert.ok(browser.knowledgeBases.length > 0, 'website saved an explicit nonempty source list');
  const handAuthored = JSON.stringify({schemaVersion:1,solution:{name:browser.solution.name,description:browser.solution.description},instanceId:browser.instanceId,knowledgeBases:browser.knowledgeBases},null,4)+'\r\n';
  assert.deepEqual(parseProjectConfiguration(handAuthored),browser);
  const ids=browser.knowledgeBases.map(reference=>reference.sourceId);
  assert.deepEqual(selectedSourceIds(parseProjectConfiguration(handAuthored),browser.instanceId,ids),ids);
  const host=editHost(browserText);
  assert.equal(await applyReferenceChange(host,'extension-roundtrip-reference',false,async()=>''),'saved');
  assert.deepEqual(selectedSourceIds(parseProjectConfiguration(host.state.content),browser.instanceId,[...ids,'extension-roundtrip-reference']),[...ids,'extension-roundtrip-reference']);
  assert.equal(await applyReferenceChange(host,'extension-roundtrip-reference',true,async()=>''),'saved');
  assert.deepEqual(parseProjectConfiguration(host.state.content),browser);
  assert.equal(host.state.content.slice(0,host.state.content.indexOf('"knowledgeBases"')),browserText.slice(0,browserText.indexOf('"knowledgeBases"')));
  assert.throws(()=>selectedSourceIds(browser,browser.instanceId,[]),/Unknown\/deleted/);
});
