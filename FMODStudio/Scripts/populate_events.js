// Фаза B: импорт 62 сэмплов + события 1:1 по FmodSfx.Sfx.* + роутинг в Master bank.
var LOG = "/private/tmp/claude-501/-Volumes-ORICO-HexLive/96b3fd6d-27c4-4f73-9b0c-28a9b5c6b7da/scratchpad/phaseB.log";
var SFX_DIR = "/Volumes/ORICO/HexLive/Assets/StreamingAssets/HexLive/Sfx";
var lines = [];
function w(s) { lines.push(String(s)); }
function flush() {
    var f = studio.system.getFile(LOG);
    f.open(studio.system.openMode.WriteOnly);
    f.writeText(lines.join("\n") + "\n");
    f.close();
}
function dB(linear) { return 20 * Math.log(linear) / Math.LN10; }

// Каталог — зеркало FmodSfx.Defs (громкость линейная, дистанции wu).
var CATALOG = [
    { id: "chop_wood",       vol: 0.80, min: 1.2, max: 26, spatial: true,  loop: false },
    { id: "chop_coco",       vol: 0.70, min: 1.2, max: 22, spatial: true,  loop: false },
    { id: "mine_stone",      vol: 0.80, min: 1.2, max: 26, spatial: true,  loop: false },
    { id: "hammer",          vol: 0.70, min: 1.2, max: 24, spatial: true,  loop: false },
    { id: "hit_flesh",       vol: 0.85, min: 1.2, max: 28, spatial: true,  loop: false },
    // §104 r5: удар по человеку — кулаком и клинком. Раньше человеческий удар
    // не звучал вовсе: hit_flesh играл только на укус, отрыв конечности,
    // разделку туши и на удар ПО ВОЛКУ.
    { id: "hit_punch",       vol: 0.85, min: 1.2, max: 28, spatial: true,  loop: false },
    { id: "hit_blade",       vol: 0.85, min: 1.2, max: 28, spatial: true,  loop: false },
    { id: "body_fall",       vol: 0.80, min: 1.2, max: 24, spatial: true,  loop: false },
    { id: "swing",           vol: 0.50, min: 1.0, max: 18, spatial: true,  loop: false },
    { id: "chop_accent",     vol: 0.90, min: 1.5, max: 30, spatial: true,  loop: false },
    { id: "tree_creak",      vol: 0.90, min: 1.5, max: 30, spatial: true,  loop: false },
    { id: "step_grass",      vol: 0.32, min: 0.8, max: 12, spatial: true,  loop: false },
    { id: "step_sand",       vol: 0.32, min: 0.8, max: 12, spatial: true,  loop: false },
    { id: "step_water",      vol: 0.40, min: 0.8, max: 14, spatial: true,  loop: false },
    { id: "wolf_growl",      vol: 0.85, min: 1.5, max: 34, spatial: true,  loop: false },
    { id: "wolf_bite",       vol: 0.90, min: 1.5, max: 30, spatial: true,  loop: false },
    { id: "wolf_howl",       vol: 0.70, min: 6.0, max: 70, spatial: true,  loop: false },
    { id: "hurt_f",          vol: 0.85, min: 1.5, max: 30, spatial: true,  loop: false },
    { id: "death_f",         vol: 0.95, min: 2.0, max: 40, spatial: true,  loop: false },
    { id: "splash",          vol: 0.80, min: 1.2, max: 24, spatial: true,  loop: false },
    { id: "gecko",           vol: 0.45, min: 4.0, max: 40, spatial: true,  loop: false },
    { id: "loop_waves",      vol: 0.70, min: 4.0, max: 42, spatial: true,  loop: true },
    { id: "loop_jungle_day", vol: 0.40, min: 0,   max: 0,  spatial: false, loop: true },
    { id: "loop_crickets",   vol: 0.40, min: 0,   max: 0,  spatial: false, loop: true },
    { id: "loop_fire",       vol: 0.60, min: 0.9, max: 13, spatial: true,  loop: true },
    { id: "loop_rain",       vol: 0.50, min: 0,   max: 0,  spatial: false, loop: true },
];

try {
    var ws = studio.project.workspace;

    // -- добить остатки фазы A: пустые папки событий и VCA --
    studio.project.model.EventFolder.findInstances().forEach(function (f) {
        if (f.id !== ws.masterEventFolder.id) { studio.project.deleteObject(f); }
    });
    ["MixerVCA"].forEach(function (cls) {
        try {
            studio.project.model[cls].findInstances().forEach(function (o) {
                studio.project.deleteObject(o);
            });
            w(cls + " purged");
        } catch (e) { w(cls + " ERR: " + e); }
    });

    // -- мастер-банк --
    var masterBank = null;
    studio.project.model.Bank.findInstances().forEach(function (b) {
        if (b.isMasterBank) { masterBank = b; }
    });
    w("master bank: " + (masterBank ? masterBank.name : "NONE"));

    // -- импорт аудио: группировка по префиксу id --
    var files = studio.system.readDir(SFX_DIR, studio.system.readDirFilters.Files);
    w("dir files: " + files.length);
    var byId = {};
    files.forEach(function (path) {
        var name = path.split("/").pop();
        if (!/\.(ogg|wav)$/i.test(name)) { return; }
        var stem = name.replace(/\.(ogg|wav)$/i, "");
        var id = stem.replace(/_\d+$/, "");
        // readDir отдаёт голые имена — importAudioFile нужен абсолютный путь.
        var af = studio.project.importAudioFile(SFX_DIR + "/" + name);
        if (!af) { w("IMPORT FAIL: " + name); return; }
        (byId[id] = byId[id] || []).push(af);
    });
    for (var k in byId) { w("imported " + k + ": " + byId[k].length); }

    // §67.6: голосовые реплики персонажей — события по факту наличия групп.
    for (var vid in byId) {
        if (vid.indexOf("voice_") === 0) {
            CATALOG.push({ id: vid, vol: 0.75, min: 1.2, max: 22, spatial: true, loop: false, voice: true });
        }
    }

    // -- папки событий --
    function makeFolder(name) {
        var f = studio.project.create("EventFolder");
        f.name = name;
        f.folder = ws.masterEventFolder;
        return f;
    }
    var sfxFolder = makeFolder("SFX");
    var ambFolder = makeFolder("Ambience");
    var voiceFolder = makeFolder("Voices");

    var firstDumped = false;
    CATALOG.forEach(function (def) {
        var variants = byId[def.id];
        if (!variants || variants.length === 0) { w("NO AUDIO for " + def.id); return; }

        var ev = studio.project.create("Event");
        ev.name = def.id;
        ev.folder = def.voice ? voiceFolder : def.loop ? ambFolder : sfxFolder;

        if (!firstDumped) {
            firstDumped = true;
            var rels = [];
            for (var r in ev.relationships) { rels.push(r); }
            w("fresh event rels: " + rels.join(","));
            w("has timeline: " + (ev.timeline ? "yes" : "no") +
              ", masterTrack: " + (ev.masterTrack ? "yes" : "no") +
              ", mixer: " + (ev.mixer ? "yes" : "no"));
        }

        // Таймлайн-лист (create('Event') его не создаёт — событию без
        // параметров он нужен, чтобы инструмент имел где жить).
        var timeline = ev.timeline;
        if (!timeline) {
            timeline = studio.project.create("Timeline");
            ev.timeline = timeline;
        }

        var track = ev.addGroupTrack("Audio 1");

        // Инструмент: один вариант — SingleSound, несколько — MultiSound-плейлист.
        var instr;
        var length = 1.0;
        try { length = variants[0].length || 1.0; } catch (e) {}
        if (variants.length === 1) {
            instr = studio.project.create("SingleSound");
            instr.audioFile = variants[0];
        } else {
            instr = studio.project.create("MultiSound");
            instr.isAsync = true;
            variants.forEach(function (af) {
                var s = studio.project.create("SingleSound");
                s.audioFile = af;
                try { s.length = af.length; } catch (e) {}
                instr.relationships.sounds.add(s);
            });
        }
        instr.length = length;
        if (def.loop) { instr.looping = true; }
        track.relationships.modules.add(instr);
        timeline.relationships.modules.add(instr);

        // Громкость мастера события (линейная → дБ).
        try { ev.masterTrack.mixerGroup.volume = dB(def.vol); }
        catch (e) { w(def.id + " vol ERR: " + e); }

        // 3D: спатиалайзер на мастер-треке + дистанции (и в макросах события).
        if (def.spatial) {
            try {
                var spat = studio.project.create("SpatialiserEffect");
                spat.minimumDistance = def.min;
                spat.maximumDistance = def.max;
                ev.masterTrack.mixerGroup.effectChain.relationships.effects.add(spat);
            } catch (e) { w(def.id + " spat ERR: " + e); }
            try {
                ev.automatableProperties.minimumDistance = def.min;
                ev.automatableProperties.maximumDistance = def.max;
            } catch (e) { w(def.id + " macro ERR: " + e); }
        }

        if (masterBank) { ev.relationships.banks.add(masterBank); }
        w("event ok: " + def.id + " (" + variants.length + " var, " +
          (def.loop ? "loop" : "oneshot") + (def.spatial ? ", 3D" : ", 2D") + ")");
    });

    var ok = studio.project.save();
    w("save: " + ok);
} catch (err) {
    w("FATAL: " + err);
}
flush();
