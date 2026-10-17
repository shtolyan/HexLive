// One import for screens: Preact with htm templates (no build step — the
// template folder is copied into the player as is, §174.6).
import { h, render, Fragment } from './vendor/preact.js';
import htm from './vendor/htm.js';

export { h, render, Fragment };
export { useState, useEffect, useRef, useMemo, useCallback } from './vendor/hooks.js';
export const html = htm.bind(h);
