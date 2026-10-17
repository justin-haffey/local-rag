const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { sharedHostToken } = require('../out/sharedHostToken.js');

test('editor windows reuse one shared host token and retain a previous credential on initial migration', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'localrag-token-'));
  try {
    const previous = 'x'.repeat(43);
    assert.equal(await sharedHostToken(root,previous),previous);
    const values = await Promise.all(Array.from({length:8},()=>sharedHostToken(root,'y'.repeat(43))));
    assert.ok(values.every(value=>value === previous));
  } finally { await fs.rm(root,{recursive:true,force:true}); }
});

function failure(code) { return Object.assign(new Error('simulated file access failure'),{code}); }
function storageWith(overrides={}) {
  return {createDirectory:async()=>{},createExclusive:async()=>{throw failure('EEXIST');},read:async()=> 'x'.repeat(43),pause:async()=>{},...overrides};
}

test('launcher sharing violations during exclusive creation and reading retry before returning the winner token', async () => {
  let creates=0;let reads=0;const delays=[];
  const storage=storageWith({
    createExclusive:async()=>{creates++;throw failure(creates===1?'EACCES':'EEXIST');},
    read:async()=>{reads++;if(reads===1)throw failure('EBUSY');return 'x'.repeat(43);},
    pause:async delay=>{delays.push(delay);}
  });
  assert.equal(await sharedHostToken('unused-test-directory','y'.repeat(43),storage),'x'.repeat(43));
  assert.equal(creates,3);assert.equal(reads,2);assert.deepEqual(delays,[50,50]);
});

test('permanent permission denial stops within the retry bound and gives actionable repair guidance', async () => {
  let attempts=0;let delays=0;
  const storage=storageWith({createExclusive:async()=>{attempts++;throw failure('EPERM');},pause:async()=>{delays++;}});
  await assert.rejects(()=>sharedHostToken('unused-test-directory',undefined,storage),/access is denied.*permissions.*repair/i);
  assert.equal(attempts,20);assert.equal(delays,19);
});

test('partial or malformed credentials are never accepted or silently overwritten', async () => {
  for(const token of ['x'.repeat(31),'x'.repeat(32)+' '+'y'.repeat(32),'x'.repeat(32)+'\u0000']) {
    let attempts=0;
    const storage=storageWith({read:async()=>{attempts++;return token;}});
    await assert.rejects(()=>sharedHostToken('unused-test-directory',undefined,storage),/incomplete or invalid/);
    assert.equal(attempts,20);
  }
});

test('actual Windows FileShare.None lock is retried until the launcher-style writer releases it', {skip:process.platform!=='win32',timeout:15000}, async () => {
  const {spawn}=require('node:child_process');
  const {once}=require('node:events');
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'localrag-token-lock-'));
  const script = "$ErrorActionPreference='Stop'; $stream=[IO.File]::Open($env:LOCALRAG_LOCK_TEST_PATH,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); try { $payload=[Text.Encoding]::UTF8.GetBytes(('x'*43)); $stream.Write($payload,0,$payload.Length); $stream.Flush($true); [Console]::WriteLine('LOCKED'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null } finally { $stream.Dispose() }";
  const child=spawn('powershell.exe',['-NoProfile','-Command',script],{windowsHide:true,stdio:['pipe','pipe','pipe'],env:{...process.env,LOCALRAG_LOCK_TEST_PATH:path.join(root,'client-token')}});
  const exited=once(child,'exit');
  let timer;
  try {
    await new Promise((resolve,reject)=>{
      let output='';
      child.stdout.on('data',chunk=>{output+=chunk.toString();if(output.includes('LOCKED'))resolve();});
      child.once('error',reject);child.once('exit',code=>{if(!output.includes('LOCKED'))reject(Error('Lock holder exited before readiness: '+code));});
    });
    await assert.rejects(()=>fs.readFile(path.join(root,'client-token'),'utf8'),error=>['EACCES','EBUSY','EPERM'].includes(error.code));
    const reading=sharedHostToken(root,'y'.repeat(43));
    timer=setTimeout(()=>child.stdin.end('\n'),150);
    assert.equal(await reading,'x'.repeat(43));
    assert.equal((await exited)[0],0);
  } finally {
    clearTimeout(timer);child.stdin.end('\n');
    if(child.exitCode===null) await exited;
    await fs.rm(root,{recursive:true,force:true});
  }
});
