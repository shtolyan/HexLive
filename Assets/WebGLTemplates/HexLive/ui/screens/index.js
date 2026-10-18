// §174.7: screen id (the first argument of HtmlUi.Show in C#) → component.
// A new HTML window is one line here plus its file in this folder.
import { GameMenu } from './gameMenu.js';
import { ConnectionLost } from './connectionLost.js';
import { ProtocolUpdate } from './protocolUpdate.js';
import { PlayerAssignment } from './playerAssignment.js';
import { Hud } from './hud.js';
import { Title } from './title.js';
import { Creator } from './creator.js';
import { Roster } from './roster.js';
import { Character } from './character.js';
import { Inventory } from './inventory.js';
import { Health } from './health.js';
import { Journal } from './journal.js';
import { ContextMenu } from './contextMenu.js';
import { Loot } from './loot.js';
import { Build } from './build.js';
import { BugReport, BugManager } from './bugs.js';
import { History } from './history.js';

export const SCREENS = {
  title: Title,
  creator: Creator,
  hud: Hud,
  roster: Roster,
  character: Character,
  inventory: Inventory,
  health: Health,
  journal: Journal,
  contextMenu: ContextMenu,
  loot: Loot,
  build: Build,
  history: History,
  bugReport: BugReport,
  bugManager: BugManager,
  blueprint: Build, // the house constructor: the same tray with an editor header
  gameMenu: GameMenu,
  connectionLost: ConnectionLost,
  protocolUpdate: ProtocolUpdate,
  playerAssignment: PlayerAssignment,
};
