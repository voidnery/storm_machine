#!/bin/sh
# Собирает переносимую версию продукта: папку, которую можно скопировать на чужую
# машину и запустить, и архив этой же папки — чтобы было что отправить.
#
# Переносимая версия отличается от установленной одним файлом: маркером
# «storm.portable» рядом с программой. По нему продукт кладёт базу измерений,
# журнал и настройки туда же, а не в профиль пользователя. Данные едут
# с продуктом, на чужой машине не остаётся ничего.
#
# Обновлений в ней нет и быть не может: папку везут с собой, и подменять её
# на ходу нельзя. Клиент это знает и прячет настройки обновления.
#
# Запуск:  tools\pack-portable.cmd
#          tools\pack-portable.cmd --без-агентов     (быстрее на минуту)
set -e

root="$(cd "$(dirname "$0")/.." && pwd)"
agents=1

for arg in "$@"; do
    case "$arg" in
        --без-агентов|--no-agents) agents=0 ;;
        *) echo "Неизвестный ключ: $arg"; exit 2 ;;
    esac
done

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1 | tr -d ' \r')
[ -n "$version" ] || { echo "В Directory.Build.props нет <Version>"; exit 1; }

name="StormMachine-$version-portable"
out="$root/artifacts/portable"
dest="$out/$name"
archive="$out/$name.zip"

echo "Переносимая версия $version"
echo "Папка  : $dest"
echo "Архив  : $archive"
echo ""

# Начисто. Частичная публикация поверх прошлой — это файлы двух версий в одной
# папке: incremental publish сверяет входы, а не результат, и лишнее не убирает.
rm -rf "$dest"
mkdir -p "$dest"

echo "Клиент..."
dotnet publish "$root/src/StormMachine.App/StormMachine.App.csproj" \
    --configuration Release --output "$dest" --nologo --verbosity quiet

echo "Консоль..."
dotnet publish "$root/src/StormMachine.Cli/StormMachine.Cli.csproj" \
    --configuration Release --output "$dest" --nologo --verbosity quiet

if [ "$agents" = "1" ]; then
    echo "Агенты для чужих концов..."
    sh "$root/tools/pack-agent.sh" "$dest" >/dev/null
fi

# Отладочные символы в переносимый архив не кладутся: на чужой машине они никому
# не нужны, а весят больше самой программы.
find "$dest" -name '*.pdb' -delete

# Маркер и есть вся портативность. Текст внутри — для того, кто откроет файл,
# гадая, можно ли его удалить.
cat >"$dest/storm.portable" <<'МАРКЕР'
Этот файл делает копию продукта переносимой.

Пока он лежит рядом с программой, Storm Machine хранит базу измерений,
настройки и журнал в этой же папке, а не в профиле пользователя. Папку можно
носить на флешке: история измерений едет вместе с ней, а на чужой машине
не остаётся ничего.

Удалите файл — и продукт начнёт хранить своё в профиле пользователя,
как обычная установленная версия.
МАРКЕР

echo ""
echo "Архив..."
rm -f "$archive"

# Сжатие встроенной командой PowerShell, а не zip: на машине сборки zip может
# не быть, а PowerShell есть везде, где есть Windows. Вызов командой, а не файлом
# сценария: политика выполнения запрещает запуск неподписанных .ps1.
powershell -NoProfile -Command \
    "Compress-Archive -Path '$(cygpath -w "$dest")\\*' -DestinationPath '$(cygpath -w "$archive")' -CompressionLevel Optimal -Force"

size=$(wc -c < "$archive")

echo ""
echo "Готово."
echo "  папка : $dest"
echo "  архив : $archive  ($((size / 1048576)) МБ)"
echo ""
echo "На чужой машине: распаковать и запустить StormMachine.exe."
echo "Захват кадров (соседи, чужой DHCP) требует прав администратора —"
echo "запускать через «Запуск от имени администратора»."
