#!/usr/bin/env node
// §168.11: drives one ?perf=1 measurement of a WebGL build on the local stand
// (Tools/webgl_perf_stand.py) in a headless Chrome, so the numbers do not
// depend on a visible window: a background tab is throttled to ~1 frame a
// second and its WebPerf summaries are useless.
//
//   node Tools/webgl_perf_probe.mjs --session <work>/session.json \
//        [--port 5201] [--query "rscale=1&bloom=0"] [--seconds 60] [--warmup 20] \
//        [--size 1600x900] [--out result.txt]
//
// A throwaway Chrome profile is created and deleted; the stand's emulator
// session is written into that profile's localStorage only. The page is
// walked through the HTML menu by clicking: the server card, then any
// acknowledgement dialog. After the warm-up every `[WebPerf]` summary is
// recorded; the output is the raw summaries plus the GPU string.

import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, i, all) =>
  value.startsWith('--') ? [...pairs, [value.slice(2), all[i + 1]]] : pairs, []));
const port = Number(args.port ?? 5201);
const seconds = Number(args.seconds ?? 60);
const warmup = Number(args.warmup ?? 20);
const [width, height] = (args.size ?? '1600x900').split('x').map(Number);
const chrome = args.chrome ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
if (!args.session) throw new Error('--session <work>/session.json is required');
const session = readFileSync(args.session, 'utf8').trim();
const origin = `http://localhost:${port}`;
const url = `${origin}/play/?perf=1${args.query ? '&' + args.query : ''}`;

const profile = mkdtempSync(join(tmpdir(), 'hexlive-perf-probe.'));
const browser = spawn(chrome, [
  '--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
  `--window-size=${width},${height}`, '--no-first-run', '--no-default-browser-check',
  '--ignore-gpu-blocklist', '--enable-gpu', '--use-angle=metal',
  '--autoplay-policy=no-user-gesture-required', 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });

const endpoint = await new Promise((resolve, reject) => {
  let text = '';
  browser.stderr.on('data', chunk => {
    text += chunk;
    const match = text.match(/DevTools listening on (ws:\/\/\S+)/);
    if (match) resolve(match[1]);
  });
  browser.on('exit', code => reject(new Error(`chrome exited ${code}`)));
});
const debugPort = new URL(endpoint).port;
const pages = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json();
const socket = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
await new Promise(resolve => socket.addEventListener('open', resolve, { once: true }));

let nextId = 1;
const pending = new Map();
const summaries = [];
const notes = [];
let measuring = false;
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  } else if (message.method === 'Runtime.consoleAPICalled') {
    const text = message.params.args.map(arg => arg.value ?? '').join(' ');
    if (measuring && text.startsWith('[WebPerf]')) summaries.push(text);
    else if (/^\[(WebPerf\] (census|probe|shadows)|GraphicsPerf\])/.test(text)) notes.push(text);
  }
});
const send = (method, params = {}) => new Promise(resolve => {
  const id = nextId++;
  pending.set(id, resolve);
  socket.send(JSON.stringify({ id, method, params }));
});
const evaluate = async expression =>
  (await send('Runtime.evaluate', { expression, returnByValue: true })).result?.result?.value;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

try {
  await send('Runtime.enable');
  await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  // Same origin first, so the session lands in the page's own localStorage.
  await send('Page.navigate', { url: `${origin}/play/server-directory.json` });
  await sleep(1000);
  await evaluate(`localStorage.setItem('hexlive.session', ${JSON.stringify(session)})`);
  await send('Page.navigate', { url });
  const gpu = await (async () => {
    await sleep(1000);
    return evaluate(`(() => { const gl = document.createElement('canvas').getContext('webgl2');
      const info = gl && gl.getExtension('WEBGL_debug_renderer_info');
      return info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : 'unknown'; })()`);
  })();

  // Walk the menu: the server card, then acknowledgement dialogs.
  let inWorld = false;
  for (let i = 0; i < 180 && !inWorld; i++) {
    await sleep(1000);
    const clicked = await evaluate(`(() => {
      const go = document.querySelector('.hx-server__go:not([disabled])');
      if (go) { go.click(); return 'server'; }
      const ack = [...document.querySelectorAll('button')].find(b => b.offsetParent &&
        /^(Got it|Понятно|OK|Ок)$/i.test(b.textContent.trim()));
      if (ack) { ack.click(); return 'ack'; }
      return document.querySelector('.hx-weather') ? 'hud' : '';
    })()`);
    // The HUD shows while the "your character" card is still up; the card
    // is dismissed first, so only a HUD with nothing left to click counts.
    if (clicked === 'hud') inWorld = true;
  }
  if (!inWorld) throw new Error('never reached the world (no HUD) within 180 s');

  await sleep(warmup * 1000);
  measuring = true;
  const shot = async suffix => {
    if (!args.out) return;
    const image = await send('Page.captureScreenshot', { format: 'jpeg', quality: 70 });
    if (image.result?.data) writeFileSync(args.out.replace(/\.txt$/, '') + suffix + '.jpg',
      Buffer.from(image.result.data, 'base64'));
  };
  await shot('-start');
  await sleep(seconds * 1000);
  measuring = false;
  await shot('-end');

  const report = [`url ${url}`, `size ${width}x${height}`, `gpu ${gpu}`, `summaries ${summaries.length}`,
    '', ...notes, '', ...summaries].join('\n');
  if (args.out) writeFileSync(args.out, report + '\n');
  console.log(report);
} finally {
  socket.close();
  browser.kill();
  await sleep(500);
  rmSync(profile, { recursive: true, force: true });
}
