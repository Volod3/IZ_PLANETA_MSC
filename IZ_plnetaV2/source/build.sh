#!/usr/bin/env bash
# Збірка PlanetaOR.dll для My Summer Car (MSCLoader, Unity / Mono profile).
#
# Потрібно:  sudo apt install mono-mcs      (Kubuntu/Ubuntu)
# Запуск:    ./build.sh
#            MSC_DIR="/шлях/до/My Summer Car" ./build.sh
#            SRC_DIR="/шлях/до/вихідників"    ./build.sh   # за замовчуванням - папка скрипта
#            INSTALL=1 ./build.sh                          # ще й скопіювати в гру

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="${SRC_DIR:-$HERE}"
MSC_DIR="${MSC_DIR:-$HOME/.steam/debian-installation/steamapps/common/My Summer Car}"
MANAGED="$MSC_DIR/mysummercar_Data/Managed"
OUT="$HERE/PlanetaOR.dll"

command -v mcs >/dev/null || { echo "mcs не знайдено: sudo apt install mono-mcs"; exit 1; }
[ -d "$MANAGED" ] || { echo "Нема $MANAGED"; echo "Вкажи шлях: MSC_DIR=\"/шлях/до/My Summer Car\" ./build.sh"; exit 1; }

_find_dll() {
    local name="$1"
    # 1. Спочатку в Managed
    local f="$MANAGED/$name.dll"
    [ -f "$f" ] && { echo "$f"; return; }
    # 2. Рекурсивно по всій папці гри
    f="$(find "$MSC_DIR" -iname "$name.dll" -print -quit 2>/dev/null || true)"
    [ -n "$f" ] && [ -f "$f" ] && { echo "$f"; return; }
    # 3. Рекурсивно по домашній папці (для сторонніх dll типу WreckMP_Ref)
    f="$(find "$HOME" -iname "$name.dll" -print -quit 2>/dev/null || true)"
    [ -n "$f" ] && [ -f "$f" ] && { echo "$f"; return; }
    echo ""
}

REFS=()
for name in mscorlib System System.Core UnityEngine Assembly-CSharp PlayMaker MSCLoader WreckMP_Ref; do
    if [ -n "${WRECKMP_REF:-}" ] && [ "$name" = "WreckMP_Ref" ]; then
        f="$WRECKMP_REF"
    else
        f="$(_find_dll "$name")"
    fi
    if [ -z "$f" ] || [ ! -f "$f" ]; then
        echo "Не знайдено $name.dll"
        exit 1
    fi
    echo "ref: $f"
    REFS+=("-r:$f")
done

# Збираємо .cs; nullglob - щоб порожній збіг давав порожній масив, а не буквальний шаблон
shopt -s nullglob
SOURCES=("$SRC_DIR"/*.cs)
shopt -u nullglob

if [ ${#SOURCES[@]} -eq 0 ]; then
    echo "Помилка: у $SRC_DIR немає жодного .cs файлу!"
    exit 1
fi
echo "Джерела (${#SOURCES[@]}) з: $SRC_DIR"

# -nostdlib: використовуємо mscorlib із гри, не системний
# -langversion:7: безпечно для Mono mcs (без C# 8+ фіч)
mcs -nostdlib -langversion:7 -target:library -optimize+ -debug- \
    -out:"$OUT" "${REFS[@]}" "${SOURCES[@]}"
echo "OK -> $OUT"

if [ "${INSTALL:-0}" = "1" ]; then
    mkdir -p "$MSC_DIR/Mods/Assets/Planeta"
    cp -v "$OUT" "$MSC_DIR/Mods/PlanetaOR.dll"
    if [ -f "$SRC_DIR/planeta5" ]; then
        cp -v "$SRC_DIR/planeta5" "$MSC_DIR/Mods/Assets/Planeta/planeta5"
    else
        echo "Увага: файл planeta5 не знайдено в $SRC_DIR (бандл не скопійовано)"
    fi
    echo "Встановлено в гру!"
fi
