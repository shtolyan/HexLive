// §67.10: досинхронизировать ГОЛОСОВЫЕ события Studio-проекта по файлам на диске.
//
// ⭐ СНАЧАЛА ПРОЧТИ, НУЖЕН ЛИ ЭТОТ СКРИПТ ВООБЩЕ. Голоса НЕ играются событиями
// Studio: `FmodSfx.EventPathFor` жёстко запрещает событийный путь для всего,
// что начинается на "voice_" (нужен конкретный файл и позиция воспроизведения
// для липсинка §67.7), и читает WAV напрямую через Core API. Поэтому НОВАЯ
// ГРУППА РЕПЛИК СЛЫШНА В ИГРЕ СРАЗУ ПОСЛЕ generate_voices.py — ни этот скрипт,
// ни пересборка банка ей не нужны. Гоняй его только когда правда нужны события
// (например, чтобы крутить голоса фейдерами в Studio).
//
// ИНКРЕМЕНТАЛЬНО: события, которые уже есть, НЕ трогаются. Раньше скрипт сносил
// все 252 события и 756 ассетов и создавал их заново — это минуты работы и
// около тысячи изменённых файлов в git на ровном месте, каждый раз. Полная
// пересборка осталась, но только по явному PURGE_ALL (см. ниже).
//
// Делает:
//   1. читает, какие события voice_* уже есть в проекте;
//   2. импортирует ТОЛЬКО файлы недостающих групп;
//   3. на каждую недостающую группу — событие с MultiSound-плейлистом
//      (3 варианта), 3D-спатиалайзер и дистанции как в FmodSfx.VoiceDef
//      (0.75 / 1.2 / 22).
//
// Запуск (ОБЯЗАТЕЛЬНО под псевдотерминалом — иначе fmodstudiocl падает с
// «The files a, tty do not exist»):
//   script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
//     -script FMODStudio/Scripts/sync_voices.js "$PWD/FMODStudio/HexLive/HexLive.fspro"
// Пути считаются ОТ ОТКРЫТОГО ПРОЕКТА (…/<repo>/FMODStudio/HexLive/HexLive.fspro),
// а не от зашитого пути: в git-воркtree чекаут другой, и зашитый путь молча
// пересобирал банк по файлам ЧУЖОГО чекаута — реплики, сгенерированные здесь,
// в него не попадали, а событий пятого голоса не появлялось вовсе.
function repoRoot() {
    var p = "";
    try { p = String(studio.project.filePath || ""); } catch (e) { p = ""; }
    var parts = p.split("/");
    if (parts.length >= 4) {
        return parts.slice(0, parts.length - 3).join("/");
    }

    return "/Volumes/ORICO/HexLive";
}

var ROOT = repoRoot();
var LOG = ROOT + "/FMODStudio/Scripts/sync_voices.log";
var VOICE_DIR = ROOT + "/Assets/StreamingAssets/HexLive/Sfx/Voices";
var CHARS = ["jana", "jolly", "kshishtof", "marta", "molly"];

// Снести ВСЕ voice_* и собрать заново. Нужно только если поменялась схема
// событий (спатиалайзер, дистанции, структура плейлиста) — и это минуты работы
// плюс ~1000 переписанных файлов в git. Для «добавили группу реплик» — false.
var PURGE_ALL = false;

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
    w("root: " + ROOT);

    // ---- 1. что уже есть (или снести всё, если так велели) ----------------
    var existing = {};
    var killedEvents = 0;
    studio.project.model.Event.findInstances().forEach(function (ev) {
        var n = ev.name || "";
        if (n.indexOf("voice_") !== 0) {
            return;
        }

        if (PURGE_ALL) {
            studio.project.deleteObject(ev);
            killedEvents++;
        } else {
            existing[n] = true;
        }
    });

    if (PURGE_ALL) {
        var killedAssets = 0;
        studio.project.model.AudioFile.findInstances().forEach(function (af) {
            var n = "";
            try { n = af.assetPath || af.name || ""; } catch (e) { n = ""; }
            if (String(n).indexOf("voice_") >= 0) {
                studio.project.deleteObject(af);
                killedAssets++;
            }
        });
        w("PURGE_ALL: deleted events=" + killedEvents + " assets=" + killedAssets);
    } else {
        var have = 0;
        for (var k in existing) { have++; }
        w("existing voice events kept: " + have);
    }

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

        var skipped = 0;
        files.forEach(function (path) {
            var name = String(path).split("/").pop();
            if (!/^voice_.*\.(wav|ogg)$/i.test(name)) { return; }
            var id = name.replace(/\.(wav|ogg)$/i, "").replace(/_\d+$/, "");
            // Событие этой группы уже собрано — не импортировать файл заново:
            // повторный импорт плодит дубли ассетов и переписывает пол-проекта.
            if (existing[id]) { skipped++; return; }
            var af = studio.project.importAudioFile(dir + "/" + name);
            if (!af) { w("IMPORT FAIL: " + name); return; }
            (byId[id] = byId[id] || []).push(af);
            imported++;
        });
        w(chr + ": files=" + files.length + " new=" + (files.length - skipped) + " skipped=" + skipped);
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
