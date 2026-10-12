import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { test } from 'node:test';
const source = await readFile(new URL('../../Server/HexLive.VoiceBrain/Client/hexlive-voice.js', import.meta.url), 'utf8');
const { createVoiceClient, USER_SILENCE_MS } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
function fixture(connectLater=false) {
  let cb, resolve, now=0, next=0, ends=0, bills=0;
  const timers = new Map(), events=[];
  const conversation = { getId:()=> 'test-conversation', endSession:async()=> { ends++; cb.onDisconnect(); } };
  const client=createVoiceClient({
    later:(f,ms)=>{timers.set(++next,{at:now+ms,f});return next;}, cancel:id=>timers.delete(id),
    request:async url=> { if(url.endsWith('/end')) bills++; return {ok:true,json:async()=>url.endsWith('/end')?{seconds:20}:{ticket:'fixture',conversationToken:'fixture'}}; },
    sdk:async()=>({Conversation:{startSession:async options=>{cb=options;cb.onConnect();if(connectLater)return await new Promise(r=>resolve=r);return conversation;}}})
  });
  return { client, events, start:()=>client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7,onEvent:e=>events.push(e)}),
    callbacks:()=>cb, resolve:()=>resolve(conversation), counts:()=>({ends,bills}),
    async advance(ms) { now+=ms; for(const [id,t] of [...timers])if(t.at<=now){timers.delete(id);t.f();} await new Promise(r=>setImmediate(r)); } };
}
test('hang up exactly after 15 seconds, once',async()=>{const f=fixture();await f.start();assert.equal(USER_SILENCE_MS,15000);await f.advance(14999);assert(f.client.isActive());await f.advance(1);assert(!f.client.isActive());await f.client.stopVoice();assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('agent speech is excluded; repeated listening status does not extend silence',async()=>{const f=fixture();await f.start();await f.advance(14000);f.callbacks().onModeChange({mode:'speaking'});await f.advance(60000);assert(f.client.isActive());f.callbacks().onModeChange({mode:'listening'});await f.advance(10000);f.callbacks().onModeChange({mode:'listening'});await f.advance(5000);assert(!f.client.isActive());});
test('actual VAD speech and user transcript reset; background/no agent message does not',async()=>{const f=fixture();await f.start();await f.advance(14000);f.callbacks().onVadScore({vadScore:.8});await f.advance(14000);f.callbacks().onMessage({source:'user',message:'hello'});await f.advance(14000);assert(f.client.isActive());f.callbacks().onVadScore({vadScore:.1});f.callbacks().onMessage({source:'ai',message:'hello'});await f.advance(1000);assert(!f.client.isActive());});
test('remote disconnect bills and closes too',async()=>{const f=fixture();await f.start();f.callbacks().onDisconnect();await f.advance(1);assert.deepEqual(f.counts(),{ends:1,bills:1});assert(!f.client.isActive());});
test('stop during SDK startup closes late returned connection and bills once',async()=>{const f=fixture(true);const start=f.start();await new Promise(r=>setImmediate(r));await f.client.stopVoice();f.resolve();await start;assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('UI callback exceptions cannot prevent hang-up',async()=>{const f=fixture();f.events.push=()=>{throw Error('UI');};await f.start();await f.advance(15000);assert.deepEqual(f.counts(),{ends:1,bills:1});});
