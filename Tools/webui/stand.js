// The stand's stand-in for Unity: it speaks the same protocol as HtmlUi.cs
// (hello → ready → strings → show/hide, then polls commands every frame),
// so a window that works here differs from the game only in its data.
import * as bridge from './ui/bridge.js';

const state = {
  lang: 'ru',
  platform: 'web', // web = shared server world (remote), desktop = local game
  size: [960, 600],
  dialog: 'titleServers', // the window picked in the bar; null once it is closed
  volumes: { voices: 80, agentVoices: 100, music: 55, environment: 90 },
  updateFailed: false,
  hud: { day: 12, minutes: 14 * 60 + 20, raining: false, link: 'live', paused: false, speed: 1 },
};
const strings = {};
let ready = false;
let shownDialog = null;

// ---- the title screen (LoadingScreen.cs): servers, loading, failure ----
const SERVERS = [
  { id: 'singapore', nameTerm: 'menu.servers.singapore', name: 'Singapore', ping: -1, state: 'pending' },
  { id: 'new-york', nameTerm: 'menu.servers.newyork', name: 'New York', ping: -1, state: 'disabled' },
];
// What LoadingScreen reports on the server path, step by step (§174.7).
const LOAD_PLAN = [
  [0.01, 'connect', { text: 'Получаем реестр контента' }],
  [0.15, 'connect', { key: 'loading.connecting' }],
  [0.3, 'connect', { key: 'loading.connecting' }],
  [0.45, 'sync', { key: 'loading.syncing' }],
  [0.6, 'island', { key: 'loading.island' }],
  [0.75, 'people', { key: 'loading.warmup' }],
  [0.82, 'people', { key: 'loading.content.wear' }],
  [0.9, 'people', { key: 'loading.content.hair' }],
  [0.97, 'people', { key: 'loading.content.done' }],
  [1, 'done', { key: 'loading.done' }],
];
const title = { phase: 'servers', connecting: false, error: null, loading: null, timer: 0 };

// ---- character creation (PlayerCharacterCreationFlow.cs), real option lists ----
const TINTS = {
  black: [38, 35, 34], graphite: [75, 76, 79], espresso: [77, 48, 35], chestnut: [126, 78, 46],
  copper: [182, 88, 42], auburn: [132, 49, 37], honey: [211, 158, 73], gold: [240, 202, 111],
  ash: [170, 161, 142], platinum: [237, 225, 196], silver: [193, 199, 207], white: [250, 247, 234],
  rose: [217, 132, 161], violet: [139, 111, 190], ocean: [73, 130, 187], mint: [110, 181, 157],
}; // HairTintPalette.cs
const EYES = { default: '#89968a', blue: '#4478a0', blue_green: '#3a827a', green: '#5c7f3f', grey: '#6a7681',
  hazel: '#8a6a2c', amber: '#a86e18', brown: '#6d4522', dark_brown: '#452a12' }; // PeopleEyeColour.cs ramp[2]
const HAIR = ['none', 'AdellHair', 'AsukaHair', 'BendineHair', 'Bob3Hair', 'ChunkyHair', 'EilisHair', 'Hair07',
  'JelikaHair_32434', 'JenniferHair', 'LeonyPonytail', 'LoonaHair', 'LowPonytail', 'Neu09Hair', 'OnyxHair',
  'ShilohHair', 'TootsieRollHair'];
const SKINS = { female: ['Marta', 'Molly', 'Jana', 'Jolly', 'Masha', 'Rita'], male: ['Kshishtof', 'Tonny'] };
const SKIN_TONE = { Marta: '#e8c4a2', Molly: '#f1d0b8', Jana: '#c99a6e', Jolly: '#dcae84', Masha: '#e2b796', Rita: '#b9825a', Kshishtof: '#d6ad86', Tonny: '#9c6a45' };
const creatorOptions = (sex) => ({
  sexes: ['female', 'male'],
  skins: SKINS[sex],
  hair: HAIR.map((id) => ({ id, colours: id === 'none' ? ['prototype'] : ['prototype', ...Object.keys(TINTS).map((k) => `tint.${k}`)] })),
  eyes: ['', 'blue', 'blue_green', 'green', 'grey', 'hazel', 'amber', 'brown', 'dark_brown'],
  attributes: ['Strength', 'Agility', 'Endurance', 'Toughness', 'Hardiness', 'Wits', 'Perception'],
  skills: ['Combat', 'Harvesting', 'Crafting', 'Building', 'Cooking', 'Medicine', 'Survival', 'Social', 'Athletics'],
  traits: ['Abuser', 'Slob', 'Neat', 'Lazy', 'Diligent', 'Sleepyhead', 'Coward', 'Brave'],
  pairs: [['Slob', 'Neat'], ['Lazy', 'Diligent'], ['Coward', 'Brave']],
  attributeBudget: 42, skillBudget: 30, // 4.2 and 3.0 in server units, shown ×10
  // Head-counts as /character/options now returns them (residents of capacity).
  camps: [[1, 6], [2, 4], [3, 17], [4, 9], [5, 2], [6, 12]].map(([n, residents]) =>
    ({ id: n === 1 ? 'Colony' : `Colony${n}`, number: n, residents, capacity: 17, hasSpace: residents < 17 })),
});
const creator = {
  touched: false, preview: 'loading', previewRect: null, view: 'body', yaw: 0, thumbs: {}, banner: null, busy: null,
  draft: { name: '', sex: 'female', skin: 'Marta', hair: 'LeonyPonytail', hairColour: 'prototype', eyes: '', camp: 'Colony',
    attributes: { Strength: 5, Agility: 5, Endurance: 5, Toughness: 5, Hardiness: 5, Wits: 5, Perception: 5 },
    skills: { Combat: 0, Harvesting: 0, Crafting: 0, Building: 0, Cooking: 0, Medicine: 0, Survival: 0, Social: 0, Athletics: 0 },
    traits: [] },
};
const isCreator = (id) => !!id && id.startsWith('creator');
const isGame = (id) => !!id && id.startsWith('game');

// ---- in play: roster + character card (CharacterPanel), sample colony ----
const COLONY = [
  { id: 11, name: 'Марта', skin: 'Marta', health: 0.92, status: 'busy', tone: 'ok', goal: 'GatherWood' },
  { id: 12, name: 'Молли', skin: 'Molly', health: 0.64, status: 'moving', tone: 'ok', goal: 'GetWater' },
  { id: 13, name: 'Яна', skin: 'Jana', health: 0.31, status: 'resting', tone: 'warn', goal: 'TreatWounds' },
  { id: 14, name: 'Джолли', skin: 'Jolly', health: 0.85, status: 'sleeping', tone: 'idle', goal: 'Sleep' },
  { id: 15, name: 'Маша', skin: 'Masha', health: 0.7, status: 'busy', tone: 'ok', goal: 'Hunt', fighting: true },
  { id: 16, name: 'Рита', skin: 'Rita', health: 1, status: 'busy', tone: 'ok', goal: 'CookMeat', mine: true },
];
const OUTSIDERS = [{ id: 41, name: 'Кшиштоф', skin: 'Kshishtof', health: 0.55, status: 'moving', tone: 'warn', goal: 'Explore' }];
const game = { selected: 16, group: [], collapsed: false, control: 'manual', pace: 'walk', portraitRect: null };
const healthColor = (h) => (h >= 0.6 ? 'rgb(193 219 141)' : h >= 0.3 ? 'rgb(240 180 92)' : 'rgb(232 104 92)');

// ---- the backpack window: real icons from HexLiveContent/Icons ----
const ICONS = ['food.coconut', 'food.coconut_open', 'food.meat_cooked', 'tool.axe_stone', 'tool.knife', 'tool.sharp_stone',
  'tool.pickaxe_stone', 'resource.stone', 'resource.rope', 'resource.stick', 'resource.palm_leaf', 'resource.log',
  'resource.hide', 'resource.cloth', 'clothing.top_primal', 'underwear.briefs_primal', 'clothing.skirt_primal'];
const inv = {
  open: false, locked: false, layer: 'wear', dollRect: null, nextUid: 100,
  items: [
    ['food.coconut', 2], ['food.coconut_open', 1], ['tool.axe_stone', 1, { inUse: true, favorite: true }], ['tool.knife', 1],
    ['tool.sharp_stone', 1], ['resource.stone', 5], ['resource.rope', 2], ['resource.stick', 6], ['resource.palm_leaf', 3],
    ['resource.log', 1], ['resource.hide', 1], ['food.meat_cooked', 1], ['resource.cloth', 2],
    ['clothing.top_primal', 1, { worn: true }], ['underwear.briefs_primal', 1, { worn: true }], ['clothing.skirt_primal', 1],
  ].map(([def, count, extra], i) => ({ uid: i + 1, def, count, ...(extra || {}) })),
};
const tr = (key, vars) => {
  let text = strings[state.lang]?.[key] ?? key;
  if (vars) for (const [k, v] of Object.entries(vars)) text = text.replace(`{${k}}`, v);
  return text;
};
const itemName = (def) => tr(`item.${def.replace('.', '_')}.name`);
const isClothing = (def) => def.startsWith('clothing.') || def.startsWith('underwear.');

function itemDetail(it) {
  const kind = it.def.split('.')[0];
  const stats = kind === 'tool' ? [['inv.durability', '86%'], ['inv.damage', it.def === 'tool.axe_stone' ? '14' : '9'], ['inv.cadence', '1.4 с'], ['inv.grip', tr('inv.hands')]]
    : kind === 'food' ? [['inv.restores', '25%'], ['inv.hydrates', it.def === 'food.coconut_open' ? '30%' : '5%']]
    : isClothing(it.def) ? [['inv.warmth', '+2'], ['inv.armor', '4%'], ['inv.layer', tr(it.def.startsWith('underwear') ? 'layer.underwear' : 'layer.wear')], ['inv.clothing_hp', '92%']]
    : [['inv.stack', `${it.count} / 10`]];
  const actions = isClothing(it.def) ? [it.worn ? 'stow' : 'wear', 'drop'] : ['drop'];
  return { category: tr(`craft.category.${kind === 'tool' ? 'tools' : kind === 'food' ? 'food' : isClothing(it.def) ? 'clothing' : 'resources'}`),
    desc: strings[state.lang]?.[`item.${it.def.replace('.', '_')}.desc`] || '', stats, actions };
}

function inventoryModel() {
  const c = COLONY.find((x) => x.id === game.selected) || COLONY[0];
  return {
    name: c.name, readonly: false, outfitLocked: inv.locked, capacity: { used: inv.items.filter((i) => !i.worn).length, cap: 20 },
    slots: 20, dollLayer: inv.layer,
    items: inv.items.map((it) => ({ ...it, icon: `icon:${it.def}`, name: itemName(it.def), detail: itemDetail(it) })),
    craft: {
      blocked: game.control === 'manual' ? null : 'craft.block.NotManual',
      categories: ['tools', 'resources', 'food', 'clothing'],
      recipes: [
        { id: 'sharp', icon: 'icon:tool.sharp_stone', name: itemName('tool.sharp_stone'), category: 'tools' },
        { id: 'axe', icon: 'icon:tool.axe_stone', name: itemName('tool.axe_stone'), category: 'tools', working: true },
        { id: 'pick', icon: 'icon:tool.pickaxe_stone', name: itemName('tool.pickaxe_stone'), category: 'tools' },
        { id: 'rope', icon: 'icon:resource.rope', name: itemName('resource.rope'), category: 'resources' },
        { id: 'cloth', icon: 'icon:resource.cloth', name: itemName('resource.cloth'), category: 'resources' },
        { id: 'meat', icon: 'icon:food.meat_cooked', name: itemName('food.meat_cooked'), category: 'food' },
        { id: 'top', icon: 'icon:clothing.top_primal', name: itemName('clothing.top_primal'), category: 'clothing' },
      ],
      detail: {
        sharp: { ingredients: [{ icon: 'icon:resource.stone', name: itemName('resource.stone'), have: 5, need: 2 }], station: null, progress: 0, block: null, button: 'create' },
        axe: { ingredients: [{ icon: 'icon:resource.stick', name: itemName('resource.stick'), have: 6, need: 1 }, { icon: 'icon:tool.sharp_stone', name: itemName('tool.sharp_stone'), have: 1, need: 1 }, { icon: 'icon:resource.rope', name: itemName('resource.rope'), have: 2, need: 1 }], station: 'workbench', progress: 0.45, block: null, button: 'working' },
        pick: { ingredients: [{ icon: 'icon:resource.stick', name: itemName('resource.stick'), have: 6, need: 1 }, { icon: 'icon:resource.stone', name: itemName('resource.stone'), have: 5, need: 3 }, { icon: 'icon:resource.rope', name: itemName('resource.rope'), have: 2, need: 2 }], station: 'workbench', progress: 0, block: 'StationBusy', button: 'create' },
        rope: { ingredients: [{ icon: 'icon:resource.palm_leaf', name: itemName('resource.palm_leaf'), have: 3, need: 4 }], station: null, progress: 0, block: 'MissingResources', button: 'create' },
        cloth: { ingredients: [{ icon: 'icon:resource.palm_leaf', name: itemName('resource.palm_leaf'), have: 3, need: 2 }], station: 'workbench', progress: 0.2, block: null, button: 'continue' },
        meat: { ingredients: [{ icon: 'icon:food.meat_cooked', name: itemName('food.meat_cooked'), have: 1, need: 1 }], station: 'campfire', progress: 0, block: 'Unreachable', button: 'create' },
        top: { ingredients: [{ icon: 'icon:resource.hide', name: itemName('resource.hide'), have: 1, need: 2 }], station: 'workbench', progress: 0, block: 'MissingResources', button: 'create' },
      },
    },
  };
}

// §138.7 on the stand: a pretend workbench — each queued item takes 3 s.
const craftQueue = {}; // recipe → count (the item in the making included)
const craftProgress = {};
setInterval(() => {
  let changed = false;
  for (const [recipe, n] of Object.entries(craftQueue)) {
    if (n <= 0) continue;
    craftProgress[recipe] = (craftProgress[recipe] || 0) + 1 / 6;
    if (craftProgress[recipe] >= 1) { craftProgress[recipe] = 0; craftQueue[recipe] = n - 1; }
    changed = true;
    break; // one at a time, head first
  }
  if (changed && inv.open) sendInventory();
}, 500);

function sendInventory() {
  if (!ready) return;
  if (inv.open && isGame(state.dialog)) {
    const model = inventoryModel();
    for (const r of model.craft.recipes) {
      const d = model.craft.detail[r.id];
      r.queued = craftQueue[r.id] || 0;
      if (r.queued > 0) r.working = true;
      d.queued = r.queued;
      if (r.queued > 0) { d.progress = craftProgress[r.id] || 0; d.button = 'working'; }
      d.canQueue = !d.block || r.queued > 0;
    }
    post({ t: 'show', screen: 'inventory', model });
  }
  else post({ t: 'hide', screen: 'inventory' });
}

// ---- health (§57) and journal (§136) windows of the selected colonist ----
const side = { health: false, journal: false, healthRect: null, read: {} };
function healthModel() {
  const c = COLONY.find((x) => x.id === game.selected) || COLONY[0];
  const hurt = c.health < 0.5;
  const zone = (id, hp, extra) => ({ id, hp, critical: 0, severed: false, bleeding: false, bandaged: false, splint: 0,
    armor: 0, wounds: 0, cut: 0, blunt: 0, prosthetic: null, ...(extra || {}) });
  return {
    name: c.name, dollAvailable: true,
    zones: [
      zone('Head', 0.96, { armor: 0 }),
      zone('Torso', hurt ? 0.48 : 0.82, { bleeding: hurt, wounds: hurt ? 2 : 1, cut: hurt ? 0.31 : 0.08, blunt: 0.05, armor: 0.35 }),
      zone('Pelvis', 0.9, { armor: 0.2 }),
      zone('ArmL', hurt ? 0.34 : 0.74, { critical: hurt ? 0.14 : 0, bandaged: true, splint: hurt ? 0.4 : 0, wounds: 1, cut: 0.12, blunt: 0.22 }),
      hurt ? zone('ArmR', 0, { severed: true }) : zone('ArmR', 1),
      zone('LegL', 0, { severed: true, prosthetic: { name: tr('item.prosthetic_peg_leg.name') === 'item.prosthetic_peg_leg.name' ? 'Деревянная нога' : tr('item.prosthetic_peg_leg.name'), hp: 0.62, max: 0.8, function: 0.7 } }),
      zone('LegR', 0.71, { blunt: 0.29 }),
    ],
  };
}
const JOURNAL = [
  [4, '18:40', 'Поговорили с Мартой. Хорошо было просто поговорить.', false],
  [4, '15:10', 'Меня достали. Рука. Больно.', true],
  [4, '11:30', 'Молли мне помогла. Я этого не забуду.', false],
  [4, '07:00', 'Как всегда: рубила дрова, носила воду, грелась у костра.', false],
  [3, '21:20', 'Во рту песок. Я думаю о воде и больше ни о чём.', true],
  [3, '16:00', 'Развела костёр. Тепло.', false],
  [3, '09:40', 'Смастерила себе каменный нож.', false],
  [2, '20:10', 'Я отбилась. Руки до сих пор трясутся.', true],
  [2, '12:00', 'Маша дала мне поесть. Я умирала с голоду.', false],
];
function journalModel() {
  const c = COLONY.find((x) => x.id === game.selected) || COLONY[0];
  const read = side.read[c.id] ?? 2;
  const entries = c.id === 14 ? [] : JOURNAL.map(([day, clock, text, danger], i) =>
    ({ id: i, time: tr('history.time', { 0: day, 1: clock }), text, danger, unread: i < read }));
  return { name: c.name, unread: entries.filter((e) => e.unread).length, entries };
}
function sendSide() {
  if (!ready) return;
  const inGame = isGame(state.dialog) && state.dialog !== 'gameNone' && state.dialog !== 'gameGroup';
  if (side.health && inGame) post({ t: 'show', screen: 'health', model: healthModel() });
  else { post({ t: 'hide', screen: 'health' }); side.health = false; side.healthRect = null; }
  if (side.journal && inGame) post({ t: 'show', screen: 'journal', model: journalModel() });
  else { post({ t: 'hide', screen: 'journal' }); side.journal = false; }
}

// ---- the exchange window (LootTransferPanel): her things ⇄ another's ----
const LOOT_GROUPS = {
  person: [['carry', 'inv.carry', 2, true], ['bag', null, 6, false], ['holster', 'inv.holster', 3, false]],
  container: [['basket', null, 8, false]],
};
const loot = { open: false, mode: 'exchange', other: 'person', pending: false, status: null, uid: 500, sides: {} };
function lootReset() {
  const mk = (list) => list.map(([def, count, group, extra]) => ({ uid: `u${loot.uid++}`, def, count, group, ...(extra || {}) }));
  loot.sides.left = mk([['tool.knife', 1, 'carry', { hand: 'R' }], ['food.coconut', 2, 'carry'], ['resource.rope', 3, 'bag'],
    ['resource.stick', 6, 'bag'], ['food.meat_cooked', 1, 'bag'], ['tool.sharp_stone', 1, 'holster'],
    ['clothing.top_primal', 1, 'worn'], ['underwear.briefs_primal', 1, 'worn']]);
  loot.sides.right = loot.other === 'container'
    ? mk([['resource.palm_leaf', 7, 'basket'], ['food.coconut', 4, 'basket'], ['resource.stone', 5, 'basket'], ['resource.hide', 1, 'basket']])
    : mk([['tool.axe_stone', 1, 'carry', { hand: 'R' }], ['resource.log', 1, 'carry', { hand: 'L' }], ['food.coconut_open', 1, 'bag'],
      ['resource.cloth', 2, 'bag'], ['clothing.skirt_primal', 1, 'worn']]);
  loot.pending = false; loot.status = null;
}
function lootItem(it) {
  return { uid: it.uid, def: it.def, icon: `icon:${it.def}`, emoji: null, name: itemName(it.def), count: it.count };
}
function lootInfo() {
  const it = loot.info && [...loot.sides.left, ...loot.sides.right].find((i) => i.uid === loot.info);
  if (!it) return null;
  const d = itemDetail(it);
  return { uid: it.uid, detail: { category: d.category, desc: d.desc, stats: d.stats.map(([k, v]) => [tr(k), v]) } };
}
function lootPane(side) {
  const items = loot.sides[side];
  const person = side === 'left' || loot.other === 'person';
  const who = side === 'left' ? (COLONY.find((x) => x.id === game.selected) || COLONY[0]) : COLONY.find((x) => x.id === 15);
  const groups = LOOT_GROUPS[person ? 'person' : 'container'].map(([id, title, cap, hands]) => {
    const inside = items.filter((i) => i.group === id);
    const slots = [];
    if (hands) for (const h of ['L', 'R']) {
      const held = inside.find((i) => i.hand === h);
      slots.push({ item: held ? lootItem(held) : null, hand: h === 'L' ? 'left' : 'right' });
    }
    const loose = inside.filter((i) => !i.hand);
    for (let n = 0; n < cap; n++) slots.push({ item: loose[n] ? lootItem(loose[n]) : null });
    const bagName = state.lang === 'ru' ? (id === 'bag' ? 'Плетёная сумка' : 'Корзина') : (id === 'bag' ? 'Woven bag' : 'Basket');
    return { title: title ? tr(title) : bagName, filled: inside.length, cap: cap + (hands ? 2 : 0), overflow: false,
      owner: id === 'bag' ? { uid: `${side}-bag`, def: 'resource.cloth', icon: 'icon:resource.cloth', name: bagName, count: 1 } : null, slots };
  }).filter((g) => side === 'left' || loot.other === 'container' || g.filled > 0 || g.title === tr('inv.carry'));
  return {
    name: person ? who.name : (state.lang === 'ru' ? 'Корзина у костра' : 'Basket by the fire'),
    face: person ? `face:${who.id}` : null, kind: person ? 'person' : 'container',
    groups, worn: person ? items.filter((i) => i.group === 'worn').map(lootItem) : [],
  };
}
function sendLoot() {
  if (!ready) return;
  if (loot.open && isGame(state.dialog)) {
    post({ t: 'show', screen: 'loot', model: { mode: loot.mode, pending: loot.pending, status: loot.status, left: lootPane('left'), right: lootPane('right'), info: lootInfo() } });
  } else post({ t: 'hide', screen: 'loot' });
}
// C#: the order goes out, she walks over, the snapshot answers.
function lootMove(uid, to, count, wear) {
  const from = to === 'left' ? 'right' : 'left';
  const it = loot.sides[from].find((i) => i.uid === uid);
  if (!it) return;
  loot.pending = true; loot.status = { key: wear ? 'loot.equipping' : 'loot.transferring', tone: 'busy' };
  sendLoot();
  setTimeout(() => {
    const group = to === 'left' ? (wear ? 'worn' : 'bag') : (loot.other === 'container' ? 'basket' : 'bag');
    const moved = { ...it, uid: `u${loot.uid++}`, count, group, hand: undefined };
    it.count -= count;
    if (it.count <= 0 || it.group === 'worn') loot.sides[from] = loot.sides[from].filter((i) => i !== it);
    loot.sides[to].push(moved);
    loot.pending = false; loot.status = null;
    sendLoot();
  }, 900);
}

// ---- build mode (BuildModePanel): the tray at the bottom ----
const BUILD = {
  furniture: [
    ['comfort', [['furniture.bed_basic', 'bed', '▰']]],
    ['heating', [['furniture.hearth', 'hearth', '♨']]],
    ['storage', [['furniture.wardrobe', 'wardrobe', '╫']]],
    ['workstations', [['furniture.workbench', 'workbench', '⚒']]],
    ['outdoor', [['campfire.spot', 'campfire', '♨'], ['drying.rack', 'drying_rack', '╥'], ['water.collector', 'water_collector', '▽']]],
  ],
  construction: [['house', [['building.hut_plan', 'hut', '⌂'], ['custom.project', 'project', '✎'], ['free.architecture', 'free', '⌁']]]],
};
const build = { open: false, mode: 'furniture', category: 'comfort', active: '', status: 'build.mode.hint', arrows: null, ok: true, site: null };
function buildModel() {
  const cats = BUILD[build.mode];
  const cards = (cats.find(([id]) => id === build.category) || cats[0])[1];
  const card = cards.concat(...cats.map(([, c]) => c)).find(([id]) => id === build.active);
  const sel = card ? { name: tr(`blueprint.catalog.${card[1]}.name`), desc: tr(`blueprint.catalog.${card[1]}.description`), availability: build.ok ? 'ready' : 'blocked', canDelete: false, canEdit: false }
    : build.site ? { name: tr(`blueprint.catalog.${build.site}.name`), desc: tr(`blueprint.catalog.${build.site}.description`), availability: null, canDelete: true, canEdit: build.site === 'hut' }
    : { name: null, desc: null, availability: null, canDelete: false, canEdit: false };
  return {
    mode: build.mode,
    categories: cats.map(([id]) => ({ id, label: tr(`blueprint.category.${id}`) })),
    category: build.category,
    cards: cards.map(([id, term, glyph]) => ({ id, name: tr(`blueprint.catalog.${term}.name`), desc: tr(`blueprint.catalog.${term}.description`), icon: null, glyph })),
    active: build.active, selection: sel, status: build.status, arrows: build.arrows,
  };
}
function setBuild(open) {
  build.open = open;
  if (open) { Object.assign(build, { mode: 'furniture', category: 'comfort', active: '', status: 'build.mode.hint', arrows: null, site: null }); inv.open = false; sendInventory(); }
  sendBuild();
  sendHud();
}
function sendBuild() {
  if (!ready) return;
  if (build.open && isGame(state.dialog)) {
    // Sims-style: the tray is about the world; the colonist's card steps aside.
    post({ t: 'hide', screen: 'character' });
    post({ t: 'show', screen: 'build', model: buildModel() });
  } else {
    build.open = false;
    post({ t: 'hide', screen: 'build' });
    if (isGame(state.dialog)) post({ t: 'show', screen: 'character', model: characterModel() });
  }
}

// ---- the house constructor (HutLayoutDesigner): the same tray, an editor header ----
const BP_CONSTRUCTION = [
  ['floor', [['architecture.floor.board', 'floor', '⬡'], ['architecture.floor.open', 'floor_open', '⏹']]],
  ['walls', [['architecture.wall.wood', 'wall', '▥']]],
  ['openings', [['architecture.window.wood', 'window', '▣'], ['architecture.door.wood', 'door', '⌑']]],
  ['roof', [['architecture.support.wood', 'support', '┃'], ['architecture.roof.palm', 'roof', '⌃']]],
];
const bp = { open: false, kind: 'project', mode: 'construction', category: 'floor', active: '', select: false, undo: 0, redo: 0,
  picked: null, statusKey: 'blueprint.status.world_start', statusText: null, door: false };
function bpCats() { return bp.mode === 'construction' ? BP_CONSTRUCTION : BUILD.furniture; }
function blueprintModel() {
  const cats = bpCats();
  const cards = (cats.find(([id]) => id === bp.category) || cats[0])[1];
  const all = [].concat(...cats.map(([, c]) => c));
  const card = all.find(([id]) => id === bp.active);
  const picked = bp.picked && all.concat(...BUILD.furniture.map(([, c]) => c)).find(([id]) => id === bp.picked.id);
  const sel = card ? { name: tr(`blueprint.catalog.${card[1]}.name`), desc: tr(`blueprint.catalog.${card[1]}.description`), availability: null, canDelete: false, canFinish: true }
    : bp.picked?.room ? { name: `${tr('blueprint.room')} #1`, desc: tr('blueprint.indoor.incomplete'), availability: null, canDelete: true, canFinish: true }
    : picked ? { name: `${tr(`blueprint.catalog.${picked[1]}.name`)} · ${bp.picked.yaw}°`, desc: tr(`blueprint.catalog.${picked[1]}.description`),
      availability: bp.picked.furniture ? 'floor' : null, canDelete: true, canFinish: true }
    : { name: null, desc: null, availability: null, canDelete: false, canFinish: false };
  return {
    mode: bp.mode, tabs: bp.kind === 'free' ? ['construction'] : ['construction', 'furniture'],
    categories: cats.map(([id]) => ({ id, label: tr(`blueprint.category.${id}`) })), category: bp.category,
    cards: cards.map(([id, term, glyph]) => ({ id, name: tr(`blueprint.catalog.${term}.name`), desc: tr(`blueprint.catalog.${term}.description`), icon: null, glyph })),
    active: bp.active, selection: sel, status: bp.statusKey, statusText: bp.statusText,
    arrows: bp.picked && !bp.picked.room ? bp.picked.at : null,
    editor: { select: bp.select && !bp.active, canUndo: bp.undo > 0, canRedo: bp.redo > 0, build: bp.kind === 'project' ? 'build' : null },
  };
}
function openBlueprint(kind) {
  setTimeout(sendHud, 0);
  Object.assign(bp, { open: true, kind, mode: 'construction', category: 'floor', active: '', select: false, undo: 0, redo: 0, picked: null,
    statusKey: kind === 'free' ? 'blueprint.status.ready' : 'blueprint.status.world_start', statusText: null, door: false });
  sendBlueprint();
}
function sendBlueprint() {
  if (!ready) return;
  if (bp.open && isGame(state.dialog)) {
    post({ t: 'hide', screen: 'character' });
    post({ t: 'show', screen: 'blueprint', model: blueprintModel() });
  } else {
    bp.open = false;
    post({ t: 'hide', screen: 'blueprint' });
    if (isGame(state.dialog) && !build.open) post({ t: 'show', screen: 'character', model: characterModel() });
  }
}

// ---- the bug tracker (BugReportPanel → Flashback): quick form + manager ----
const BUGS = {
  quick: false, manager: false, tab: 'open', filter: null, card: null, nextId: 489,
  reports: [
    { id: 488, status: 'created', text: 'Первый клик по колонистке в мире не выделяет её, второй не включает слежение.', created: '18.10 14:02',
      context: 'seed=30430 tick=1840221 npc=16', comments: [], commits: [], agent: null, build: null },
    { id: 487, status: 'in_progress', text: 'Лицо в карточке персонажа мигает, пока окно открыто.', created: '18.10 13:58',
      context: 'seed=30430 tick=1839904 npc=13', agent: 'claude', comments: [{ author: 'claude', text: 'Взяла в работу: портрет пропускает кадры на экране.' }], commits: [], build: null },
    { id: 485, status: 'ready_for_test', text: 'Иконки в рюкзаке больше ячеек и вылезают за край.', created: '18.10 13:51',
      context: 'seed=30430 tick=1839517 npc=16', agent: 'claude', ready: '0.9.412', comments: [{ author: 'claude', text: 'Картинка ячейки теперь бокс внутри неё. Готово к проверке.' }],
      commits: ['5ba607f9f'], build: 'not_included' },
    { id: 479, status: 'rework', text: 'Сушилка не принимает мокрую одежду, если на ней уже висит одна вещь.', created: '16.10 21:14',
      context: 'seed=30430 tick=1712203 npc=11', agent: 'codex', comments: [{ author: 'codex', text: 'Исправил выбор вешалки.' }, { author: 'user', text: 'Всё ещё не вешает вторую вещь, проверила на юбке.' }],
      commits: ['a1f3c09d2'], build: 'included' },
    { id: 471, status: 'fixed', text: 'Крафт верёвки не идёт, если листья в соседнем мешке.', created: '15.10 10:40',
      context: 'seed=30430 tick=1611010', agent: 'claude', comments: [{ author: 'claude', text: 'Ингредиенты считаются из всех контейнеров.' }], commits: ['77c0e2b1a'], build: 'included' },
    { id: 452, status: 'fixed', archived: true, text: 'Волк проходит сквозь закрытую дверь хижины.', created: '13.10 18:22',
      context: 'seed=30430 tick=1488732', agent: 'codex', comments: [], commits: ['0be41f9aa'], build: 'included' },
  ],
};
const bugTab = (r) => (r.archived ? 'archive' : r.status === 'fixed' ? 'fixed' : 'open');
function bugManagerModel() {
  const list = BUGS.reports.filter((r) => bugTab(r) === BUGS.tab && (!BUGS.filter || r.status === BUGS.filter));
  const card = BUGS.reports.find((r) => r.id === BUGS.card);
  const counts = { open: 0, fixed: 0, archive: 0 };
  for (const r of BUGS.reports) counts[bugTab(r)] += 1;
  return {
    tab: BUGS.tab, filter: BUGS.filter, counts,
    reports: list.map((r) => ({ id: r.id, status: r.status, archived: !!r.archived, title: r.text, created: r.created, comments: r.comments.length,
      build: r.status === 'ready_for_test' || r.status === 'fixed' ? r.build : null })),
    card: card && {
      id: card.id, status: card.status, archived: !!card.archived, created: card.created,
      meta: [card.agent && tr('bugs.assigned_agent', { 0: card.agent }), tr('bugs.reported_version', { 0: '0.9.405' }), card.ready && tr('bugs.ready_version', { 0: card.ready })].filter(Boolean),
      build: card.status === 'ready_for_test' || card.status === 'fixed' ? card.build : null,
      text: card.text, context: card.context, commits: card.commits,
      comments: card.comments.map((c) => ({ author: c.author, text: c.text, editable: c.author === 'user' })),
      can: { fix: card.status === 'ready_for_test' && !card.archived, rework: card.status === 'ready_for_test' && !card.archived,
        archive: card.status === 'fixed' && !card.archived, restore: !!card.archived },
    },
  };
}
function sendBugs() {
  if (!ready) return;
  if (BUGS.manager) post({ t: 'show', screen: 'bugManager', model: bugManagerModel() });
  else post({ t: 'hide', screen: 'bugManager' });
  // The quick form opens on top of the manager («+ Новый отчёт»): hide and show raises it.
  post({ t: 'hide', screen: 'bugReport' });
  if (BUGS.quick) post({ t: 'show', screen: 'bugReport', model: { context: `seed=30430 tick=${1840000 + Math.round(state.hud.minutes)} npc=${game.selected}`, sending: false, error: null } });
}

// ---- the colony history (GameHistoryPanel): a drawer on the left ----
const HISTORY = { collapsed: true, shown: 10, entries: [
  ['4, 18:40', 'Рита хорошо поговорила с Мартой.', 'Общение стало лучше. Отношения улучшились.', 'social'],
  ['4, 18:05', 'Костер погас.', '', 'bad'],
  ['4, 17:30', 'Маша атакована собакой.', 'Нападающий: собака #3.', 'danger'],
  ['4, 16:10', 'Яна получила рану.', 'Причина: нападение собаки.', 'danger'],
  ['4, 15:50', 'Молли помогла Яне.', 'Помощь: лечение ран. Отношения улучшились.', 'good'],
  ['4, 14:20', 'Марта сделала сушилку.', '', 'build'],
  ['4, 12:00', 'Начался дождь.', '', 'neutral'],
  ['4, 10:45', 'Джолли разожгла костер.', '', 'good'],
  ['4, 09:15', 'Рита срубила дерево.', '', 'neutral'],
  ['4, 07:30', 'Маша поссорилась с Яной.', 'Общение случилось, но отношения ухудшились.', 'social'],
  ['3, 22:10', 'Хижина достроена.', '', 'build'],
  ['3, 20:40', 'На закате на лагерь напали собаки.', '', 'danger'],
  ['3, 19:00', 'Молли получила еду от Риты.', '', 'good'],
  ['3, 16:30', 'Мясо испортилось.', '', 'bad'],
] };
function historyModel() {
  return { collapsed: HISTORY.collapsed, more: HISTORY.shown < HISTORY.entries.length,
    entries: HISTORY.entries.slice(0, HISTORY.shown).map(([when, title, detail, tone], i) => {
      const [day, clock] = when.split(', ');
      return { id: i, time: tr('history.time', { 0: day, 1: clock }), title, detail, tone };
    }) };
}
function sendHistory() {
  if (!ready) return;
  if (isGame(state.dialog)) post({ t: 'show', screen: 'history', model: historyModel() });
  else post({ t: 'hide', screen: 'history' });
}

// ---- developer tools (DebugControlsPanel): a drawer under the HUD ----
const DEBUG = { open: false, on: { clothesHidden: false, fog: true, fogSelected: false, culling: true, trace: false, hexInspector: false }, shade: 0 };
const SHADES = ['Desaturate', 'Darken', 'Sepia'];
function sendDebug() {
  if (!ready) return;
  if (DEBUG.open && isGame(state.dialog)) {
    const who = COLONY.find((c) => c.id === game.selected);
    // C#: body edits only where the world runs in this process — a desktop world, not the web client's server.
    post({ t: 'show', screen: 'debug', model: { target: state.dialog === 'gameNone' ? null : who?.name ?? null, mutable: state.platform === 'desktop', on: DEBUG.on, shade: SHADES[DEBUG.shade],
      ready: BUGS.reports.filter((r) => r.status === 'ready_for_test' && !r.archived).length, canReport: true, canManage: true, console: state.platform === 'desktop' } });
  } else post({ t: 'hide', screen: 'debug' });
  sendInspector();
}

// ---- voice (§160/§161): the admin window, the recording card, the reply line ----
const VOICE = { open: false, access: 'approved', busy: false, confirm: null, recording: false, level: 0, subtitle: null };
let voiceTimer = 0, subtitleTimer = 0;
function subtitle(text, tone, seconds = 5) {
  VOICE.subtitle = { text, tone };
  clearTimeout(subtitleTimer);
  subtitleTimer = setTimeout(() => { VOICE.subtitle = null; sendVoice(); }, seconds * 1000);
  sendVoice();
}
function finishCapture() {
  VOICE.recording = false; sendHud();
  VOICE.busy = true; sendVoice();
  setTimeout(() => { VOICE.busy = false; subtitle(`${tr('admin.voice.title')}: ${state.lang === 'ru' ? 'Рита вылечена: раны закрыты, кровь 100%.' : 'Rita healed: wounds closed, blood 100%.'}`, 'success'); }, 1200);
}
function sendVoice() {
  if (!ready) return;
  const game = isGame(state.dialog);
  if (game && VOICE.open) post({ t: 'show', screen: 'adminVoice', model: {
    access: VOICE.access, expires: VOICE.access === 'approved' ? (state.lang === 'ru' ? 'до 25.10.2026 18:00' : 'until 2026-10-25 18:00') : null,
    showRequest: true, canRequest: true, busy: VOICE.busy, confirm: VOICE.confirm, mic: { recording: VOICE.recording } } });
  else post({ t: 'hide', screen: 'adminVoice' });
  if (game && VOICE.recording) post({ t: 'show', screen: 'voiceCapture', model: { title: `${tr('admin.voice.title')} · ${tr('agent.voice.listening')}`, level: VOICE.level } });
  else post({ t: 'hide', screen: 'voiceCapture' });
  if (game && VOICE.subtitle) post({ t: 'show', screen: 'voiceSubtitle', model: VOICE.subtitle });
  else post({ t: 'hide', screen: 'voiceSubtitle' });
  clearInterval(voiceTimer);
  if (game && VOICE.recording) voiceTimer = setInterval(() => {
    // A voice: the level breathes, as FmodMicrophoneCapture.Level does.
    VOICE.level = Math.max(0.05, Math.min(1, 0.45 + 0.4 * Math.sin(Date.now() / 160) * Math.sin(Date.now() / 470)));
    post({ t: 'show', screen: 'voiceCapture', model: { title: `${tr('admin.voice.title')} · ${tr('agent.voice.listening')}`, level: VOICE.level } });
  }, 70);
}

// ---- the hex inspector (HexInspectorPanel): DEBUG → «Инспектор гекса» ----
function sendInspector() {
  if (!ready) return;
  if (!DEBUG.on.hexInspector || !isGame(state.dialog)) { post({ t: 'hide', screen: 'inspector' }); return; }
  const ru = state.lang === 'ru';
  post({ t: 'show', screen: 'inspector', model: {
    q: 4, r: -2, empty: false,
    objects: [
      { emoji: '🔥', name: ru ? 'Костёр (стройка)' : 'Campfire (build site)', category: tr('inspect.object'),
        desc: ru ? 'Каменное кольцо под костёр. Девушки носят сюда брёвна и камни; когда всё доставлено, место становится костром.' : 'A stone ring for a fire. The girls carry logs and stones here; once all is delivered it becomes a campfire.',
        stats: [['id', '#418 / campfire.site', 'dim'], ['build_site', 'campfire / 3/5', 'warn'], ['logs', '1/2', 'warn'], ['stones', '2/3', 'warn'], ['owner', '#12', 'dim'], ['tags', 'build_site, fire', 'dim']] },
      { icon: 'icon:resource.log', name: itemName('resource.log'), category: tr('inspect.object'),
        desc: tr('inspect.no_desc'),
        stats: [['id', '#431 / resource.log', 'dim'], ['resource', '1', null], ['variant', 'palm', 'dim']] },
    ],
    people: [{ name: 'Рита', id: 12 }, { name: 'Яна', id: 9 }],
    hex: [['walkable', true, 'good'], ['blocked', false, 'good'], ['indoor', false, 'dim'], ['water', false, 'dim'], ['elevation', '2', null], ['coord', '4,-2', 'dim']],
    junctions: ['5121 / blocked:0 occupied:1 reserved:0', '5122 / blocked:1 occupied:0 reserved:0', '5130 / blocked:0 occupied:0 reserved:1 / climb'],
  } });
}

// ---- the HUD map (TacticalMapPanel): a generated island ----
const MAP = (() => {
  const R = 1.5, rows = [];
  const world = (q, r) => [R * Math.sqrt(3) * (q + r / 2), 1.5 * R * r];
  const noise = (q, r) => (Math.sin(q * 12.9898 + r * 78.233) * 43758.5453) % 1;
  for (let r = -10; r <= 10; r++) for (let q = -10; q <= 10; q++) {
    const d = (Math.abs(q) + Math.abs(r) + Math.abs(q + r)) / 2;
    const land = d + Math.abs(noise(q, r)) * 2.2 < 8.2;
    const [x, z] = world(q, r);
    rows.push({ q, r, x, z, d, water: !land, elev: land ? Math.max(0, Math.round(4 - d / 2 + Math.abs(noise(r, q)) * 1.5)) : 0 });
  }
  return { collapsed: false, cam: { x: 0, z: 0 }, rows, world };
})();
const hexDist = (a, b) => (Math.abs(a.q - b.q) + Math.abs(a.r - b.r) + Math.abs(a.q + a.r - b.q - b.r)) / 2;
const MAP_PEOPLE = [[11, 2, 1], [12, -2, 2], [13, 1, -2], [14, -1, 0], [15, 4, -1], [16, 0, 1], [41, -6, 5]];
function mapTerrain() {
  return JSON.stringify({ radius: 1.5, tiles: MAP.rows.map((t) => [+t.x.toFixed(2), +t.z.toFixed(2), t.water ? 1 : 0, t.elev]) });
}
function mapModel() {
  const at = (q, r) => { const [x, z] = MAP.world(q, r); return { x: +x.toFixed(2), z: +z.toFixed(2) }; };
  const people = MAP_PEOPLE.map(([id, q, r]) => ({ id, q, r, ...at(q, r) }));
  const fog = MAP.rows.map((t) => (people.slice(0, 6).some((p) => hexDist(p, t) <= 3) ? '2' : t.d <= 6 ? '1' : '0')).join('');
  const land = MAP.rows.filter((t) => !t.water);
  const pick = (n, salt) => land.filter((t, i) => (i * 7 + salt) % n === 0 && t.d > 1);
  const { x: cx, z: cz } = MAP.cam;
  return {
    collapsed: MAP.collapsed,
    terrain: 'terrain:stand-island',
    fog,
    markers: [{ kind: 'camp', ...at(0, 0), live: true, lit: true },
      ...pick(9, 3).map((t) => ({ kind: 'palm', ...at(t.q, t.r), live: t.d <= 6 })),
      ...pick(23, 5).map((t) => ({ kind: 'resource', ...at(t.q, t.r), live: t.d <= 4 })),
      { kind: 'item', ...at(3, -3), live: true, icon: 'icon:tool.knife' }],
    people: people.map((p) => ({ id: p.id, x: p.x, z: p.z, owned: p.id !== 41, hostile: p.id === 41, selected: p.id === game.selected,
      dead: false, controlled: p.id === 16, face: `face:${p.id}` })),
    strangers: [at(-7, 2)],
    mobs: [{ ...at(5, 2), kind: 'wolf' }, { ...at(-4, 7), kind: 'crab' }],
    footprint: [[cx - 7, cz + 5], [cx + 7, cz + 5], [cx + 5, cz - 4], [cx - 5, cz - 4]],
  };
}
let mapTerrainSent = false;
function sendMap() {
  if (!ready) return;
  if (isGame(state.dialog)) {
    // C#: HtmlUi.Asset once per island; the model then names it on every frame.
    if (!mapTerrainSent) { post({ t: 'asset', key: 'terrain:stand-island', url: mapTerrain() }); mapTerrainSent = true; }
    post({ t: 'show', screen: 'map', model: mapModel() });
  }
  else { post({ t: 'hide', screen: 'map' }); mapTerrainSent = false; }
}

// ---- an unfinished craft project (CraftProjectDetailsPanel) ----
const PROJECT = { open: false, done: 7 };
function projectModel() {
  const required = 12;
  return { name: itemName('resource.rope'), icon: 'icon:resource.rope', emoji: null, batch: 3,
    status: PROJECT.done >= required ? 'ready' : PROJECT.done % 2 ? 'active' : 'paused',
    worker: { name: 'Марта', current: PROJECT.done % 2 === 1 && PROJECT.done < required },
    progress: PROJECT.done / required, remaining: required - PROJECT.done, required };
}
function sendProject() {
  if (!ready) return;
  if (PROJECT.open && isGame(state.dialog)) post({ t: 'show', screen: 'craftProject', model: projectModel() });
  else post({ t: 'hide', screen: 'craftProject' });
}
// C# refreshes the card five times a second while work goes on.
setInterval(() => { if (PROJECT.open && PROJECT.done < 12) { PROJECT.done += 1; sendProject(); } }, 1500);

// ---- the end of the game (EndSummaryPanel): the raft is done ----
const END = { open: false };
function endModel() {
  const alive = COLONY.filter((c) => c.id !== 13 && c.id !== 14);
  return {
    survivors: alive.length, total: COLONY.length, fallen: 2, day: 23, time: '17:40', raft: { done: 6, target: 6 },
    avgHealth: alive.reduce((n, c) => n + c.health, 0) / alive.length,
    people: alive.map((c, i) => ({ id: c.id, name: c.name, face: `face:${c.id}`, health: c.health, blood: [0.9, 0.74, 0.88, 1][i % 4],
      stamina: [0.6, 0.45, 0.8, 0.7][i % 4], wounds: [0, 2, 1, 0][i % 4] })),
    dead: [
      { id: 13, name: 'Яна', face: 'face:13', day: 17, tile: '4,−2', cause: tr('end.cause_dog') },
      { id: 14, name: 'Джолли', face: 'face:14', day: 21, tile: '−1,3', cause: tr('end.cause_heat') },
    ],
  };
}
function sendEnd() {
  if (!ready) return;
  if (END.open && isGame(state.dialog)) post({ t: 'show', screen: 'endSummary', model: endModel() });
  else post({ t: 'hide', screen: 'endSummary' });
}

// ---- the action menu (ContextMenuPanel): a right click on the world ----
const ctx = { target: 'object', open: false, at: null, id: 0 };
const E = (key, extra) => ({ label: tr(key).replace(/\s*▸$/, ''), enabled: true, hint: null, danger: false, ...(extra || {}) });
const OFF = (key, hintKey, extra) => E(key, { enabled: false, hint: tr(hintKey), ...(extra || {}) });
function contextModel() {
  const other = COLONY.find((x) => x.id === 15);
  const me = COLONY.find((x) => x.id === game.selected) || COLONY[0];
  const menus = {
    object: { title: state.lang === 'ru' ? 'Костёр' : 'Campfire', entries: [
      E('interaction.Fuel.verb'),
      OFF('menu.cook_meat', 'menu.cook_meat.no_meat'),
      E('interaction.take.from.spit.verb'),
      OFF('interaction.Harvest.verb', 'menu.missing_tool'),
      E('menu.steal', { danger: true }),
      E('interaction.Observe.verb'),
    ] },
    person: { title: other.name, entries: [
      E('menu.talk_to'),
      E('menu.gift_person'),
      E('menu.aid', { children: ['menu.aid.feed', 'menu.aid.hydrate', 'menu.aid.treat', 'menu.aid.console'].map((k) => E(k))
        .concat([OFF('menu.aid.medicate', 'menu.needs_two_hands')]) }),
      E('menu.directive', { children: ['menu.directive.firewood', 'menu.directive.stock_water', 'menu.directive.stock_food', 'menu.directive.build'].map((k) => E(k)) }),
      OFF('menu.camp_merge.invite', 'menu.camp_merge.need_relation'),
      E('menu.attack', { danger: true }),
      E('menu.dark.prey', { danger: true }),
      E('menu.select'),
    ] },
    self: { title: me.name, entries: [
      ...['menu.self.call_help', 'menu.self.treat', 'menu.self.sit', 'menu.self.sleep', 'menu.self.bathe', 'menu.self.wash',
        'menu.self.eat', 'menu.self.drink', 'menu.self.go_home'].map((k) => E(k)),
      E('menu.shout', { children: ['menu.shout.firewood', 'menu.shout.stock_water', 'menu.shout.stock_food', 'menu.shout.build'].map((k) => E(k)) }),
      E('menu.stop'),
    ] },
  };
  return { id: ctx.id, at: ctx.at, ...menus[ctx.target] };
}
function sendContext() {
  if (!ready) return;
  if (ctx.open && isGame(state.dialog)) { ctx.id += 1; post({ t: 'show', screen: 'contextMenu', model: contextModel() }); }
  else { ctx.open = false; post({ t: 'hide', screen: 'contextMenu' }); }
}

function sendFaces() {
  for (const def of ICONS) post({ t: 'asset', key: `icon:${def}`, url: `assets/icons/${def}.png` });
  for (const c of [...COLONY, ...OUTSIDERS]) post({ t: 'asset', key: `face:${c.id}`, url: thumb(c.skin, c.id % 2 ? 'LeonyPonytail' : 'Bob3Hair', 'tint.chestnut') });
}

const card = (c) => ({ id: c.id, name: c.name, face: `face:${c.id}`, health: c.health, healthColor: healthColor(c.health),
  status: c.status, statusTone: c.tone, fighting: !!c.fighting, mine: !!c.mine,
  selected: game.group.length ? game.group.includes(c.id) : game.selected === c.id });

function rosterModel() {
  return { clan: { count: COLONY.length, total: COLONY.length + 1 }, cards: COLONY.map(card), outsiders: OUTSIDERS.map(card) };
}

function npcModel(c) {
  return {
    id: c.id, name: c.name, control: game.control, controlLocked: null, canCall: true, call: null,
    pace: game.pace, canStop: game.control === 'manual', health: c.health, healthColor: healthColor(c.health),
    status: c.status, statusTone: c.tone, uv: 'mid', shade: false, badge: c.fighting ? 'fighting' : null,
    journalUnread: 2, goal: c.goal, dream: 'OwnBed', directive: { kind: 'Firewood', from: null },
    effects: [{ slug: 'wellfed', emoji: '😋', buff: true }, { slug: 'bandaged', emoji: '🩹', buff: true },
      { slug: 'strongsun', emoji: '🌞', buff: false }, { slug: 'soaked', emoji: '💦', buff: false }],
    needs: [
      { id: 'hunger', value: 0.78, impacts: [{ emoji: '😋', title: 'effect.wellfed.title', up: true }] },
      { id: 'thirst', value: 0.42, impacts: [{ emoji: '🌞', title: 'effect.strongsun.title', up: false }] }, { id: 'energy', value: 0.66 }, { id: 'comfort', value: 0.55 },
      { id: 'social', value: 0.24 }, { id: 'thermal', value: 0, thermal: 0.35, impacts: [{ emoji: '🌞', title: 'effect.strongsun.title', up: true }, { emoji: '💦', title: 'effect.soaked.title', up: false }] }, { id: 'stamina', value: 0.9 },
      { id: 'blood', value: 0.82, deficit: 0.12 }, { id: 'hygiene', value: 0.47 }, { id: 'stress', value: 0.71 },
      { id: 'compassion', value: 0.6 }, { id: 'breath', value: 1 },
    ],
    nature: [['strength', 6], ['agility', 7], ['endurance', 5], ['toughness', 4], ['hardiness', 6], ['wits', 8], ['perception', 7]]
      .map(([id, value]) => ({ id, value })),
    skills: [['combat', 2], ['harvesting', 5], ['crafting', 3], ['building', 1], ['cooking', 6], ['medicine', 0], ['survival', 4], ['social', 2], ['athletics', 3]]
      .map(([id, value]) => ({ id, value })),
    traits: ['trait.neat', 'trait.brave'],
    relations: COLONY.filter((x) => x.id !== c.id).slice(0, 5).map((x, i) => ({ id: x.id, name: x.name, face: `face:${x.id}`,
      tag: ['friend', 'close', 'acquaint', 'tense', 'neutral'][i], affinity: [0.7, 0.85, 0.2, -0.4, 0][i],
      familiarity: [0.8, 0.95, 0.4, 0.6, 0.2][i], trust: [0.6, 0.9, 0.3, 0.15, 0.25][i] })),
  };
}

function characterModel() {
  if (state.dialog === 'gameNone') return { mode: 'none' };
  if (state.dialog === 'gameGroup') {
    const ids = game.group.length ? game.group : [11, 12, 15];
    return { mode: 'group', group: { count: ids.length, manual: 2, total: ids.length, blocked: 0, fighting: 1, mode: 'mixed',
      faces: ids.map((id) => { const c = COLONY.find((x) => x.id === id); return { id, name: c.name, face: `face:${id}` }; }), feedback: null } };
  }
  const c = COLONY.find((x) => x.id === game.selected) || COLONY[0];
  return { mode: 'single', collapsed: game.collapsed, npc: npcModel(c) };
}

function sendGame() {
  if (!ready || !isGame(state.dialog)) return;
  post({ t: 'show', screen: 'roster', model: rosterModel() });
  if (!build.open && !bp.open) post({ t: 'show', screen: 'character', model: characterModel() });
  if (side.health || side.journal) sendSide();
  if (typeof MAP !== 'undefined') sendMap(); // the selected face grows on the map
}

// Stand-only stand-ins for the thumbnails Unity renders from the real model.
function thumb(skin, hairId, colour) {
  const c = document.createElement('canvas');
  c.width = c.height = 96;
  const g = c.getContext('2d');
  g.fillStyle = '#121a1e'; g.fillRect(0, 0, 96, 96);
  const hairRgb = colour && TINTS[colour.replace('tint.', '')] ? `rgb(${TINTS[colour.replace('tint.', '')].join(' ')})` : '#5a3a26';
  const i = Math.max(0, HAIR.indexOf(hairId));
  if (hairId !== 'none') { g.fillStyle = hairRgb; g.beginPath(); g.ellipse(48, 46 + (i % 3) * 4, 30, 34 + (i % 4) * 5, 0, 0, Math.PI * 2); g.fill(); }
  g.fillStyle = SKIN_TONE[skin] || '#e0b896'; g.beginPath(); g.ellipse(48, 50, 21, 26, 0, 0, Math.PI * 2); g.fill();
  g.fillRect(40, 70, 16, 18);
  if (hairId !== 'none') { g.fillStyle = hairRgb; g.beginPath(); g.ellipse(48, 30 + (i % 2) * 3, 23, 12 + (i % 5), 0, Math.PI, Math.PI * 2); g.fill(); }
  g.fillStyle = '#2b2b2b'; g.fillRect(39, 48, 5, 3); g.fillRect(52, 48, 5, 3);
  return c.toDataURL('image/png');
}

function renderThumbs() {
  // Arrive one by one, the way the Unity renderer delivers them.
  const d = creator.draft;
  const jobs = [...SKINS[d.sex].map((id) => [`skin:${id}`, () => thumb(id, 'none')]),
    ...HAIR.map((id) => [`hair:${id}`, () => thumb(d.skin, id, 'prototype')])].filter(([k]) => !creator.thumbs[k]);
  let i = 0;
  // Like HtmlUi.Asset: the picture travels once under a key; the model only names the key.
  const tick = () => {
    if (i >= jobs.length) return;
    const [k, f] = jobs[i++];
    const key = `${k}|${d.skin}`;
    post({ t: 'asset', key, url: f() });
    creator.thumbs[k] = key;
    sendCreator();
    setTimeout(tick, 60);
  };
  tick();
}

function creatorModel() {
  const d = creator.draft;
  const sum = (o) => Object.values(o).reduce((a, b) => a + b, 0);
  const o = creatorOptions(d.sex);
  const nameKey = !d.name.trim() ? 'creator.error.nameRequired' : d.name.trim().length > 64 ? 'creator.error.nameTooLong' : null;
  const budgetKey = sum(d.attributes) > o.attributeBudget ? 'creator.error.attributeBudget' : sum(d.skills) > o.skillBudget ? 'creator.error.skillBudget' : null;
  const campKey = budgetKey || (creator.preview !== 'ready' ? 'creator.previewLoading' : null);
  const tints = { prototype: 'conic-gradient(#5a3a26, #b0763f, #3b2618, #5a3a26)' };
  for (const [k, rgb] of Object.entries(TINTS)) tints[`tint.${k}`] = `rgb(${rgb.join(' ')})`;
  // C# words these through Loc.Get; the stand through the same I2 table.
  const tr = (key) => (key ? strings[state.lang]?.[key] ?? key : null);
  return {
    phase: state.dialog === 'creatorSignedOut' ? 'signedOut' : 'editing',
    bannerText: tr(creator.banner), busyText: tr(creator.busy), pending: false, draft: d, options: o,
    thumbs: creator.thumbs, tints, eyeSwatches: EYES, preview: creator.preview,
    nameErrorText: creator.touched ? tr(nameKey) : null,
    blocked: { appearance: tr(nameKey), abilities: tr(nameKey || budgetKey), camp: tr(nameKey || campKey) },
  };
}

function sendCreator() {
  if (ready && isCreator(state.dialog)) post({ t: 'show', screen: 'creator', model: creatorModel() });
}

function reloadPreview() {
  creator.preview = 'loading';
  sendCreator();
  drawWorld();
  clearTimeout(creator.previewTimer);
  creator.previewTimer = setTimeout(() => { creator.preview = 'ready'; sendCreator(); drawWorld(); }, 900);
}

function startCreator() {
  creator.banner = state.dialog === 'creatorSignedOut' ? null : creator.banner;
  sendCreator();
  if (state.dialog === 'creator') { renderThumbs(); reloadPreview(); }
}

function titleModel() {
  return { phase: title.phase, servers: SERVERS, connecting: title.connecting, error: title.error, loading: title.loading };
}

function sendTitle() {
  if (ready && isTitle(state.dialog)) post({ t: 'show', screen: 'title', model: titleModel() });
}

// The ping is re-measured on its own every 10 s while the card is up; only
// a server never measured shows «measuring», later rounds keep the last value.
function probe() {
  for (const server of SERVERS) if (server.state !== 'disabled' && server.ping < 0) server.state = 'pending';
  sendTitle();
  setTimeout(() => {
    const sg = SERVERS[0];
    sg.state = 'ready';
    sg.ping = 180 + Math.round(Math.random() * 40); // Singapore from Europe: yellow
    sendTitle();
  }, 1400);
}
setInterval(() => { if (isTitle(state.dialog) && title.phase === 'servers' && !title.connecting) probe(); }, 10000);

function startTitle(kind) {
  clearInterval(title.timer);
  title.connecting = false;
  title.loading = null;
  title.error = kind === 'titleError' ? { key: 'loading.connect.failed', detail: 'wss://play.62-146-235-120.sslip.io/watch: handshake timed out after 15 s' } : null;
  title.phase = kind === 'titleLoading' ? 'loading' : 'servers';
  if (kind === 'titleLoading') runLoading(); else probe();
}

function runLoading() {
  title.phase = 'loading';
  let i = 0;
  const step = () => {
    const [progress, stepId, status] = LOAD_PLAN[i];
    title.loading = { progress, step: stepId, status };
    sendTitle();
    if (++i >= LOAD_PLAN.length) { clearInterval(title.timer); setTimeout(() => startTitle('titleLoading'), 2500); }
  };
  step();
  title.timer = setInterval(step, 1100);
}

const $ = (id) => document.getElementById(id);
const log = $('log');

function post(message) {
  window.HexUi.receive(JSON.stringify(message));
}

async function loadStrings(lang) {
  if (!strings[lang]) {
    const response = await fetch(`i18n/${lang}.json`);
    strings[lang] = response.ok ? await response.json() : {};
  }
  return strings[lang];
}

async function sendStrings() {
  post({ t: 'strings', lang: state.lang, terms: await loadStrings(state.lang) });
}

// ---- test data, shaped exactly like the C# models in §174.7 ----

const MOLLY = {
  name: 'Молли',
  attributes: [['strength', 4], ['agility', 7], ['endurance', 6], ['hardiness', 5], ['toughness', 3], ['wits', 8], ['perception', 6]],
  traits: ['trait.neat', 'trait.brave'],
};
const MARTA = {
  name: 'Марта',
  attributes: [['strength', 8], ['agility', 5], ['endurance', 7], ['hardiness', 7], ['toughness', 6], ['wits', 3], ['perception', 5]],
  traits: [],
};

function character(person, intro) {
  return { name: person.name, intro, attributes: person.attributes.map(([id, value]) => ({ id, value })), traits: person.traits };
}

const DIALOGS = {
  gameMenu: () => ({ canQuit: state.platform === 'desktop', volumes: { ...state.volumes } }),
  connectionLost: () => ({ detail: 'socket closed 1006 (no close frame) after 6 reconnect attempts · last frame tick 682913' }),
  protocolUpdate: () => ({ canQuit: state.platform === 'desktop', failed: state.updateFailed }),
  playerAssignment: () => ({ losses: [], characters: [character(MOLLY, 'introduction')], footer: 'persistence' }),
  playerReplacement: () => ({
    losses: [{ kind: 'died', name: 'Джолли' }],
    characters: [character(MARTA, 'replacement')],
    footer: 'persistence',
  }),
};
// The replacement scenario is the same C# window with other data.
const SCREEN_OF = { playerReplacement: 'playerAssignment' };
const isTitle = (id) => !!id && id.startsWith('title');
const isFull = (id) => isTitle(id) || isCreator(id); // screens that own the whole display
const GAME_SCREENS = ['roster', 'character'];

function hudModel() {
  const h = state.hud;
  const hh = Math.floor(h.minutes / 60) % 24;
  const phase = hh < 6 ? 'Night' : hh < 11 ? 'Morning' : hh < 18 ? 'Day' : hh < 22 ? 'Evening' : 'Night';
  const temperature = h.raining ? 22 : 28;
  const remote = state.platform === 'web';
  return {
    day: h.day,
    temperature,
    band: temperature < 22 ? 'cold' : temperature > 30 ? 'hot' : 'mild',
    raining: h.raining,
    clock: `${String(hh).padStart(2, '0')}:${String(h.minutes % 60).padStart(2, '0')}`,
    phase,
    link: remote ? { state: h.link, ping: 84 + Math.round(Math.random() * 12) } : null,
    paused: remote ? false : h.paused,
    speed: remote ? 1 : h.speed,
    remote,
    build: isGame(state.dialog) && !bp.open ? { open: build.open } : null, // CanOpen is false while the constructor is open
    bugs: isGame(state.dialog) ? { ready: BUGS.reports.filter((r) => r.status === 'ready_for_test' && !r.archived).length, canReport: true } : null,
    debug: isGame(state.dialog) ? { open: DEBUG.open } : null,
    admin: isGame(state.dialog) ? { recording: VOICE.recording } : null,
  };
}

function sendHud() {
  // SimSpeedBar: the HUD is withdrawn while the loading screen owns the display.
  if (!ready) return;
  if (isFull(state.dialog)) post({ t: 'hide', screen: 'hud' });
  else post({ t: 'show', screen: 'hud', model: hudModel() });
  sendCorner();
}

// ---- the top-right corner (CharacterPanel): language, FPS/RAM, balance ----
let cornerTimer = 0;
function cornerModel() {
  const fps = 52 + Math.round(Math.random() * 9);
  return {
    lang: state.lang,
    stats: { version: '0.9.418', fps, ms: (1000 / fps).toFixed(1), ram: 404 + Math.round(Math.random() * 12), gc: 88 + Math.round(Math.random() * 6) },
    // Only a browser client with an account has a balance (WebVoiceAgent + control token).
    balance: state.platform === 'desktop' ? null
      : { amount: '12.40', spent: '1.60', cents: '160.00', approximate: true, pending: 0, estimated: true, opening: '14.00', confirmed: '1.52', estimate: '0.08' },
  };
}
function sendCorner() {
  if (!ready) return;
  clearInterval(cornerTimer);
  if (isFull(state.dialog)) { post({ t: 'hide', screen: 'corner' }); return; }
  post({ t: 'show', screen: 'corner', model: cornerModel() });
  cornerTimer = setInterval(() => post({ t: 'show', screen: 'corner', model: cornerModel() }), 500);
}

function showDialog(id) {
  if (isGame(shownDialog) && !isGame(id) && ready) { for (const screen of GAME_SCREENS) post({ t: 'hide', screen }); inv.open = false; post({ t: 'hide', screen: 'inventory' }); side.health = side.journal = false; side.healthRect = null; post({ t: 'hide', screen: 'health' }); post({ t: 'hide', screen: 'journal' }); ctx.open = false; post({ t: 'hide', screen: 'contextMenu' }); loot.open = false; post({ t: 'hide', screen: 'loot' }); build.open = false; post({ t: 'hide', screen: 'build' }); bp.open = false; post({ t: 'hide', screen: 'blueprint' }); BUGS.quick = BUGS.manager = false; post({ t: 'hide', screen: 'bugReport' }); post({ t: 'hide', screen: 'bugManager' }); post({ t: 'hide', screen: 'history' }); END.open = false; post({ t: 'hide', screen: 'endSummary' }); PROJECT.open = false; post({ t: 'hide', screen: 'craftProject' }); post({ t: 'hide', screen: 'map' }); mapTerrainSent = false; DEBUG.open = false; post({ t: 'hide', screen: 'debug' }); post({ t: 'hide', screen: 'inspector' }); VOICE.open = VOICE.recording = false; VOICE.subtitle = null; sendVoice(); }
  if (shownDialog && !isGame(shownDialog) && ready) post({ t: 'hide', screen: isTitle(shownDialog) ? 'title' : isCreator(shownDialog) ? 'creator' : SCREEN_OF[shownDialog] || shownDialog });
  if (isGame(id)) {
    shownDialog = id;
    state.dialog = id;
    for (const button of document.querySelectorAll('[data-dialog]')) button.setAttribute('aria-pressed', String(button.dataset.dialog === id));
    $('reopen').hidden = true;
    if (id === 'gameGroup') game.group = [11, 12, 15]; else game.group = [];
    drawWorld();
    sendHud();
    inv.open = id === 'gameInventory';
    side.health = id === 'gameHealth';
    side.journal = id === 'gameJournal';
    if (side.health) game.selected = 13; // the most hurt one shows every kind of row
    ctx.open = id === 'gameContext';
    DEBUG.open = id === 'gameDebug';
    DEBUG.on.hexInspector = id === 'gameInspector';
    VOICE.open = id === 'gameAdmin'; VOICE.recording = id === 'gameVoice'; VOICE.busy = false; VOICE.confirm = null; VOICE.access = id === 'gameAdmin' ? 'unrequested' : 'approved';
    VOICE.subtitle = id === 'gameVoice' ? { text: state.lang === 'ru' ? 'Рита: Слышу тебя. Скажи, что нужно, и я пойду.' : 'Rita: I hear you. Tell me what you need and I will go.', tone: null } : null;
    HISTORY.collapsed = id !== 'gameHistory'; HISTORY.shown = 10;
    END.open = id === 'gameEnd';
    PROJECT.open = id === 'gameProject2'; PROJECT.done = 7;
    BUGS.quick = id === 'gameBugQuick'; BUGS.manager = id === 'gameBugs'; if (BUGS.manager) { BUGS.tab = 'open'; BUGS.card = 485; }
    bp.open = id === 'gameProject' || id === 'gameFree';
    if (bp.open) { openBlueprint(id === 'gameFree' ? 'free' : 'project'); bp.undo = 3; bp.door = false; bp.active = 'architecture.wall.wood'; bp.category = 'walls'; }
    build.open = id === 'gameBuild';
    if (build.open) Object.assign(build, { mode: 'furniture', category: 'outdoor', active: 'campfire.spot', status: 'build.mode.hint', arrows: { x: 0.46, y: 0.42 }, ok: true, site: null });
    loot.open = ['gameLoot', 'gameGift', 'gameBasket'].includes(id);
    if (loot.open) { loot.mode = id === 'gameGift' ? 'gift' : 'exchange'; loot.other = id === 'gameBasket' ? 'container' : 'person'; lootReset(); }
    if (ctx.open) ctx.at = { x: 0.52, y: 0.42 };
    if (ready) { sendFaces(); sendGame(); sendInventory(); sendSide(); sendContext(); sendLoot(); if (build.open) sendBuild(); if (bp.open) sendBlueprint(); sendBugs(); sendHistory(); sendEnd(); sendProject(); sendMap(); sendDebug(); sendVoice(); }
    return;
  }
  if (!isTitle(id)) clearInterval(title.timer);
  shownDialog = id;
  state.dialog = id;
  drawWorld();
  sendHud();
  if (isCreator(id)) {
    for (const button of document.querySelectorAll('[data-dialog]')) button.setAttribute('aria-pressed', String(button.dataset.dialog === id));
    $('reopen').hidden = true;
    if (ready) startCreator();
    return;
  }
  if (isTitle(id)) {
    for (const button of document.querySelectorAll('[data-dialog]')) button.setAttribute('aria-pressed', String(button.dataset.dialog === id));
    $('reopen').hidden = true;
    if (ready) startTitle(id);
    return;
  }
  $('reopen').hidden = !!id || !ready;
  for (const button of document.querySelectorAll('[data-dialog]')) button.setAttribute('aria-pressed', String(button.dataset.dialog === id));
  if (id && ready) post({ t: 'show', screen: SCREEN_OF[id] || id, model: DIALOGS[id]() });
}

function refreshDialog() {
  if (isTitle(shownDialog)) { sendTitle(); return; }
  if (isCreator(shownDialog)) { sendCreator(); return; }
  if (shownDialog && ready) post({ t: 'show', screen: SCREEN_OF[shownDialog] || shownDialog, model: DIALOGS[shownDialog]() });
}

function note(from, text) {
  const item = document.createElement('li');
  const who = document.createElement('span');
  who.className = 'from';
  who.textContent = from + ' ';
  item.append(who, text);
  log.prepend(item);
  while (log.children.length > 60) log.lastChild.remove();
  return item;
}

// What the C# side does with each command, in words.
const EFFECT = {
  'hud.bug': 'открыть быстрое окно отчёта об ошибке',
  'hud.debug': 'открыть / закрыть DEBUG',
  'hud.build': 'открыть / закрыть режим стройки (клавиша B)',
  'gameMenu.continue': 'закрыть меню, мир продолжает идти',
  'gameMenu.mainMenu': 'сохранить игру и вернуться в главное меню',
  'gameMenu.quit': 'выйти из игры (только десктоп)',
  'connectionLost.ok': 'вернуться в главное меню',
  'playerAssignment.ok': 'отметить уведомления прочитанными и выбрать персонажа',
  'hud.pause': 'пауза / пустить время',
  'hud.speed': 'сменить скорость и снять паузу',
};

function handle(message) {
  if (message.t === 'ready') {
    ready = true;
    note('страница', `ready v${message.v}`);
    sendStrings().then(() => { sendHud(); showDialog(state.dialog); });
    return;
  }
  if (message.t !== 'cmd') return;
  const key = `${message.screen}.${message.cmd}`;
  const args = message.args && Object.keys(message.args).length ? JSON.stringify(message.args) : '';
  if (key === 'gameMenu.volume') {
    state.volumes[message.args.category] = message.args.value;
    const last = log.firstElementChild; // a drag is one line per slider
    if (last && last.dataset.volume === message.args.category) last.remove();
    note(key, args).dataset.volume = message.args.category;
    return;
  }
  if (message.screen === 'inventory') {
    const a = message.args || {};
    if (message.cmd === 'dollRect') { inv.dollRect = a; drawWorld(); return; }
    if (message.cmd === 'dollRotate') return;
    note(key, `${args} → ${{ close: 'закрыть окно', action: 'надеть / снять / выбросить вещь', outfitLock: 'закрепить одежду',
      dollLayer: 'показать слой одежды на кукле', craft: 'ещё одну в очередь крафта', craftCancel: 'очистить очередь (текущую доделает)' }[message.cmd] || '?'}`);
    if (message.cmd === 'close') { inv.open = false; inv.dollRect = null; drawWorld(); }
    if (message.cmd === 'outfitLock') inv.locked = !!a.value;
    if (message.cmd === 'dollLayer') inv.layer = a.layer;
    if (message.cmd === 'craft') craftQueue[a.recipe] = (craftQueue[a.recipe] || 0) + 1;
    if (message.cmd === 'craftCancel') craftQueue[a.recipe] = Math.min(craftQueue[a.recipe] || 0, 1);
    if (message.cmd === 'action') {
      const it = inv.items.find((x) => x.uid === a.uid);
      if (it && a.kind === 'wear') it.worn = true;
      if (it && a.kind === 'stow') it.worn = false;
      if (it && a.kind === 'drop') { it.count -= a.count || 1; if (it.count <= 0) inv.items = inv.items.filter((x) => x !== it); }
    }
    sendInventory();
    return;
  }
  if (message.screen === 'corner') {
    // Loc.Toggle: the whole page switches language, as the stand's own RU/EN does.
    note(key, `${args} → Loc.Toggle`);
    document.getElementById(`lang-${message.args?.code}`)?.click();
    return;
  }
  if (message.screen === 'adminVoice') {
    const a = message.args || {};
    if (message.cmd === 'close') { VOICE.open = false; note(key, '→ закрыть'); }
    else if (message.cmd === 'request') {
      VOICE.access = 'pending'; note(key, '→ request_access');
      // The server owner approves it; AdminVoicePanel then closes the window and says so.
      setTimeout(() => { VOICE.access = 'approved'; subtitle(`${tr('admin.voice.title')}: ${tr('admin.voice.access.approved')}`, 'success'); }, 1800);
    }
    else if (message.cmd === 'mic') { if (VOICE.recording) finishCapture(); else { VOICE.recording = true; VOICE.open = false; sendHud(); } note(key, '→ микрофон'); }
    else if (message.cmd === 'send') {
      note(key, `→ text: ${a.text}`);
      VOICE.busy = true; sendVoice();
      setTimeout(() => {
        VOICE.busy = false;
        VOICE.confirm = /удал|delete|снеси|demolish/i.test(a.text) ? `${tr('admin.voice.action.delete_npc')} — Яна (#9)` : null;
        if (!VOICE.confirm) subtitle(`${tr('admin.voice.title')}: ${tr('admin.voice.done')}`, 'success');
        sendVoice();
      }, 900);
      return;
    }
    else if (message.cmd === 'confirm') { note(key, `→ confirm ${a.accept}`); VOICE.confirm = null; subtitle(`${tr('admin.voice.title')}: ${tr(a.accept ? 'admin.voice.done' : 'admin.voice.reason.Cancelled')}`, a.accept ? 'success' : 'error'); }
    else if (message.cmd === 'typed') { note(key, `→ debug_command ${a.json}`); try { JSON.parse(a.json); subtitle(`${tr('admin.voice.title')}: ${tr('admin.voice.done')}`, 'success'); } catch { subtitle(`${tr('admin.voice.title')}: ${tr('admin.voice.reason.InvalidInput')}`, 'error'); } }
    sendVoice();
    return;
  }
  if (message.screen === 'voiceCapture') {
    note(key, `→ ${message.cmd}`);
    if (message.cmd === 'done') finishCapture();
    else { VOICE.recording = false; sendVoice(); sendHud(); }
    return;
  }
  if (message.screen === 'inspector') {
    note(key, `${args} → HexInspectorPanel: закрыть`);
    DEBUG.on.hexInspector = false; sendDebug();
    return;
  }
  if (message.screen === 'debug') {
    const id = message.args?.id;
    if (message.cmd === 'close') { DEBUG.open = false; note(key, '→ закрыть'); sendDebug(); sendHud(); return; }
    note(key, `${args} → DebugControlsPanel: ${id}`);
    if (id in DEBUG.on) DEBUG.on[id] = !DEBUG.on[id];
    if (id === 'shade') DEBUG.shade = (DEBUG.shade + 1) % SHADES.length;
    if (id === 'report') { BUGS.quick = true; sendBugs(); }
    if (id === 'manager') { BUGS.manager = true; BUGS.tab = 'open'; sendBugs(); }
    sendDebug();
    return;
  }
  if (message.screen === 'map') {
    const a = message.args || {};
    if (message.cmd === 'click') { MAP.cam = { x: a.x, z: a.z }; note(key, `${args} → RtsCameraController.MoveToMapPoint`); }
    else note(key, `→ ${message.cmd === 'collapse' ? 'свернуть карту' : 'развернуть карту'}`);
    if (message.cmd === 'collapse') MAP.collapsed = true;
    if (message.cmd === 'expand') MAP.collapsed = false;
    sendMap();
    return;
  }
  if (message.screen === 'craftProject') {
    note(key, '→ закрыть карточку');
    PROJECT.open = false; sendProject();
    return;
  }
  if (message.screen === 'endSummary') {
    note(key, '→ закрыть итоги и осмотреть остров (до новой игры они не вернутся)');
    END.open = false; sendEnd();
    return;
  }
  if (message.screen === 'history') {
    note(key, `→ ${{ expand: 'развернуть историю', collapse: 'свернуть', more: 'ещё страница записей (GameHistoryLog.ReadTail)' }[message.cmd] || '?'}`);
    if (message.cmd === 'expand') HISTORY.collapsed = false;
    if (message.cmd === 'collapse') HISTORY.collapsed = true;
    if (message.cmd === 'more') HISTORY.shown += 10;
    sendHistory();
    return;
  }
  if (message.screen === 'bugReport' || message.screen === 'bugManager') {
    const a = message.args || {};
    const r = BUGS.reports.find((x) => x.id === a.id);
    const say = { submit: 'POST /api/bugs/v1/reports (текст + seed/tick/npc)', manager: 'открыть менеджер', close: 'закрыть окно', new: 'новый отчёт — маленькое окно поверх',
      tab: 'вкладка', filter: 'фильтр статуса', open: 'открыть карточку', back: 'к списку', editText: 'сохранить новый текст отчёта', comment: 'комментарий',
      editComment: 'изменить свой комментарий', fixed: 'подтвердить исправление → fixed', rework: 'вернуть в доработку с комментарием', archive: 'архив / восстановить' };
    note(key, `${message.cmd === 'submit' || message.cmd.startsWith('edit') || message.cmd === 'comment' || message.cmd === 'rework' ? '' : args} → ${say[message.cmd] || '?'}`);
    switch (message.cmd) {
      case 'submit':
        BUGS.reports.unshift({ id: BUGS.nextId, status: 'created', text: a.text, created: '18.10 14:30', context: `seed=30430 tick=1840305 npc=${game.selected}`, comments: [], commits: [], agent: null, build: null });
        BUGS.card = BUGS.nextId++; BUGS.quick = false; BUGS.manager = true; BUGS.tab = 'open'; BUGS.filter = null; break;
      case 'manager': BUGS.quick = false; BUGS.manager = true; break;
      case 'close': if (message.screen === 'bugReport') BUGS.quick = false; else BUGS.manager = false; break;
      case 'new': BUGS.quick = true; break;
      case 'tab': BUGS.tab = a.value; BUGS.card = null; break;
      case 'filter': BUGS.filter = a.status; break;
      case 'open': BUGS.card = a.id; break;
      case 'back': BUGS.card = null; break;
      case 'editText': if (r) r.text = a.text; break;
      case 'comment': if (r) r.comments.push({ author: 'user', text: a.text }); break;
      case 'editComment': if (r?.comments[a.index]) r.comments[a.index].text = a.text; break;
      case 'fixed': if (r) { r.status = 'fixed'; BUGS.card = null; } break;
      case 'rework': if (r) { r.status = 'rework'; r.comments.push({ author: 'user', text: a.text }); } break;
      case 'archive': if (r) { r.archived = !!a.value; BUGS.card = null; } break;
    }
    sendBugs();
    return;
  }
  if (message.screen === 'blueprint') {
    const a = message.args || {};
    const say = { mode: 'сменить вкладку', category: 'открыть категорию', card: 'взять элемент: тянуть в мире', select: 'инструмент «Выбор»',
      undo: 'отменить жест', redo: 'повторить жест', delete: 'удалить выбранное', done: 'снять выделение', exit: 'закрыть конструктор (черновик сохранён)',
      build: 'отдать чертёж: PlaceBuildingBlueprintCommand', rotate: 'повернуть выбранную мебель' };
    note(key, `${args} → ${say[message.cmd] || '?'}`);
    bp.statusText = null;
    if (message.cmd === 'exit') { bp.open = false; sendBlueprint(); sendHud(); return; }
    if (message.cmd === 'build') {
      if (!bp.door) { bp.statusText = tr(bp.undo === 0 ? 'blueprint.build.needs_floor' : 'blueprint.build.needs_door'); sendBlueprint(); return; }
      bp.open = false; sendBlueprint(); sendHud(); return;
    }
    if (message.cmd === 'mode') { bp.mode = a.value; bp.category = bpCats()[0][0]; bp.active = ''; }
    if (message.cmd === 'category') bp.category = a.id;
    if (message.cmd === 'card') { bp.active = bp.active === a.id ? '' : a.id; bp.picked = null; bp.select = false;
      if (bp.kind === 'free' && bp.active) bp.statusKey = 'blueprint.status.free_start'; }
    if (message.cmd === 'select') { bp.active = ''; bp.select = true; bp.statusKey = 'blueprint.shortcuts'; }
    if (message.cmd === 'undo' && bp.undo > 0) { bp.undo -= 1; bp.redo += 1; bp.statusKey = 'blueprint.status.undo'; }
    if (message.cmd === 'redo' && bp.redo > 0) { bp.redo -= 1; bp.undo += 1; bp.statusKey = 'blueprint.status.redo'; }
    if (message.cmd === 'delete') { bp.picked = null; bp.undo += 1; }
    if (message.cmd === 'done') { bp.picked = null; bp.active = ''; }
    if (message.cmd === 'rotate' && bp.picked) bp.picked.yaw = (bp.picked.yaw + (a.dir < 0 ? 300 : 60)) % 360;
    sendBlueprint();
    return;
  }
  if (message.screen === 'build') {
    const a = message.args || {};
    const say = { mode: 'сменить вкладку каталога', category: 'открыть категорию', card: 'взять изделие на курсор (призрак в мире)',
      rotate: 'повернуть на 60°', delete: 'снять размеченную площадку', edit: 'открыть конструктор дома', finish: 'выйти из режима стройки' };
    note(key, `${args} → ${say[message.cmd] || '?'}`);
    if (message.cmd === 'finish') { setBuild(false); return; }
    if (message.cmd === 'mode') { build.mode = a.value; build.category = BUILD[a.value][0][0]; build.active = ''; build.site = null; build.arrows = null; }
    if (message.cmd === 'category') build.category = a.id;
    if (message.cmd === 'card') {
      if (a.id === 'free.architecture' || a.id === 'custom.project') {
        note(key, `→ ${a.id === 'free.architecture' ? 'свободное строительство' : 'свой проект'}: лоток сменяется конструктором`);
        build.open = false; post({ t: 'hide', screen: 'build' }); sendHud(); openBlueprint(a.id === 'free.architecture' ? 'free' : 'project');
        return;
      }
      build.active = build.active === a.id ? '' : a.id; build.site = null; build.status = 'build.mode.hint';
    }
    if (message.cmd === 'delete') { build.site = null; build.arrows = null; }
    sendBuild();
    return;
  }
  if (message.screen === 'loot') {
    const a = message.args || {};
    if (message.cmd === 'close') { note(key, '→ закрыть окно'); loot.open = false; sendLoot(); return; }
    if (message.cmd === 'info') { loot.info = a.uid; sendLoot(); return; }
    if (message.cmd === 'move') {
      const toRight = a.to === 'right';
      note(key, `${args} → TransferInventoryCommand: ${toRight ? (loot.mode === 'gift' ? 'подарить' : 'отдать') : 'забрать'} ×${a.count}`);
      lootMove(a.uid, a.to, a.count, false);
      return;
    }
    if (message.cmd === 'quick') {
      const mine = loot.sides.left.find((i) => i.uid === a.uid);
      const theirs = loot.sides.right.find((i) => i.uid === a.uid);
      if (!mine && loot.mode === 'gift') {
        note(key, `${args} → отказ: подарок в одну сторону`);
        loot.status = { key: 'gift.take_forbidden', tone: 'error' }; sendLoot(); return;
      }
      if (theirs) { note(key, `${args} → забрать (одежду — сразу надеть)`); lootMove(a.uid, 'left', theirs.count, isClothing(theirs.def)); return; }
      if (mine && isClothing(mine.def)) {
        note(key, `${args} → ${mine.group === 'worn' ? 'снять' : 'надеть'}`);
        loot.pending = true; loot.status = { key: mine.group === 'worn' ? 'loot.removing' : 'loot.equipping', tone: 'busy' }; sendLoot();
        setTimeout(() => { mine.group = mine.group === 'worn' ? 'bag' : 'worn'; loot.pending = false; loot.status = null; sendLoot(); }, 700);
        return;
      }
      note(key, `${args} → нет быстрого действия`);
      loot.status = { key: 'loot.no_quick_action', tone: 'hint' }; sendLoot();
      return;
    }
    return;
  }
  if (message.screen === 'contextMenu') {
    if (message.cmd === 'pick') {
      const m = contextModel();
      const [i, j] = message.args.path;
      const e = j == null ? m.entries[i] : m.entries[i].children[j];
      note(key, `${args} → «${j == null ? '' : `${m.entries[i].label} › `}${e.label}»: приказ колонистке`);
    } else note(key, '→ закрыть меню (Esc)');
    ctx.open = false;
    sendContext();
    return;
  }
  if (message.screen === 'health' || message.screen === 'journal') {
    const a = message.args || {};
    if (message.cmd === 'dollRect') { side.healthRect = a.w > 0 ? a : null; drawWorld(); return; }
    if (message.cmd === 'dollRotate') return;
    note(key, `${args} → закрыть окно${message.screen === 'journal' ? ' (прочитанное запоминается до последней записи)' : ''}`);
    if (message.screen === 'journal') { side.read[game.selected] = 0; side.journal = false; }
    else { side.health = false; side.healthRect = null; drawWorld(); }
    sendSide();
    return;
  }
  if (message.screen === 'roster' || message.screen === 'character') {
    const a = message.args || {};
    if (message.cmd === 'portraitRect') { game.portraitRect = a; drawWorld(); return; }
    if (message.cmd === 'coverage') return; // C#: keeps the followed colonist above the card
    note(key, `${args} → ${{
      select: 'выбрать колонистку (Shift — добавить к выбору)', selectClan: 'выбрать весь лагерь',
      control: 'сменить управление: ИИ / вручную / звонок', pace: 'темп приказов по умолчанию', stop: 'отменить текущий приказ',
      open: 'открыть окно', collapse: 'свернуть карточку', expand: 'развернуть карточку',
      groupMode: 'сменить режим группы', groupStop: 'остановить группу' }[message.cmd] || '?'}`);
    if (message.cmd === 'select') {
      if (a.add) { game.group = [...new Set([...(game.group.length ? game.group : [game.selected]), a.id])]; showDialog('gameGroup'); return; }
      game.selected = a.id; game.group = [];
      if (state.dialog !== 'gameCharacter') { showDialog('gameCharacter'); return; }
    }
    if (message.cmd === 'selectClan') { game.group = COLONY.map((c) => c.id); showDialog('gameGroup'); return; }
    if (message.cmd === 'control') game.control = a.mode;
    if (message.cmd === 'pace') game.pace = a.value;
    if (message.cmd === 'open' && a.window === 'inventory') { inv.open = true; sendInventory(); return; }
    if (message.cmd === 'open' && a.window === 'health') { side.health = true; side.journal = false; sendSide(); return; }
    if (message.cmd === 'open' && a.window === 'journal') { side.journal = true; side.health = false; sendSide(); return; }
    if (message.cmd === 'collapse') game.collapsed = true;
    if (message.cmd === 'expand') game.collapsed = false;
    sendGame();
    return;
  }
  if (message.screen === 'creator') {
    const a = message.args || {};
    if (message.cmd === 'previewRect') { creator.previewRect = a; drawWorld(); return; }
    if (message.cmd === 'look') return; // C#: which thumbnails to render next
    if (message.cmd === 'camera') {
      if (a.action === 'face' || a.action === 'body') creator.view = a.action;
      if (a.action === 'left' || a.action === 'right') creator.yaw += a.action === 'left' ? -30 : 30;
      if (a.action !== 'orbit' && a.action !== 'zoom') note(key, `${args} → повернуть камеру превью`);
      drawWorld();
      return;
    }
    if (message.cmd === 'set') {
      const d = creator.draft;
      if (a.field === 'name') creator.touched = true;
      if (a.field === 'sex' && d.sex !== a.value) { d.sex = a.value; d.skin = SKINS[a.value][0]; creator.thumbs = {}; renderThumbs(); }
      else if (a.field === 'hair') { d.hair = a.value; d.hairColour = 'prototype'; }
      else d[a.field] = a.value;
      const last = log.firstElementChild; // a drag or typing is one line per field
      if (last && last.dataset.field === a.field) last.remove();
      note(key, JSON.stringify(a)).dataset.field = a.field;
      if (['sex', 'skin', 'hair', 'hairColour', 'eyes'].includes(a.field)) { if (a.field === 'skin' || a.field === 'sex') creator.thumbs = Object.fromEntries(Object.entries(creator.thumbs).filter(([k]) => k.startsWith('skin:'))), renderThumbs(); reloadPreview(); }
      else sendCreator();
      return;
    }
    if (message.cmd === 'submit') {
      note(key, '→ заморозить попытку, POST /character, затем вход в мир');
      creator.busy = 'creator.creating'; sendCreator();
      setTimeout(() => { creator.busy = null; sendCreator(); }, 1800);
      return;
    }
    if (message.cmd === 'signIn') { note(key, '→ открыть /login/'); return; }
    if (message.cmd === 'retry') { note(key, '→ загрузить настройки заново'); return; }
    if (message.cmd === 'back') { note(key, '→ назад к выбору сервера'); showDialog('titleServers'); return; }
    return;
  }
  if (message.screen === 'title') {
    if (message.cmd === 'play') {
      note(key, `${args} → свой сервер страницы: создание персонажа, потом подключение`);
      title.connecting = true;
      title.error = null;
      sendTitle();
      setTimeout(() => { title.connecting = false; showDialog('creator'); }, 700);
    }
    return;
  }
  if (key === 'protocolUpdate.update') {
    if (state.platform === 'web') {
      note(key, '→ перезагрузить страницу (на стенде не перезагружаем)');
    } else {
      state.updateFailed = true;
      note(key, '→ запустить обновлятор; на стенде показан отказ');
      refreshDialog();
    }
    return;
  }
  note(key, `${args} → ${EFFECT[key] || '?'}`);
  if (message.screen === 'hud') {
    if (message.cmd === 'pause') state.hud.paused = !state.hud.paused;
    if (message.cmd === 'speed') { state.hud.speed = message.args.value; state.hud.paused = false; }
    if (message.cmd === 'build') { setBuild(!build.open); return; }
    if (message.cmd === 'bug') { BUGS.quick = true; BUGS.manager = false; sendBugs(); return; }
    if (message.cmd === 'debug') { DEBUG.open = !DEBUG.open; sendDebug(); sendHud(); return; }
    if (message.cmd === 'admin') {
      // AdminVoicePanel.Microphone: the window until access is granted, then record / stop.
      if (VOICE.recording) { finishCapture(); return; }
      if (VOICE.access !== 'approved') { VOICE.open = true; note(key, '→ окно админа'); sendVoice(); return; }
      VOICE.recording = true; note(key, '→ запись'); sendVoice(); sendHud(); return;
    }
    sendHud();
    return;
  }
  showDialog(null);
}

// Unity drains the queue once per frame. The stand uses a timer instead of
// animation frames: those stop in a hidden tab and the queue would pile up.
function pump() {
  for (let json = window.HexUi?.take(); json; json = window.HexUi.take()) handle(JSON.parse(json));
}

// A living HUD: ten game minutes a second, so model updates are visible
// (and prove that a refreshed HUD never climbs over a dialog).
setInterval(() => {
  const h = state.hud;
  if (state.platform === 'desktop' && h.paused) return;
  h.minutes += 10;
  if (h.minutes >= 24 * 60) { h.minutes -= 24 * 60; h.day += 1; }
  sendHud();
}, 1000);

// ---- the world behind the windows: flat faceted hex tiles, like the game ----
function hash(x, y) {
  const s = Math.sin(x * 127.1 + y * 311.7) * 43758.5453;
  return s - Math.floor(s);
}

// LoadingScreen.cs draws the island art (cover) and the logo at left 36,
// top 20, 360 px wide on a 1920×1080 reference — Unity's layer, not the page's.
const art = { island: new Image(), logo: new Image() };
art.island.src = 'assets/loading_island.jpg';
art.logo.src = 'assets/logo.png';
art.island.onload = art.logo.onload = () => { if (isFull(state.dialog)) drawWorld(); };

function drawTitleArt(g, w, h) {
  g.fillStyle = 'rgb(13 18 23)';
  g.fillRect(0, 0, w, h);
  const img = art.island;
  if (img.naturalWidth) {
    const k = Math.max(w / img.naturalWidth, h / img.naturalHeight);
    g.drawImage(img, (w - img.naturalWidth * k) / 2, (h - img.naturalHeight * k) / 2, img.naturalWidth * k, img.naturalHeight * k);
  }
  const s = h / 1080;
  if (art.logo.naturalWidth) {
    const lw = 360 * s;
    g.drawImage(art.logo, 36 * s, 20 * s, lw, lw * art.logo.naturalHeight / art.logo.naturalWidth);
  }
}

// The creator's 3D preview, as Unity would draw it into the rectangle the page
// reported: here a crop of the title art stands in for the live model.
function drawPreview(g, w, h) {
  g.fillStyle = 'rgb(9 13 16)';
  g.fillRect(0, 0, w, h);
  const r = creator.previewRect;
  if (!r || creator.preview !== 'ready' || state.dialog !== 'creator' || !art.island.naturalWidth) return;
  const img = art.island;
  const face = creator.view === 'face';
  // The middle girl of loading_island: full body, or just her head.
  const src = face ? [img.naturalWidth * 0.45, img.naturalHeight * 0.02, img.naturalWidth * 0.12, img.naturalHeight * 0.3]
    : [img.naturalWidth * 0.405, 0, img.naturalWidth * 0.185, img.naturalHeight];
  const k = Math.min(r.w / src[2], r.h / src[3]);
  const dw = src[2] * k;
  const dh = src[3] * k;
  g.save();
  g.beginPath(); g.rect(r.x, r.y, r.w, r.h); g.clip();
  const grd = g.createRadialGradient(r.x + r.w / 2, r.y + r.h * 0.45, 10, r.x + r.w / 2, r.y + r.h / 2, Math.max(r.w, r.h) * 0.7);
  grd.addColorStop(0, 'rgb(38 52 58)'); grd.addColorStop(1, 'rgb(9 13 16)');
  g.fillStyle = grd; g.fillRect(r.x, r.y, r.w, r.h);
  g.translate(r.x + r.w / 2, 0);
  if (Math.round(creator.yaw / 30) % 2) g.scale(-1, 1); // a turn, as far as a flat picture can turn
  g.drawImage(img, ...src, -dw / 2, r.y + (r.h - dh) / 2, dw, dh);
  g.restore();
}

// The live portrait (PortraitStage), as Unity draws it into portraitRect.
function drawPortrait(g) {
  const r = game.portraitRect;
  if (!r || state.dialog !== 'gameCharacter' || game.collapsed || !art.island.naturalWidth) return;
  const img = art.island;
  const src = [img.naturalWidth * 0.43, img.naturalHeight * 0.02, img.naturalWidth * 0.16, img.naturalHeight * 0.5];
  const k = Math.max(r.w / src[2], r.h / src[3]);
  g.save();
  g.beginPath(); g.rect(r.x, r.y, r.w, r.h); g.clip();
  g.drawImage(img, ...src, r.x + (r.w - src[2] * k) / 2, r.y, src[2] * k, src[3] * k);
  g.restore();
}

// The inventory doll (CharacterDollStage), full figure, into dollRect.
function drawDoll(g, r = inv.open ? inv.dollRect : null, zones = null) {
  if (!r || !r.w || !art.island.naturalWidth) return;
  const img = art.island;
  const src = [img.naturalWidth * 0.405, 0, img.naturalWidth * 0.185, img.naturalHeight];
  const k = Math.min(r.w / src[2], r.h / src[3]);
  g.save();
  g.beginPath(); g.rect(r.x, r.y, r.w, r.h); g.clip();
  g.fillStyle = 'rgb(11 16 19)'; g.fillRect(r.x, r.y, r.w, r.h);
  const dw = src[2] * k, dh = src[3] * k, dx = r.x + (r.w - dw) / 2, dy = r.y + (r.h - dh) / 2;
  g.drawImage(img, ...src, dx, dy, dw, dh);
  if (zones) {
    // Stand-in for the zone overlay CharacterDollStage paints on the real doll.
    const at = { Head: [0.5, 0.09, 0.09], Torso: [0.5, 0.29, 0.14], Pelvis: [0.5, 0.46, 0.11], ArmL: [0.27, 0.33, 0.07],
      ArmR: [0.73, 0.33, 0.07], LegL: [0.41, 0.72, 0.09], LegR: [0.59, 0.72, 0.09] };
    const mix = (a2, b2, t2) => a2.map((x, i) => Math.round(x + (b2[i] - x) * t2));
    for (const z of healthModel().zones) {
      const [fx, fy, fr] = at[z.id];
      const v = z.severed ? -1 : Math.max(0, z.hp - z.critical);
      const c = v < 0 ? [61, 43, 43] : v >= 0.5 ? mix([232, 178, 60], [92, 184, 84], (v - 0.5) * 2) : mix([232, 87, 79], [232, 178, 60], v * 2);
      g.fillStyle = `rgb(${c.join(' ')} / 0.55)`;
      g.beginPath(); g.ellipse(dx + fx * dw, dy + fy * dh, fr * dw * 1.6, fr * dh * 0.9, 0, 0, Math.PI * 2); g.fill();
    }
  }
  g.restore();
}

function drawWorld() {
  const canvas = $('world');
  const [w, h] = state.size;
  canvas.width = w;
  canvas.height = h;
  const g = canvas.getContext('2d');
  if (isTitle(state.dialog)) { drawTitleArt(g, w, h); return; }
  if (isCreator(state.dialog)) { drawPreview(g, w, h); return; }
  if (isGame(state.dialog)) { drawHexWorld(g, w, h); drawPortrait(g); drawDoll(g); if (side.health) drawDoll(g, side.healthRect, true); return; }
  drawHexWorld(g, w, h);
}

function drawHexWorld(g, w, h) {
  g.fillStyle = '#1f5f78';
  g.fillRect(0, 0, w, h);
  const r = h / 14;
  const cx = w * 0.5;
  const cy = h * 0.52;
  const palette = {
    deep: ['#1f5f78', '#22667f'],
    shallow: ['#2f8296', '#348a9d'],
    sand: ['#e2c88f', '#d8bc80'],
    grass: ['#7fae58', '#73a14e'],
    forest: ['#4f8442', '#467a3b'],
  };
  for (let q = -14; q <= 14; q++) {
    for (let s = -9; s <= 9; s++) {
      const x = cx + r * 1.5 * q;
      const y = cy + r * Math.sqrt(3) * (s + q / 2);
      if (x < -r * 2 || x > w + r * 2 || y < -r * 2 || y > h + r * 2) continue;
      const d = Math.hypot((x - cx) / (w * 0.36), (y - cy) / (h * 0.36)) + (hash(q, s) - 0.5) * 0.35;
      const kind = d < 0.45 ? 'forest' : d < 0.75 ? 'grass' : d < 0.92 ? 'sand' : d < 1.15 ? 'shallow' : 'deep';
      const [light, dark] = palette[kind];
      for (let i = 0; i < 6; i++) {
        const a0 = (Math.PI / 3) * i;
        const a1 = a0 + Math.PI / 3;
        g.beginPath();
        g.moveTo(x, y);
        g.lineTo(x + r * Math.cos(a0), y + r * Math.sin(a0));
        g.lineTo(x + r * Math.cos(a1), y + r * Math.sin(a1));
        g.closePath();
        g.fillStyle = i < 3 ? dark : light; // two facet tones = the low-poly look
        g.fill();
      }
      g.strokeStyle = 'rgb(0 0 0 / 0.12)';
      g.lineWidth = 1;
      g.beginPath();
      for (let i = 0; i <= 6; i++) {
        const a = (Math.PI / 3) * i;
        g[i ? 'lineTo' : 'moveTo'](x + r * Math.cos(a), y + r * Math.sin(a));
      }
      g.stroke();
    }
  }
}

// ---- sizing: the game area keeps its pixel size and is scaled to fit ----
function layout() {
  const viewport = $('viewport');
  const stage = $('hex-stage');
  const [w, h] = state.size;
  stage.style.width = `${w}px`;
  stage.style.height = `${h}px`;
  const scale = viewport.clientWidth / w;
  stage.style.transform = `scale(${scale})`;
  viewport.style.height = `${h * scale}px`;
  // Two frames: the rem scale is set by ui/main.js's ResizeObserver, which
  // runs after this frame's animation callbacks.
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const rem = parseFloat(document.documentElement.style.fontSize) || 16;
    $('frame-size').textContent = `${w}×${h} · 1rem = ${rem.toFixed(1)}px · показано в ${Math.round(scale * 100)}%`;
  }));
}

function bindSegment(attr, apply) {
  for (const button of document.querySelectorAll(`[data-${attr}]`)) {
    button.addEventListener('click', () => {
      for (const other of document.querySelectorAll(`[data-${attr}]`)) other.setAttribute('aria-pressed', String(other === button));
      apply(button.dataset[attr]);
    });
  }
}

bindSegment('lang', (lang) => { state.lang = lang; if (ready) { sendStrings(); sendCorner(); } });
bindSegment('platform', (platform) => { state.platform = platform; state.updateFailed = false; sendHud(); refreshDialog(); });
bindSegment('size', (size) => { state.size = size.split('x').map(Number); drawWorld(); layout(); });
bindSegment('weather', (weather) => { state.hud.raining = weather === 'rain'; sendHud(); });
bindSegment('link', (link) => { state.hud.link = link; sendHud(); });
bindSegment('target', (target) => { ctx.target = target; if (ctx.open) sendContext(); });
// C# (TryDismissOnOutsidePress): a press outside the open menu only closes it.
let ctxDismissed = false;
$('world').addEventListener('pointerdown', () => {
  ctxDismissed = ctx.open;
  if (!ctx.open) return;
  ctx.open = false;
  sendContext();
  note('мир', 'нажатие мимо меню → C# закрывает меню, дальше в мир нажатие не идёт');
});
$('world').addEventListener('pointermove', (e) => {
  if (!build.open || !build.active) return;
  const r = $('world').getBoundingClientRect();
  const x = (e.clientX - r.left) / r.width, y = (e.clientY - r.top) / r.height;
  const ok = x < 0.55; // the stand pretends the right side is taken
  if (build.arrows && Math.abs(build.arrows.x - x) < 0.01 && Math.abs(build.arrows.y - y) < 0.01 && ok === build.ok) return;
  build.arrows = { x, y }; build.ok = ok; sendBuild();
});
$('world').addEventListener('click', (e) => {
  if (bp.open) {
    const r = $('world').getBoundingClientRect();
    const at = { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
    if (bp.active) {
      bp.undo += 1; bp.redo = 0; bp.statusText = null;
      if (bp.active === 'architecture.door.wood') bp.door = true;
      note('мир', 'жест в мире → элемент в черновике (в «свободном» — сразу ApplyFreeArchitectureCommand)');
    } else {
      const furniture = bp.mode === 'furniture';
      bp.picked = at.x < 0.35 && !furniture ? { room: true } : { id: furniture ? 'furniture.wardrobe' : 'architecture.wall.wood', yaw: 0, at, furniture };
      note('мир', 'клик «Выбором» → элемент или комната, у комнаты ручки сторон');
    }
    sendBlueprint();
    return;
  }
  if (!build.open) return;
  const r = $('world').getBoundingClientRect();
  const at = { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
  if (build.active) {
    note('мир', build.ok ? 'клик → PlaceFurnitureSiteCommand: площадка размечена' : 'клик → здесь нельзя строить');
    build.status = build.ok ? 'build.mode.placed' : 'build.mode.blocked';
  } else {
    note('мир', 'клик по размеченной площадке → выбрать её: повернуть, снять');
    build.site = 'campfire'; build.arrows = at; build.status = 'build.mode.hint';
  }
  sendBuild();
});
$('world').addEventListener('contextmenu', (e) => {
  e.preventDefault();
  if (!isGame(state.dialog) || ctxDismissed) return;
  const r = $('world').getBoundingClientRect();
  ctx.at = { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
  ctx.open = true;
  sendContext();
});
for (const button of document.querySelectorAll('[data-dialog]')) {
  button.addEventListener('click', () => { state.updateFailed = false; showDialog(button.dataset.dialog); });
}
$('reopen').addEventListener('click', () => showDialog('gameMenu'));
$('clear-log').addEventListener('click', () => { log.textContent = ''; });
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  // GameMenu.cs: Escape opens the menu and closes it; other dialogs ignore it.
  if (isFull(shownDialog)) return; // GameMenu: Escape never opens over the loading screen
  if (shownDialog === 'gameMenu') handle({ t: 'cmd', screen: 'gameMenu', cmd: 'continue', args: {} });
  else if (!shownDialog) showDialog('gameMenu');
});
new ResizeObserver(layout).observe($('viewport'));

drawWorld();
layout();
setInterval(pump, 16);
post({ t: 'hello', v: bridge.PROTOCOL });
