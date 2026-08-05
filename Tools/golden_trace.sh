#!/usr/bin/env bash
#
# Сравнение поведения симуляции ДО и ПОСЛЕ правки — байт в байт.
#
#   Tools/golden_trace.sh <база> [--ticks N] [--seeds A,B,C] [--preset NAME]
#
#   Tools/golden_trace.sh HEAD              # рабочее дерево против HEAD
#   Tools/golden_trace.sh HEAD~1 --preset scores --ticks 8000
#
# Зачем это существует. Распилить метод на 1900 строк без сети под ногами
# нельзя: «выглядит эквивалентно» — не проверка, а надежда. Здесь проверка
# такая: прогнать оба дерева на одних сидах, записать трассу решений и сравнить
# как текст. Совпало — поведение не поехало.
#
# ⭐ Порядок операций с float — ЭТО ПОВЕДЕНИЕ. Одна и та же последовательность
# операций даёт бит в бит один результат, поэтому переписывание `a + b + c` в
# `a + (b + c)` покажет дифф. Так и задумано: в мире, где всё решает хэш от
# сида, такая перестановка — изменение игры, а не косметика. Дифф означает
# «прими осознанно и опиши в спеке» либо «сохрани порядок».
#
# Эталон НЕ коммитится и не переиспользуется между запусками: он машинно- и
# рантайм-зависим, а протухший эталон хуже отсутствующего. Оба прогона делаются
# здесь и сейчас, из двух рабочих деревьев.
#
# Почему worktree, а не stash: stash трогает ваше рабочее состояние. Однажды
# это уже стоило потерянного вечера — GitHub Desktop застешил незакоммиченное
# посреди работы.

set -euo pipefail

if [ $# -lt 1 ]; then
    sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
fi

BASE_REF="$1"; shift
TICKS=4000
SEEDS="12345,424242,7"
PRESET="decisions"
HASH_EVERY=200

while [ $# -gt 0 ]; do
    case "$1" in
        --ticks)  TICKS="$2"; shift 2 ;;
        --seeds)  SEEDS="$2"; shift 2 ;;
        --preset) PRESET="$2"; shift 2 ;;
        --state-hash-every) HASH_EVERY="$2"; shift 2 ;;
        *) echo "Неизвестный аргумент: $1" >&2; exit 2 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="${TMPDIR:-/tmp}/hexlive-golden.$$"
BASE_TREE="$WORK_DIR/base"
OUT_DIR="$WORK_DIR/out"

cleanup() {
    # Worktree снимаем всегда, иначе следующий запуск упрётся в занятый путь.
    git -C "$REPO_ROOT" worktree remove --force "$BASE_TREE" 2>/dev/null || true
}
trap cleanup EXIT

mkdir -p "$OUT_DIR"

echo "база:    $BASE_REF"
echo "текущее: рабочее дерево $REPO_ROOT"
echo "сиды:    $SEEDS, тиков: $TICKS, пресет: $PRESET"
echo

echo "==> отдельное дерево на $BASE_REF"
git -C "$REPO_ROOT" worktree add --detach "$BASE_TREE" "$BASE_REF" >/dev/null 2>&1

# simdata ПЕРЕКРЫВАЕТ код: если экспорт между ревизиями разный, разойдутся и
# трассы — и это будет не про вашу правку. Предупредить, но не чинить молча.
if ! diff -q "$REPO_ROOT/SimData/simdata.json" "$BASE_TREE/SimData/simdata.json" >/dev/null 2>&1; then
    echo "⚠️  simdata.json РАЗНЫЙ в двух деревьях — часть диффа будет от тюнинга,"
    echo "    а не от кода. Сравнивай с оглядкой (спек §59.3)."
    echo
fi

run_side() {
    local label="$1" tree="$2"
    echo "==> сборка и прогон: $label"
    dotnet build "$tree/Tests/HexLive.Simulation.Soak/HexLive.Simulation.Soak.csproj" \
        -v q --nologo >/dev/null
    "$tree/Build/dotnet/bin/HexLive.Simulation.Soak/Debug/net9.0/hexsoak" \
        --seeds "$SEEDS" --ticks "$TICKS" \
        --simdata "$tree/SimData/simdata.json" \
        --trace-out "$OUT_DIR/$label.trace" \
        --trace-preset "$PRESET" \
        --state-hash-every "$HASH_EVERY" \
        --quiet
}

run_side base "$BASE_TREE"
run_side head "$REPO_ROOT"

echo
STATUS=0
IFS=',' read -ra SEED_LIST <<< "$SEEDS"
for seed in "${SEED_LIST[@]}"; do
    if [ "${#SEED_LIST[@]}" -gt 1 ]; then
        a="$OUT_DIR/base.trace.seed$seed"; b="$OUT_DIR/head.trace.seed$seed"
    else
        a="$OUT_DIR/base.trace"; b="$OUT_DIR/head.trace"
    fi

    if diff -q "$a" "$b" >/dev/null; then
        echo "сид $seed: совпало ($(wc -l < "$a" | tr -d ' ') строк)"
        continue
    fi

    STATUS=1
    echo "сид $seed: РАСХОЖДЕНИЕ"
    # Дифф пишется В ФАЙЛ, а не в `| head`: под `set -euo pipefail` голова
    # закрывает трубу, diff умирает по SIGPIPE, и скрипт молча обрывался на
    # ПЕРВОМ разошедшемся сиде — об остальных не узнать.
    diff "$a" "$b" > "$OUT_DIR/diff.seed$seed" || true
    echo "  строк разошлось: $(grep -c '^[<>]' "$OUT_DIR/diff.seed$seed" || true) из $(wc -l < "$a" | tr -d ' ')"
    echo "  первое отличие:"
    head -6 "$OUT_DIR/diff.seed$seed" | sed 's/^/    /'
    echo "  полный дифф: $OUT_DIR/diff.seed$seed"
done

echo
if [ "$STATUS" -eq 0 ]; then
    echo "✅ поведение не изменилось."
else
    echo "❌ поведение изменилось. Если это ожидаемо — опиши в spec.md."
    echo "   Если нет — ищи перестановку float-операций или порядок обхода."
    # Файлы нужны для разбора, поэтому не удаляем.
    trap - EXIT
    git -C "$REPO_ROOT" worktree remove --force "$BASE_TREE" 2>/dev/null || true
    echo "   Трассы: $OUT_DIR"
fi

exit "$STATUS"
