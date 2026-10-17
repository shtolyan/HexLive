import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { test } from 'node:test';
const source = await readFile(new URL('../../Server/HexLive.VoiceBrain/Client/hexlive-voice.js', import.meta.url), 'utf8');
const { createVoiceClient, USER_SILENCE_MS, CONNECTION_TIMEOUT_MS } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
test('control release does not delay hangup billing and forwards fenced epoch',async()=>{
  const requests=[];let cb,ends=0;
  const client=createVoiceClient({later:()=>0,cancel:()=>{},request:async(url,o)=>{
    requests.push({url,body:JSON.parse(o.body)});
    if(url.endsWith('/cancel'))return new Promise(()=>{});
    return {ok:true,json:async()=>url.endsWith('/end')?{seconds:1}:{ticket:'fixture',conversationToken:'fixture'}};
  },sdk:async()=>({Conversation:{startSession:async o=>{cb=o;cb.onConnect();return{getId:()=> 'fixture',endSession:async()=>{ends++;cb.onDisconnect();}};}}})});
  await client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7,controlEpoch:42});
  await client.stopVoice();await client.stopVoice();
  assert.equal(requests[0].body.controlEpoch,42);assert.equal(ends,1);
  assert.equal(requests.filter(r=>r.url.endsWith('/cancel')).length,1);
  assert.equal(requests.filter(r=>r.url.endsWith('/end')).length,1);
});
test('a cancelled pending HTTP session is released when its late ticket arrives',async()=>{
  let resolve;const requests=[];
  const client=createVoiceClient({later:()=>0,cancel:()=>{},sdk:async()=>{throw Error('must not load SDK');},request:async(url,o)=>{
    requests.push(url);if(url.endsWith('/session'))return await new Promise(r=>resolve=r);
    return {ok:true};
  }});
  const starting=client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7});
  await new Promise(r=>setImmediate(r));await client.stopVoice();
  resolve({ok:true,json:async()=>({ticket:'late',conversationToken:'fixture'})});await starting;
  assert.equal(client.isActive(),false);assert.equal(requests.filter(x=>x.endsWith('/cancel')).length,1);
});
function fixture(connectLater=false) {
  let cb, resolve, now=0, next=0, ends=0, bills=0, inputVolume=0;
  const timers = new Map(), events=[];
  const conversation = { getId:()=> 'test-conversation', getInputVolume:()=>inputVolume, endSession:async()=> { ends++; cb.onDisconnect(); } };
  const client=createVoiceClient({
    later:(f,ms)=>{timers.set(++next,{at:now+ms,f});return next;}, cancel:id=>timers.delete(id),
    clock:()=>now,
    request:async url=> { if(url.endsWith('/end')) bills++; return {ok:true,json:async()=>url.endsWith('/end')?{seconds:20}:{ticket:'fixture',conversationToken:'fixture'}}; },
    sdk:async()=>({Conversation:{startSession:async options=>{cb=options;cb.onConnect();if(connectLater)return await new Promise(r=>resolve=r);return conversation;}}})
  });
  return { client, events, start:()=>client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7,onEvent:e=>events.push(e)}),
    callbacks:()=>cb, input:value=>{inputVolume=value;}, resolve:()=>resolve(conversation), counts:()=>({ends,bills}),
    async advance(ms) { now+=ms; for(const [id,t] of [...timers])if(t.at<=now){timers.delete(id);t.f();} await new Promise(r=>setImmediate(r)); } };
}
test('hang up exactly after 15 seconds, once',async()=>{const f=fixture();await f.start();assert.equal(USER_SILENCE_MS,15000);await f.advance(14999);assert(f.client.isActive());await f.advance(1);assert(!f.client.isActive());await f.client.stopVoice();assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('agent speech pauses remaining silence; its reminders cannot grant another full 15 seconds',async()=>{const f=fixture();await f.start();await f.advance(14000);f.callbacks().onModeChange({mode:'speaking'});await f.advance(60000);assert(f.client.isActive());f.callbacks().onModeChange({mode:'listening'});await f.advance(999);f.callbacks().onModeChange({mode:'listening'});assert(f.client.isActive());await f.advance(1);assert(!f.client.isActive());assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('actual VAD speech and user transcript reset; background/no agent message does not',async()=>{const f=fixture();await f.start();await f.advance(14000);f.callbacks().onVadScore({vadScore:.8});await f.advance(14000);f.callbacks().onMessage({source:'user',message:'hello'});await f.advance(14000);assert(f.client.isActive());f.callbacks().onVadScore({vadScore:.1});f.callbacks().onMessage({source:'ai',message:'hello'});await f.advance(1000);assert(!f.client.isActive());});
test('remote disconnect bills and closes too',async()=>{const f=fixture();await f.start();f.callbacks().onDisconnect();await f.advance(1);assert.deepEqual(f.counts(),{ends:1,bills:1});assert(!f.client.isActive());});
test('stop during SDK startup closes late returned connection and bills once',async()=>{const f=fixture(true);const start=f.start();await new Promise(r=>setImmediate(r));await f.client.stopVoice();f.resolve();await start;assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('UI callback exceptions cannot prevent hang-up',async()=>{const f=fixture();f.events.push=()=>{throw Error('UI');};await f.start();await f.advance(15000);assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('long VAD-confirmed speech stays connected before its final transcript',async()=>{const f=fixture();await f.start();for(let i=0;i<6;i++){await f.advance(10000);f.callbacks().onVadScore({vadScore:.9});}assert(f.client.isActive());f.callbacks().onVadScore({vadScore:0});await f.advance(14999);assert(f.client.isActive());await f.advance(1);assert(!f.client.isActive());});
test('steady loud background cannot keep renewing the call',async()=>{const f=fixture();f.input(.4);await f.start();await f.advance(1000);await f.advance(1000);await f.advance(13000);assert(!f.client.isActive());assert.deepEqual(f.counts(),{ends:1,bills:1});});
test('mic fallback renews on a new burst, never on every sample of continuous noise',async()=>{const f=fixture();await f.start();f.input(.01);await f.advance(200);f.input(.3);await f.advance(200);await f.advance(10000);assert(f.client.isActive());await f.advance(5000);assert(!f.client.isActive());});
test('low VAD suppresses microphone-volume false positives',async()=>{const f=fixture();await f.start();f.callbacks().onVadScore({vadScore:0});for(let i=0;i<15;i++){f.input(i%2?.9:.01);await f.advance(1000);}assert(!f.client.isActive());});
test('quiet mic noise does not extend the call',async()=>{const f=fixture();await f.start();f.input(.01);await f.advance(14000);await f.advance(1000);assert(!f.client.isActive());});
test('depleted credit error remains visible while the connection is closed',async()=>{const events=[];const client=createVoiceClient({request:async()=>({ok:false,status:402})});await assert.rejects(client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7,onEvent:e=>events.push(e)}));assert(!client.isActive());assert.equal(events.at(-1).state,'error');assert.equal(events.at(-1).detail,'session:402');});
for (const stage of ['session', 'sdk', 'webrtc']) test('hung '+stage+' setup ends with a specific timeout; cancelled attempts cannot overwrite a later call', async()=>{
  let deadline, calls=0, aborted=false, resolveLate, ends=0, callbacks;
  const events=[];
  const conversation={getId:()=> 'fixture-call',endSession:async()=>{ends++;}};
  const lateConversation={getId:()=> 'fixture-old-call',endSession:async()=>{ends++;}};
  const client=createVoiceClient({later:(f,ms)=>{if(ms===CONNECTION_TIMEOUT_MS)deadline=f;return ms;},cancel:()=>{},
    request:async(url,options)=>{
      if(url.endsWith('/end'))return {ok:true,json:async()=>({seconds:1})};
      calls++;
      if(stage==='session' && calls===1)return await new Promise((_,reject)=>options.signal.addEventListener('abort',()=>{aborted=true;reject(new DOMException('cancelled old attempt','AbortError'));}));
      return {ok:true,json:async()=>({ticket:'fixture',conversationToken:'fixture'})};
    },
    sdk:async()=>{
      if(stage==='sdk' && calls===1)await new Promise(r=>resolveLate=r);
      return {Conversation:{startSession:async o=>{callbacks=o;if(stage==='webrtc' && calls===1)return await new Promise(r=>resolveLate=r);o.onConnect();return conversation;}}};
    }});
  const start=()=>client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7,onEvent:e=>events.push(e)});
  const old=start();await new Promise(r=>setImmediate(r));
  assert.equal(CONNECTION_TIMEOUT_MS,30000);deadline();await new Promise(r=>setImmediate(r));
  assert(!client.isActive());assert.equal(events.at(-1).detail,'connect_timeout:'+stage);
  if(stage==='session')assert(aborted);
  await start();assert(client.isActive());assert.equal(events.at(-1).state,'listening');
  if(resolveLate)resolveLate(stage==='webrtc'?lateConversation:undefined);
  await old;await new Promise(r=>setImmediate(r));
  assert.equal(events.filter(e=>e.type==='state').at(-1).state,'listening','Late cancellation/SDK completion cannot report an error for the new call.');
  if(stage==='webrtc')assert.equal(ends,1,'Late returned connection must close.');
  await client.stopVoice();
});
test('§171.26: the server-written first line opens the call, an absent one leaves the agent default',async()=>{
  const seen=[];
  for(const firstMessage of ['Ну наконец-то.',null,'   ']){
    const client=createVoiceClient({later:()=>0,cancel:()=>{},
      request:async url=>({ok:true,json:async()=>url.endsWith('/end')?{seconds:1}:{ticket:'t',conversationToken:'c',voiceId:'v',firstMessage}}),
      sdk:async()=>({Conversation:{startSession:async o=>{seen.push(o.overrides);o.onConnect();return{getId:()=>'x',endSession:async()=>o.onDisconnect()};}}})});
    await client.startVoice({server:'https://fixture',bearer:'fixture',npcId:7});
    await client.stopVoice();
  }
  assert.deepEqual(seen[0],{tts:{voiceId:'v'},agent:{firstMessage:'Ну наконец-то.'}});
  assert.deepEqual(seen[1],{tts:{voiceId:'v'}});
  assert.deepEqual(seen[2],{tts:{voiceId:'v'}});
});
