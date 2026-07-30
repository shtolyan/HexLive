// §67.10: пересобрать ГОЛОСОВЫЕ события Studio-проекта по файлам на диске.
//
// Зачем отдельный скрипт, а не populate_events.js: тот сканирует папку Sfx
// ПЛОСКО, а реплики теперь лежат в Voices/<char>/ (их 756, свалка недопустима).
// Плюс он пересоздаёт вообще всё; здесь трогаются только voice_*.
//
// Делает:
//   1. сносит ВСЕ старые события voice_* и их аудио-ассеты из проекта
//      (старые 24 группы «6 эмоций» — их файлов на диске больше нет);
//   2. импортирует новые реплики рекурсивно;
//   3. на каждую группу — событие с MultiSound-плейлистом (3 варианта),
//      3D-спатиалайзер и дистанции как в FmodSfx.VoiceDef (0.75 / 1.2 / 22).
//
// Запуск (ОБЯЗАТЕЛЬНО под псевдотерминалом — иначе fmodstudiocl падает с
// «The files a, tty do not exist»):
//   script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
//     -script FMODStudio/Scripts/sync_voices.js "$PWD/FMODStudio/HexLive/HexLive.fspro"
var LOG = "/Volumes/ORICO/HexLive/FMODStudio/Scripts/sync_voices.log";
var VOICE_DIR = "/Volumes/ORICO/HexLive/Assets/StreamingAssets/HexLive/Sfx/Voices";
var CHARS = ["jana", "jolly", "marta", "molly"];

// Зеркало FmodSfx.VoiceDef — громкость линейная, дистанции в wu.
var VOL = 0.75, MIN_DIST = 1.2, MAX_DIST = 22;

var lines = [];
function w(s) { lines.push(String(s)); }
function flush() {
    var f = studio.system.getFile(LOG);
    f.open(studio.system.openMode.WriteOnly);
    f.writeText(lines.join("\n") + "\n");
    f.close();
}
function dB(linear) { return 20 * Math.log(linear) / Math.LN10; }

try {
    var ws = studio.project.workspace;

    // ---- 1. снести старое ------------------------------------------------
    var killedEvents = 0;
    studio.project.model.Event.findInstances().forEach(function (ev) {
        var n = ev.name || "";
        if (n.indexOf("voice_") === 0) {
            studio.project.deleteObject(ev);
            killedEvents++;
        }
    });
    w("deleted stale voice events: " + killedEvents);

    var killedAssets = 0;
    studio.project.model.AudioFile.findInstances().forEach(function (af) {
        var n = "";
        try { n = af.assetPath || af.name || ""; } catch (e) { n = ""; }
        if (String(n).indexOf("voice_") >= 0) {
            studio.project.deleteObject(af);
            killedAssets++;
        }
    });
    w("deleted stale voice assets: " + killedAssets);

    // ---- 2. мастер-банк и папка ------------------------------------------
    var masterBank = null;
    studio.project.model.Bank.findInstances().forEach(function (b) {
        if (b.isMasterBank) { masterBank = b; }
    });
    w("master bank: " + (masterBank ? masterBank.name : "NONE"));

    var voiceFolder = null;
    studio.project.model.EventFolder.findInstances().forEach(function (f) {
        if (f.name === "Voices") { voiceFolder = f; }
    });
    if (!voiceFolder) {
        voiceFolder = studio.project.create("EventFolder");
        voiceFolder.name = "Voices";
        voiceFolder.folder = ws.masterEventFolder;
        w("created Voices folder");
    }

    // ---- 3. импорт по персонажам ----------------------------------------
    var byId = {};
    var imported = 0;
    CHARS.forEach(function (chr) {
        var dir = VOICE_DIR + "/" + chr;
        var files;
        try {
            files = studio.system.readDir(dir, studio.system.readDirFilters.Files);
        } catch (e) {
            w("readDir FAIL " + chr + ": " + e);
            return;
        }

        files.forEach(function (path) {
            var name = String(path).split("/").pop();
            if (!/^voice_.*\.wav$/i.test(name)) { return; }
            var id = name.replace(/\.wav$/i, "").replace(/_\d+$/, "");
            var af = studio.project.importAudioFile(dir + "/" + name);
            if (!af) { w("IMPORT FAIL: " + name); return; }
            (byId[id] = byId[id] || []).push(af);
            imported++;
        });
        w(chr + ": files=" + files.length);
    });
    w("imported files: " + imported);

    // ---- 4. событие на группу -------------------------------------------
    var made = 0, failed = 0;
    for (var id in byId) {
        var variants = byId[id];
        try {
            var ev = studio.project.create("Event");
            ev.name = id;
            ev.folder = voiceFolder;

            var timeline = ev.timeline;
            if (!timeline) {
                timeline = studio.project.create("Timeline");
                ev.timeline = timeline;
            }

            var track = ev.addGroupTrack("Audio 1");

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
            track.relationships.modules.add(instr);
            timeline.relationships.modules.add(instr);

            try { ev.masterTrack.mixerGroup.volume = dB(VOL); }
            catch (e) { w(id + " vol ERR: " + e); }

            try {
                var spat = studio.project.create("SpatialiserEffect");
                spat.minimumDistance = MIN_DIST;
                spat.maximumDistance = MAX_DIST;
                ev.masterTrack.mixerGroup.effectChain.relationships.effects.add(spat);
                ev.automatableProperties.minimumDistance = MIN_DIST;
                ev.automatableProperties.maximumDistance = MAX_DIST;
            } catch (e) { w(id + " spat ERR: " + e); }

            if (masterBank) { ev.relationships.banks.add(masterBank); }
            made++;
        } catch (e) {
            failed++;
            w("EVENT FAIL " + id + ": " + e);
        }
    }

    w("events created: " + made + ", failed: " + failed);
    var ok = studio.project.save();
    w("save: " + ok);
} catch (err) {
    w("FATAL: " + err);
}
flush();
