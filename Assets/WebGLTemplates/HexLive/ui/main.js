// §174: entry of the page's HTML interface. index.html (the WebGL template)
// and the stand (Tools/webui_stand.py) both load this file unchanged.
import { html, render, useEffect, useState } from './html.js';
import * as bridge from './bridge.js';
import { SCREENS } from './screens/index.js';

function App() {
  const [, setVersion] = useState(0);
  useEffect(() => bridge.subscribe(() => setVersion((v) => v + 1)), []);
  const open = [...bridge.openScreens()];
  return html`${open.map(([id, model]) => {
    const Screen = SCREENS[id];
    if (!Screen) {
      console.warn(`[HexUi] no screen "${id}"`);
      return null;
    }
    return html`<${Screen} key=${id} model=${model} send=${(cmd, args) => bridge.send(id, cmd, args)} />`;
  })}`;
}

// §174.5: one rem follows the height of the game area — 16 px at 900 px,
// clamped so a small embedded canvas stays readable and a 4K one not tiny.
function watchScale(root) {
  const apply = () => {
    const scale = Math.min(1.5, Math.max(0.8, root.clientHeight / 900));
    document.documentElement.style.fontSize = `${(16 * scale).toFixed(2)}px`;
    root.style.setProperty('--hx-scale', scale.toFixed(3));
  };
  new ResizeObserver(apply).observe(root);
  apply();
}

const root = document.getElementById('hex-ui');
watchScale(root);
render(html`<${App} />`, root);

window.HexUi = { receive: bridge.receive, take: bridge.take, pointerBits: bridge.pointerBits };
// Messages Unity posted before this module finished loading (HexHtmlUi.jslib).
for (const json of (window.__hexUiInbox || []).splice(0)) bridge.receive(json);
