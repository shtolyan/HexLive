import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import assert from 'node:assert/strict';
import { test } from 'node:test';
const source=await readFile(new URL('../../Assets/HexLive/UnityPresentation/Platform/HexVoiceAgent.jslib',import.meta.url),'utf8');
function bridge(moduleReady=true) {
  let library,ready,stops=0;const starts=[],scripts=[];
  const module={startVoice:o=>{starts.push(o);return Promise.resolve();},stopVoice:()=>{stops++;}};
  const window={addEventListener:(name,f)=>{ready=f;},__hexVoiceModule:module};
  if(moduleReady)window.__hexVoice={queue:[],module};
  runInNewContext(source,{window,document:{createElement:()=>({}),head:{appendChild:s=>scripts.push(s)}},LibraryManager:{library:{}},
    mergeInto:(_,x)=>library=x,UTF8ToString:x=>x||'',lengthBytesUTF8:x=>Buffer.byteLength(x),stringToUTF8:()=>{}});
  return {library,window,starts,scripts,stops:()=>stops,ready:()=>ready()};
}
test('mode switch clears frozen events and rejects late callbacks from old call',()=>{
  const b=bridge();b.library.HexVoiceStart('https://fixture','fixture','fixture',7,'4');
  assert.equal(b.starts[0].controlEpoch,4);
  const old=b.starts[0].onEvent;old({type:'state',state:'listening'});
  assert(b.library.HexVoiceNextSize()>0);
  b.library.HexVoiceStop();assert.equal(b.stops(),1);assert.equal(b.library.HexVoiceNextSize(),0);
  old({type:'state',state:'speaking'});assert.equal(b.library.HexVoiceNextSize(),0);
  b.library.HexVoiceStart('https://fixture','fixture','fixture',7,'5');
  old({type:'agent',text:'late'});b.starts[1].onEvent({type:'state',state:'listening'});
  assert.equal(b.window.__hexVoice.queue.length,1);assert.equal(b.window.__hexVoice.queue[0].state,'listening');
});
test('cancelled module loading cannot start a phone after AI/manual selection',()=>{
  const b=bridge(false);b.library.HexVoiceStart('https://fixture','fixture','fixture',7,'1');
  b.library.HexVoiceStop();b.ready();assert.equal(b.starts.length,0);
  b.library.HexVoiceStart('https://fixture','fixture','fixture',7,'3');assert.equal(b.starts.length,1);
});
test('module error is delivered only to the latest pending call',()=>{
  const b=bridge(false);b.library.HexVoiceStart('https://fixture','fixture','fixture',7,'1');
  b.library.HexVoiceStop();b.library.HexVoiceStart('https://fixture','fixture','fixture',7,'2');
  b.scripts[0].onerror();assert.equal(b.window.__hexVoice.queue.length,1);
  assert.equal(b.window.__hexVoice.queue[0].detail,'module');
});
