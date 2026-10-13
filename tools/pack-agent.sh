#!/bin/sh
# Собирает агентов под все платформы и кладёт их в поставку клиента.
#
# Агенты едут ВНУТРИ продукта, а не скачиваются по требованию: станцию носят
# на объекты, где интернета нет, а агент нужен именно там. Цена — размер:
# пять платформ в сжатом виде стоят около 33 МБ вместо 80.
#
# Windows-агент кладётся ещё и распакованным, рядом с клиентом: им пользуются
# прямо на этой машине, и доставать его из архива ради запуска незачем.
#
# Почему агент хранится сжатым: комплект для чужого конца собирает сам клиент
# («Настройки → Агенты → Собрать комплект»), и ему всё равно, откуда брать байты.
set -e

root="$(cd "$(dirname "$0")/.." && pwd)"
dest="${1:-$root/artifacts/StormMachine.App}"
platforms="${2:-win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64}"

staging="$root/artifacts/agents-staging"
agents="$dest/agents"

rm -rf "$staging"
mkdir -p "$agents"

for rid in $platforms; do
    echo "Собираю агента для $rid..."

    out="$staging/$rid"
    dotnet publish "$root/src/StormMachine.Agent/StormMachine.Agent.csproj" \
        --configuration Release --runtime "$rid" --output "$out" --nologo --verbosity quiet

    binary="$out/storm-agent"
    [ -f "$binary.exe" ] && binary="$binary.exe"

    if [ ! -f "$binary" ]; then
        echo "Для $rid не нашлось собранного агента в $out"
        exit 1
    fi

    # Сжатие делается встроенной командой PowerShell, а не zip: на машине сборки
    # его может не быть, а PowerShell есть везде, где есть Windows. Вызов идёт
    # командой, а не файлом сценария: политика выполнения на рабочей машине
    # запрещает запуск неподписанных .ps1, и скрипт-упаковщик туда не годится.
    zip="$agents/storm-agent-$rid.zip"
    rm -f "$zip"
    powershell -NoProfile -Command \
        "Compress-Archive -Path '$(cygpath -w "$binary")' -DestinationPath '$(cygpath -w "$zip")' -CompressionLevel Optimal -Force"

    echo "  $(wc -c < "$binary") байт -> $(wc -c < "$zip") байт"

    # Распакованный — только для своей платформы.
    if [ "$rid" = "win-x64" ]; then
        cp "$binary" "$dest/storm-agent.exe"
    fi
done

rm -rf "$staging"

echo
echo "Готово: $agents"
echo "Комплект для площадки собирается из клиента: «Настройки → Агенты → Собрать комплект»."
echo "Внутри комплекта — агент, написанные команды и (для Linux) юнит systemd."
