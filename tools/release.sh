#!/bin/sh
#
# Storm Machine — подготовка релиза: смена версии перед переливкой beta -> master.
#
# Версия хранится в одном месте — <Version> в Directory.Build.props.
# После переливки в master хук .githooks/pre-push создаст подписанный тег vX.Y.Z.
#
# Запуск:  tools\release.cmd --check
#          tools\release.cmd --version 0.1.0

set -e

VERSION=""
CHECK=0

while [ $# -gt 0 ]; do
    case "$1" in
        --check)   CHECK=1; shift ;;
        --version) VERSION="$2"; shift 2 ;;
        *)         echo "Неизвестный параметр: $1"; exit 1 ;;
    esac
done

if [ -t 1 ]; then
    G='\033[32m'; Y='\033[33m'; R='\033[31m'; C='\033[36m'; W='\033[37m'; N='\033[0m'
else
    G=''; Y=''; R=''; C=''; W=''; N=''
fi
say() { printf "%s\n" "$1"; }

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
cd "$ROOT"

PROPS="$ROOT/Directory.Build.props"
[ -f "$PROPS" ] || { printf "${R}Не найден Directory.Build.props${N}\n"; exit 1; }

CURRENT=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROPS" | head -n 1 | tr -d ' \r')
[ -n "$CURRENT" ] || { printf "${R}В Directory.Build.props нет <Version>${N}\n"; exit 1; }

# --------------------------------------------------------------- Состояние
if [ "$CHECK" = "1" ] || [ -z "$VERSION" ]; then
    say ""
    printf "${C}=== Состояние релиза ===${N}\n"
    printf "${W}Текущая версия в Directory.Build.props : %s${N}\n" "$CURRENT"

    if [ -d "$ROOT/.git" ]; then
        if [ -n "$(git tag -l)" ]; then
            LAST_TAG=$(git describe --tags --abbrev=0)
        else
            LAST_TAG="(тегов ещё нет)"
        fi
        printf "${W}Последний тег                          : %s${N}\n" "$LAST_TAG"

        BRANCH=$(git symbolic-ref --short HEAD 2>/dev/null || echo "(не определена)")
        printf "${W}Текущая ветка                          : %s${N}\n" "$BRANCH"

        if [ -n "$(git status --porcelain)" ]; then
            printf "${Y}Рабочая копия                          : есть незакоммиченные изменения${N}\n"
        else
            printf "${G}Рабочая копия                          : чисто${N}\n"
        fi
    else
        printf "${Y}Репозиторий git ещё не создан${N}\n"
    fi

    if [ "$CURRENT" = "0.0.0" ]; then
        say ""
        printf "${Y}Версия 0.0.0 — предрелизная. Тег при пуше в master создаваться не будет.${N}\n"
        printf "${Y}Для первого релиза:  tools\\\\release.cmd --version 0.1.0${N}\n"
    fi
    say ""
    [ -z "$VERSION" ] && exit 0
fi

# ------------------------------------------------------------ Смена версии
if ! echo "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'; then
    printf "${R}Некорректная версия '%s'. Ожидается X.Y.Z или X.Y.Z-suffix${N}\n" "$VERSION"
    exit 1
fi

if [ "$VERSION" = "$CURRENT" ]; then
    printf "${Y}Версия уже равна %s — менять нечего${N}\n" "$VERSION"
    exit 0
fi

if [ -d "$ROOT/.git" ] && [ -n "$(git tag -l "v$VERSION")" ]; then
    printf "${R}Тег v%s уже существует. Выбери другую версию.${N}\n" "$VERSION"
    exit 1
fi

sed -i "s:<Version>$CURRENT</Version>:<Version>$VERSION</Version>:" "$PROPS"

NEW=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROPS" | head -n 1 | tr -d ' \r')
[ "$NEW" = "$VERSION" ] || { printf "${R}Не удалось изменить версию${N}\n"; exit 1; }

say ""
printf "${G}Версия изменена: %s -> %s${N}\n" "$CURRENT" "$VERSION"
say ""
printf "${C}Дальше:${N}\n"
printf "${W}  1. GitHub Desktop: закоммить изменение в ветку beta${N}\n"
say "     (сообщение, например: release: version $VERSION)"
printf "${W}  2. Push origin — изменение уходит в beta, тег НЕ создаётся${N}\n"
printf "${W}  3. Current Branch -> master${N}\n"
printf "${W}  4. Branch -> Merge into current branch... -> beta${N}\n"
printf "${W}  5. Push origin — хук создаст подписанный тег v%s и отправит его${N}\n" "$VERSION"
say ""
say "Подробности: docs/GITHUB-SETUP.md, раздел 6"
say ""
