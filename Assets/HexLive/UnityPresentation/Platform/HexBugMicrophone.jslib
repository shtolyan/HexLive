mergeInto(LibraryManager.library, {
  $HexBugMic: {
    state: 0, serial: 0, level: 0, chunks: [], samples: 0, rate: 0,
    stream: null, context: null, source: null, node: null, timer: 0, wav: null,
    release: function() {
      clearTimeout(HexBugMic.timer); HexBugMic.timer = 0;
      if (HexBugMic.node) { HexBugMic.node.port.onmessage = null; HexBugMic.node.disconnect(); }
      if (HexBugMic.source) HexBugMic.source.disconnect();
      if (HexBugMic.stream) HexBugMic.stream.getTracks().forEach(function(t) { t.stop(); });
      if (HexBugMic.context) HexBugMic.context.close().catch(function() {});
      HexBugMic.node = HexBugMic.source = HexBugMic.stream = HexBugMic.context = null;
      HexBugMic.level = 0;
    },
    reset: function() {
      ++HexBugMic.serial; HexBugMic.release(); HexBugMic.state = 0;
      HexBugMic.chunks = []; HexBugMic.samples = 0; HexBugMic.wav = null;
    },
    finish: function() {
      if (HexBugMic.state !== 2) {
        if (HexBugMic.state !== 3) { ++HexBugMic.serial; HexBugMic.release(); HexBugMic.state = -1; }
        return;
      }
      ++HexBugMic.serial; HexBugMic.release();
      var count = HexBugMic.samples, rate = HexBugMic.rate;
      var bytes = new Uint8Array(44 + count * 2), view = new DataView(bytes.buffer);
      function text(at, value) { for (var i=0;i<value.length;i++) bytes[at+i]=value.charCodeAt(i); }
      text(0,'RIFF'); view.setUint32(4,36+count*2,true); text(8,'WAVE'); text(12,'fmt ');
      view.setUint32(16,16,true); view.setUint16(20,1,true); view.setUint16(22,1,true);
      view.setUint32(24,rate,true); view.setUint32(28,rate*2,true);
      view.setUint16(32,2,true); view.setUint16(34,16,true); text(36,'data'); view.setUint32(40,count*2,true);
      var at=44;
      HexBugMic.chunks.forEach(function(chunk) {
        for(var i=0;i<chunk.length;i++) {
          var sample=Math.max(-1,Math.min(1,chunk[i]));
          view.setInt16(at,Math.round(sample*(sample<0?32768:32767)),true); at+=2;
        }
      });
      HexBugMic.wav=bytes; HexBugMic.chunks=[]; HexBugMic.state=3;
    },
    start: function(maxSeconds) {
      if (HexBugMic.state===1 || HexBugMic.state===2) return 0;
      HexBugMic.reset();
      if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || !window.AudioContext) return 0;
      var serial=HexBugMic.serial;
      HexBugMic.state=1;
      // Create/resume during the explicit button gesture, before permission awaits.
      try { HexBugMic.context=new AudioContext(); HexBugMic.context.resume().catch(function() {}); }
      catch (_) { HexBugMic.reset(); return 0; }
      HexBugMic.timer=setTimeout(function() {
        if (serial===HexBugMic.serial && HexBugMic.state===1) HexBugMic.finish();
      },90000);
      (async function() {
        var stream=null, context=HexBugMic.context, url=null;
        try {
          stream=await navigator.mediaDevices.getUserMedia({audio:{channelCount:1,echoCancellation:true,noiseSuppression:true},video:false});
          if (serial!==HexBugMic.serial) { stream.getTracks().forEach(function(t){t.stop();}); return; }
          HexBugMic.stream=stream;
          if (!context.audioWorklet || context.sampleRate<8000 || context.sampleRate>192000) throw new Error('Unsupported recording context');
          // A silent output keeps the input graph active without microphone feedback.
          var code="class HexBugCapture extends AudioWorkletProcessor { process(inputs,outputs) { const channels=inputs[0]; if(channels&&channels.length&&channels[0].length){ const pcm=new Float32Array(channels[0].length); for(let c=0;c<channels.length;c++)for(let i=0;i<pcm.length;i++)pcm[i]+=channels[c][i]/channels.length; this.port.postMessage(pcm,[pcm.buffer]); } return true; } } registerProcessor('hex-bug-capture',HexBugCapture);";
          url=URL.createObjectURL(new Blob([code],{type:'application/javascript'}));
          await context.audioWorklet.addModule(url);
          URL.revokeObjectURL(url); url=null;
          if(serial!==HexBugMic.serial) return;
          var source=context.createMediaStreamSource(stream), node=new AudioWorkletNode(context,'hex-bug-capture');
          HexBugMic.source=source; HexBugMic.node=node; HexBugMic.rate=context.sampleRate;
          node.port.onmessage=function(event) {
            if(serial!==HexBugMic.serial || HexBugMic.state!==2) return;
            var pcm=event.data, remaining=maxSeconds*HexBugMic.rate-HexBugMic.samples;
            if(!(pcm instanceof Float32Array)) return;
            if(pcm.length>remaining) pcm=pcm.slice(0,remaining);
            if(pcm.length) {
              HexBugMic.chunks.push(pcm); HexBugMic.samples+=pcm.length;
              var energy=0; for(var i=0;i<pcm.length;i++) energy+=pcm[i]*pcm[i];
              HexBugMic.level=Math.min(1,Math.sqrt(energy/pcm.length)*8);
            }
            if(HexBugMic.samples>=maxSeconds*HexBugMic.rate) HexBugMic.finish();
          };
          source.connect(node); node.connect(context.destination);
          await context.resume();
          if(serial!==HexBugMic.serial) return;
          clearTimeout(HexBugMic.timer); HexBugMic.state=2;
          HexBugMic.timer=setTimeout(function(){if(serial===HexBugMic.serial)HexBugMic.finish();},maxSeconds*1000);
        } catch (_) {
          if (serial===HexBugMic.serial) { ++HexBugMic.serial; HexBugMic.release(); HexBugMic.state=-1; }
        } finally { if(url)URL.revokeObjectURL(url); }
      })();
      return 1;
    }
  },
  HexLiveBugMicStart__deps: ['$HexBugMic'],
  HexLiveBugMicStart: function(maxSeconds) { return HexBugMic.start(maxSeconds); },
  HexLiveBugMicState__deps: ['$HexBugMic'],
  HexLiveBugMicState: function() { return HexBugMic.state; },
  HexLiveBugMicLevel__deps: ['$HexBugMic'],
  HexLiveBugMicLevel: function() { return HexBugMic.level; },
  HexLiveBugMicStop__deps: ['$HexBugMic'],
  HexLiveBugMicStop: function() { HexBugMic.finish(); return HexBugMic.wav ? HexBugMic.wav.length : 0; },
  HexLiveBugMicCopy__deps: ['$HexBugMic'],
  HexLiveBugMicCopy: function(destination,capacity) {
    if (!HexBugMic.wav || capacity<HexBugMic.wav.length) return 0;
    HEAPU8.set(HexBugMic.wav,destination); return HexBugMic.wav.length;
  },
  HexLiveBugMicCancel__deps: ['$HexBugMic'],
  HexLiveBugMicCancel: function() { HexBugMic.reset(); }
});
