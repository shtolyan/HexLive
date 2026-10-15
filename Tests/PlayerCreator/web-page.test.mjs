import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function bridge(locks) {
  const storage = new Map(); const output = new Map(); let next = 0;
  const context = { LibraryManager: { library: {} }, mergeInto: Object.assign,
    window: {}, navigator: locks ? { locks } : {}, UTF8ToString: value => value,
    localStorage: { getItem: key => storage.get(key), setItem: (k,v) => storage.set(k,v), removeItem: k => storage.delete(k) },
    _malloc: () => ++next, lengthBytesUTF8: text => Buffer.byteLength(text), stringToUTF8: (value, pointer) => output.set(pointer,value) };
  vm.runInNewContext(readFileSync('Assets/HexLive/UnityPresentation/Platform/HexWebPage.jslib','utf8'),context);
  return { api: context.LibraryManager.library, context, storage, output };
}
test('draft bridge confines storage to creator keys and fails closed on storage denial',()=>{
  const b=bridge(); const a=b.api;
  assert.equal(a.HexCreatorStoreSet('hexlive.session','must-not-write'),0);
  assert.equal(a.HexCreatorStoreSet('hexlive.creator.fixture','x'.repeat(32769)),0);
  assert.equal(a.HexCreatorStoreSet('hexlive.characterAttempt.fixture','{"requestId":"immutable"}'),1);
  const pointer=a.HexCreatorStoreGet('hexlive.characterAttempt.fixture');
  assert.equal(b.output.get(pointer),'{"requestId":"immutable"}');
  b.context.localStorage.setItem=()=>{throw new Error('denied');};
  assert.equal(a.HexCreatorStoreSet('hexlive.creator.fixture','draft'),0);
  assert.equal(a.HexCreatorStoreGet('hexlive.session'),null);
});
test('closing before asynchronous lock delivery never leaves an acquired lock held',async()=>{
  let deliver; const b=bridge({ request: (key, options, callback)=>new Promise(resolve=>{deliver=()=>resolve(callback({name:key}));}) });
  const key='hexlive.character.fixture';
  assert.equal(b.api.HexCreatorLockStart(key),0);
  b.api.HexCreatorLockRelease(key); await deliver();
  assert.equal(b.api.HexCreatorLockStatus(key),2);
  assert.equal(b.context.window.__hexCreatorLocks.size,0);
});
test('one held browser lock excludes another tab and release permits retry',async()=>{
  const active=new Set(); const locks={ request: async(key,options,callback)=>{
    if(active.has(key))return callback(null);
    active.add(key); try {return await callback({name:key});} finally {active.delete(key);}
  }};
  const first=bridge(locks), second=bridge(locks), key='hexlive.character.fixture';
  first.api.HexCreatorLockStart(key);await Promise.resolve();
  assert.equal(first.api.HexCreatorLockStatus(key),1);
  second.api.HexCreatorLockStart(key);await Promise.resolve();assert.equal(second.api.HexCreatorLockStatus(key),2);
  second.api.HexCreatorLockRelease(key);assert.equal(first.api.HexCreatorLockStatus(key),1);
  first.api.HexCreatorLockRelease(key);await Promise.resolve();await Promise.resolve();
  second.api.HexCreatorLockStart(key);await Promise.resolve();assert.equal(second.api.HexCreatorLockStatus(key),1);
  second.api.HexCreatorLockRelease(key);
});
