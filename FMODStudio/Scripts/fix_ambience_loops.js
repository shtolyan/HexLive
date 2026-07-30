// §67.12 r2: эмбиент должен ЗАЦИКЛИВАТЬСЯ.
//
// populate_events.js ставил инструменту `looping = true`, но этого мало: сам
// ИНСТРУМЕНТ крутит свой сэмпл, а таймлайн события всё равно доезжает до конца
// триггер-региона и событие останавливается. FMOD такое событие считает
// одноразовым (`isOneshot() == true`), поэтому прибой и птицы отыгрывали свои
// 23 секунды и замолкали навсегда — а с учётом того, что беды стартуют на
// нулевой громкости (кроссфейд день/ночь), их вдобавок глушило виртуализацией.
//
// Лечение: положить на маркер-трек события LOOP REGION длиной во весь
// инструмент. Тогда таймлайн ходит по кругу и событие живёт вечно.
//
// Запуск (обязательно под псевдотерминалом, иначе fmodstudiocl падает):
//   script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
//     -script "$PWD/FMODStudio/Scripts/fix_ambience_loops.js" \
//     "$PWD/FMODStudio/HexLive/HexLive.fspro"
var LOG = "/Volumes/ORICO/HexLive/FMODStudio/Scripts/fix_ambience_loops.log";
var AMBIENCE = ["loop_waves", "loop_jungle_day", "loop_crickets", "loop_rain", "loop_fire"];

var lines = [];
function w(s) { lines.push(String(s)); }
function flush() {
    var f = studio.system.getFile(LOG);
    f.open(studio.system.openMode.WriteOnly);
    f.writeText(lines.join("\n") + "\n");
    f.close();
}

try {
    // Сначала выясняем, как в этой версии называются свойства луп-региона —
    // гадать нельзя, схема менялась между версиями Studio.
    var probe = studio.project.create("LoopRegion");
    var keys = [];
    for (var k in probe) { keys.push(k); }
    w("LoopRegion поля: " + keys.join(", "));
    studio.project.deleteObject(probe);

    var fixed = 0, skipped = 0;
    studio.project.model.Event.findInstances().forEach(function (ev) {
        if (AMBIENCE.indexOf(ev.name) < 0) {
            return;
        }

        // Длина = длина инструмента на группе (SingleSound / MultiSound).
        var len = 0;
        ev.groupTracks.forEach(function (track) {
            track.modules.forEach(function (m) {
                if (m.length && m.length > len) { len = m.length; }
            });
        });
        if (len <= 0) { w(ev.name + ": не нашёл длину инструмента"); skipped++; return; }

        // Уже есть луп-регион? Тогда не плодим второй.
        var already = false;
        ev.markerTracks.forEach(function (mt) {
            mt.markers.forEach(function (mk) {
                if (mk.isOfExactType && mk.isOfExactType("LoopRegion")) { already = true; }
            });
        });
        if (already) { w(ev.name + ": луп-регион уже есть"); skipped++; return; }

        var region = studio.project.create("LoopRegion");
        region.position = 0;
        region.length = len;
        ev.markerTracks[0].markers.add(region);
        w(ev.name + ": добавлен луп-регион 0.." + len.toFixed(2) + " c");
        fixed++;
    });

    w("исправлено: " + fixed + ", пропущено: " + skipped);
    w("save: " + studio.project.save());
} catch (err) {
    w("FATAL: " + err);
}
flush();
