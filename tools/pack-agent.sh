#!/bin/sh
# Собирает storm-agent.exe в чистую папку, готовую к копированию на площадку.
#
# Почему не годится папка публикации напрямую: агент хранит свою личность и список
# сопряжений РЯДОМ С СОБОЙ — в этом и состоит его портативность. Стоит один раз
# запустить его оттуда, и там останутся ключ и чужие сопряжения; скопировав такую
# папку на площадку, вы увезли бы туда личность отладочного агента вместо новой.
set -e

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/agent"
pub="$root/src/StormMachine.Agent/bin/Release/net10.0-windows/win-x64/publish"

echo "Собираю агента..."
dotnet publish "$root/src/StormMachine.Agent/StormMachine.Agent.csproj" \
    --configuration Release --nologo --verbosity quiet

if [ ! -f "$pub/storm-agent.exe" ]; then
    echo "Не найден $pub/storm-agent.exe"
    exit 1
fi

rm -rf "$out"
mkdir -p "$out"

# Копируется ровно один файл. Остальное в папке публикации — отладочные символы
# и состояние от прошлых запусков: на площадке не нужно ни то, ни другое.
cp "$pub/storm-agent.exe" "$out/storm-agent.exe"

# Путь печатается в том виде, в каком его примут проводник и cmd: файл отсюда
# понесут копировать руками, а bash-путь вида /d/... там не откроется.
shown="$out/storm-agent.exe"
if command -v cygpath >/dev/null 2>&1; then
    shown="$(cygpath -w "$out/storm-agent.exe")"
fi

echo
echo "Готово: $shown"
echo "Размер: $(wc -c < "$out/storm-agent.exe") байт"
echo
echo "Скопируйте этот файл на вторую машину в любую папку и запустите там:"
echo "    storm-agent.exe listen --сопряжение"
echo
echo "Личность и сопряжения агент создаст сам рядом с собой при первом запуске."
