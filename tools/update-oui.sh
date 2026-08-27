#!/bin/sh
# Собирает встроенную базу «префикс MAC → вендор» из реестров IEEE.
#
# База встроена в поставку сознательно: вендор по MAC входит в уровень 0 —
# те самые 80% ценности без прав администратора и драйверов. Требовать ради него
# ручных действий значило бы сломать сценарий «первый запуск за минуту».
# Реестр IEEE публичный, и его распространение не ограничено — в отличие
# от Npcap и DB-IP, которые в поставку не входят и входить не могут.
#
# Запускать при обновлении базы: tools/update-oui.cmd
set -e

root=$(cd "$(dirname "$0")/.." && pwd)
out="$root/src/StormMachine.Discovery/Resources"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

echo "Каталог назначения: $out"
mkdir -p "$out"

fetch() {
    echo "  $2"
    curl -sS -L --max-time 300 -o "$tmp/$2" "$1"
}

echo "Загрузка реестров IEEE:"
fetch "https://standards-oui.ieee.org/oui/oui.csv"    "ma-l.csv"
fetch "https://standards-oui.ieee.org/oui28/mam.csv"  "ma-m.csv"
fetch "https://standards-oui.ieee.org/oui36/oui36.csv" "ma-s.csv"

python "$root/tools/build-oui.py" "$tmp" "$out/oui.tsv.gz"

echo
echo "Готово. Не забудьте пересобрать решение — файл встраивается как ресурс."
