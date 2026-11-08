const fs=require('node:fs'), vm=require('node:vm'), assert=require('node:assert/strict');
const source=fs.readFileSync('Assets/HexLive/UnityPresentation/Platform/HexBugMicrophone.jslib','utf8');
const flush=async()=>{for(let i=0;i<12;i++)await Promise.resolve();};
function setup(rate=48000){
 let resolve,reject,trackStops=0,closes=0,node,workletCode;
 const permission=new Promise((a,b)=>{resolve=a;reject=b;});
 const stream={getTracks:()=>[{stop(){trackStops++;}}]};
 class Context{constructor(){this.sampleRate=rate;this.destination={};this.audioWorklet={addModule:async()=>{}};}
 resume(){return Promise.resolve();}close(){closes++;return Promise.resolve();}
 createMediaStreamSource(){return {connect(){},disconnect(){}};}}
 class WorkletNode{constructor(){node=this;this.port={onmessage:null};}connect(){}disconnect(){}}
 const sandbox={LibraryManager:{library:{}},mergeInto:(_,v)=>Object.assign(sandbox,v),
 navigator:{mediaDevices:{getUserMedia:()=>permission}},window:{AudioContext:Context},AudioContext:Context,
 AudioWorkletNode:WorkletNode,Blob:class{constructor(parts){workletCode=parts.join('');}},
 URL:{createObjectURL:()=> 'blob:test',revokeObjectURL(){}},
 setTimeout:()=>1,clearTimeout(){},Uint8Array,Float32Array,DataView,HEAPU8:new Uint8Array(2*1024*1024)};
 vm.runInNewContext(source,sandbox);sandbox.HexBugMic=sandbox.$HexBugMic;
 return {s:sandbox,resolve:()=>resolve(stream),reject:()=>reject(Error('Denied')),node:()=>node,
 stops:()=>trackStops,closes:()=>closes,code:()=>workletCode};
}
(async()=>{
 let t=setup();assert.equal(t.s.HexLiveBugMicStart(),1);assert.equal(t.s.HexLiveBugMicState(),1);
 t.s.HexLiveBugMicCancel();t.resolve();await flush();assert.equal(t.s.HexLiveBugMicState(),0);assert.equal(t.stops(),1);
 t=setup();t.s.HexLiveBugMicStart();t.reject();await flush();assert.equal(t.s.HexLiveBugMicState(),-1);assert.equal(t.closes(),1);
 t=setup();t.s.HexLiveBugMicStart();t.resolve();await flush();assert.equal(t.s.HexLiveBugMicState(),2);
 const pcm=Float32Array.from({length:48000},(_,i)=>0.5*Math.sin(i*2*Math.PI*440/48000));
 t.node().port.onmessage({data:pcm});assert(t.s.HexLiveBugMicLevel()>0);
 assert.equal(t.s.HexLiveBugMicStop(),96044);assert.equal(t.stops(),1);assert.equal(t.closes(),1);
 assert.equal(t.s.HexLiveBugMicCopy(32,96044),96044);
 const wav=Buffer.from(t.s.HEAPU8.slice(32,32+96044));assert.equal(wav.toString('ascii',0,4),'RIFF');
 assert.equal(wav.readUInt32LE(24),48000);assert.equal(wav.readUInt16LE(22),1);assert.equal(wav.readUInt32LE(40),96000);
 assert(Math.abs(wav.readInt16LE(44+200*2)/32768-pcm[200])<0.0001);
 let Processor,posted;vm.runInNewContext(t.code(),{AudioWorkletProcessor:class{constructor(){this.port={postMessage:v=>posted=v};}},
 Float32Array,registerProcessor:(name,p)=>Processor=p});
 new Processor().process([[new Float32Array([1,0]),new Float32Array([-1,1])]],[]);
 assert.deepEqual(Array.from(posted),[0,0.5]);
 t=setup(8000);t.s.HexLiveBugMicStart();t.resolve();await flush();
 t.node().port.onmessage({data:new Float32Array(8000*91)});assert.equal(t.s.HexLiveBugMicState(),3);
 assert.equal(t.s.HexLiveBugMicStop(),44+8000*90*2);t.s.HexLiveBugMicCancel();assert.equal(t.s.HexLiveBugMicCopy(0,2000000),0);
 const receipt={passed:true,permissionCancellation:true,denialCleanup:true,pcmWavHeapRoundTrip:true,
 stereoToMonoWorklet:true,max90Seconds:true,physicalMicrophoneUsed:false};
 fs.mkdirSync('Build/Diagnostics',{recursive:true});
 fs.writeFileSync('Build/Diagnostics/reporter-web-microphone-check.json',JSON.stringify(receipt,null,2));console.log(receipt);
})().catch(e=>{console.error(e);process.exitCode=1;});
