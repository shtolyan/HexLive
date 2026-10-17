// §174.4: every string of the page comes from the I2 terms (§58) that Unity
// sends once per language — the page has no string table of its own. Same
// contract as Loc.Get: a missing term renders as its key, so a gap is visible.
let terms = {};
let lang = 'en';

export function setStrings(language, table) {
  lang = language === 'ru' ? 'ru' : 'en';
  terms = table || {};
  document.documentElement.lang = lang;
}

export function language() {
  return lang;
}

/** t('menu.title') or t('craft.count', { n: 3 }) for «{n}» placeholders. */
export function t(key, vars) {
  const text = terms[key];
  if (!text) return key;
  return vars ? text.replace(/\{(\w+)\}/g, (all, name) => (name in vars ? String(vars[name]) : all)) : text;
}
