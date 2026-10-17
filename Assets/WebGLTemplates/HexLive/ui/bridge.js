// §174.2: the page half of HtmlUi.cs. Unity posts JSON here (HexUiPost in
// HexHtmlUi.jslib → receive); the page never calls into Unity — it queues
// commands and Unity takes them once per frame (take, pointerBits).
//
// Bump PROTOCOL together with HtmlUi.Protocol: a page cached from another
// build then stays silent and the player keeps its UI Toolkit windows.
import { setStrings } from './i18n.js';

export const PROTOCOL = 2; // 2: asset messages

const outbox = [];
const screens = new Map(); // id → model, in opening order
const assets = new Map(); // key → data URL, sent once by HtmlUi.Asset
const listeners = new Set();
let bits = 0;
let lastPointer = null;

const POINTER_OVER = 1;
const PRESS_HELD = 2;
const TEXT_FOCUS = 4;

function emit() {
  for (const listener of listeners) listener();
  // A window opening under a resting mouse gets no pointermove: re-test the
  // last known position once the DOM has the new window.
  requestAnimationFrame(() => {
    if (lastPointer) setBit(POINTER_OVER, isSurface(document.elementFromPoint(lastPointer.x, lastPointer.y)));
  });
}

function setBit(bit, on) {
  bits = on ? bits | bit : bits & ~bit;
}

function isSurface(element) {
  return !!(element && element.closest && element.closest('.hx-surface'));
}

function isTextField(element) {
  if (!element || !element.closest || !element.closest('#hex-ui')) return false;
  if (element.isContentEditable || element.tagName === 'TEXTAREA') return true;
  return element.tagName === 'INPUT' && !/^(range|checkbox|radio|button|submit|color)$/i.test(element.type);
}

/** Unity → page. Unknown message types are ignored, not fatal. */
export function receive(json) {
  let message;
  try {
    message = JSON.parse(json);
  } catch (e) {
    console.warn('[HexUi] unreadable message', e);
    return;
  }
  switch (message.t) {
    case 'hello':
      if (message.v !== PROTOCOL) console.warn(`[HexUi] player protocol ${message.v}, page ${PROTOCOL}`);
      outbox.push(JSON.stringify({ t: 'ready', v: PROTOCOL }));
      break;
    case 'strings':
      setStrings(message.lang, message.terms);
      emit();
      break;
    case 'show':
      // A new window opens on top; a model update keeps the window where it
      // is (Map keeps insertion order), so a HUD refreshed every second never
      // climbs over a dialog. To raise a window, hide and show it.
      screens.set(message.screen, message.model || {});
      emit();
      break;
    case 'hide':
      if (screens.delete(message.screen)) emit();
      break;
    case 'asset':
      assets.set(message.key, message.url);
      emit();
      break;
  }
}

/** An image a model refers to by key (HtmlUi.Asset), or undefined until it arrives. */
export function asset(key) {
  return key ? assets.get(key) : undefined;
}

/** Page → Unity: one player command for a screen. */
export function send(screen, cmd, args = {}) {
  outbox.push(JSON.stringify({ t: 'cmd', screen, cmd, args }));
}

/** Unity polls: the oldest queued message, or null. */
export function take() {
  return outbox.length ? outbox.shift() : null;
}

/** Unity polls: pointer/keyboard ownership bits (see HexHtmlUi.jslib). */
export function pointerBits() {
  return bits;
}

export function openScreens() {
  return screens;
}

export function subscribe(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** For the stand (Tools/webui_stand.py): what Unity would have taken. */
export function drainOutbox() {
  return outbox.splice(0);
}

document.addEventListener('pointermove', (e) => {
  lastPointer = { x: e.clientX, y: e.clientY };
  setBit(POINTER_OVER, isSurface(e.target));
}, true);
document.addEventListener('pointerdown', (e) => {
  lastPointer = { x: e.clientX, y: e.clientY };
  const surface = isSurface(e.target);
  setBit(POINTER_OVER, surface);
  setBit(PRESS_HELD, surface);
}, true);
const release = () => setBit(PRESS_HELD, false);
window.addEventListener('pointerup', release, true);
window.addEventListener('pointercancel', release, true);
window.addEventListener('blur', release);
document.addEventListener('focusin', (e) => setBit(TEXT_FOCUS, isTextField(e.target)));
document.addEventListener('focusout', () => setBit(TEXT_FOCUS, false));
