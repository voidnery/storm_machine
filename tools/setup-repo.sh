#!/bin/sh
#
# Storm Machine — настройка репозитория: SSH-подпись коммитов и тегов + git-хуки.
#
# Почему sh, а не PowerShell: на машине действует групповая политика
# MachinePolicy = AllSigned, которая запрещает запуск неподписанных .ps1
# и не обходится ключом -ExecutionPolicy Bypass. Скрипты Git Bash под эту
# политику не подпадают.
#
# Почему SSH-подпись, а не GPG: сборка GnuPG 2.4 в составе Git for Windows
# обращается к keyboxd по пути /usr/lib/gnupg/keyboxd, которого на Windows
# не существует. SSH-подпись эту поломку обходит по построению.
#
# Запуск:  tools\setup-repo.cmd            (обычная настройка)
#          tools\setup-repo.cmd --verify   (только проверка)
#          tools\setup-repo.cmd --reuse-key net_gui_client_signing

set -e

KEY_NAME="storm_signing"
VERIFY=0

while [ $# -gt 0 ]; do
    case "$1" in
        --verify)     VERIFY=1; shift ;;
        --reuse-key)  KEY_NAME="$2"; shift 2 ;;
        --key-name)   KEY_NAME="$2"; shift 2 ;;
        *)            echo "Неизвестный параметр: $1"; exit 1 ;;
    esac
done

# Цвет — только если вывод идёт в терминал. Иначе в перехваченном выводе
# останутся управляющие последовательности вместо оформления.
if [ -t 1 ]; then
    G='\033[32m'; Y='\033[33m'; R='\033[31m'; C='\033[36m'; W='\033[37m'; N='\033[0m'
else
    G=''; Y=''; R=''; C=''; W=''; N=''
fi
ok()   { printf "  ${G}[ OK ]${N} %s\n" "$1"; }
warn() { printf "  ${Y}[ !  ]${N} %s\n" "$1"; }
bad()  { printf "  ${R}[ X  ]${N} %s\n" "$1"; }
say()  { printf "%s\n" "$1"; }

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
cd "$ROOT"

printf "\n${C}=== Storm Machine — настройка репозитория ===${N}\n"
say "Каталог: $ROOT"
say ""

# ------------------------------------------------------------------ 0. Git
GIT_VER=$(git --version | sed -n 's/.*version \([0-9]*\.[0-9]*\).*/\1/p')
GIT_MAJOR=${GIT_VER%%.*}
GIT_MINOR=${GIT_VER##*.}
if [ "$GIT_MAJOR" -lt 2 ] || { [ "$GIT_MAJOR" -eq 2 ] && [ "$GIT_MINOR" -lt 34 ]; }; then
    bad "Нужен Git >= 2.34 (SSH-подпись). Установлен: $GIT_VER"
    exit 1
fi
ok "Git $GIT_VER — SSH-подпись поддерживается"

SSH_DIR="$HOME/.ssh"
[ -d "$SSH_DIR" ] || mkdir -p "$SSH_DIR"

# ---------------------------------------------------------- 1. Репозиторий
if [ ! -d "$ROOT/.git" ]; then
    if [ "$VERIFY" = "1" ]; then bad "Репозиторий git не инициализирован"; exit 1; fi
    say ""
    printf "${Y}Репозиторий git ещё не создан. Создаю с веткой master...${N}\n"
    git init -b master >/dev/null
    ok "git init -b master"
else
    ok "Репозиторий git уже существует"
fi

# ---------------------------------------------------------------- 2. Ключ
PRIV="$SSH_DIR/$KEY_NAME"
PUB="$PRIV.pub"

if [ ! -f "$PUB" ]; then
    if [ "$VERIFY" = "1" ]; then bad "Ключ подписи не найден: $PUB"; exit 1; fi
    say ""
    printf "${Y}Создаю выделенный ключ подписи без пароля.${N}\n"
    say "Пароля нет намеренно: хук pre-push подписывает тег без участия человека,"
    say "а запрос пароля в GUI GitHub Desktop заблокировал бы push."
    say "Ключ используется ТОЛЬКО для подписи — доступа к серверам он не даёт."
    say ""
    ssh-keygen -t ed25519 -C "storm-machine-signing" -f "$PRIV" -N "" -q
    [ -f "$PUB" ] || { bad "Не удалось создать ключ"; exit 1; }
    ok "Создан ключ $KEY_NAME"
else
    ok "Ключ найден: $KEY_NAME"
fi

PUBKEY=$(cat "$PUB")

# Пути для git — в «смешанном» виде C:/..., чтобы их понимал и git.exe, и ssh-keygen
PUB_WIN=$(cygpath -m "$PUB" 2>/dev/null || echo "$PUB")

# --------------------------------------------------------- 3. Конфигурация
USER_EMAIL=$(git config --get user.email || true)
if [ -z "$USER_EMAIL" ]; then
    bad "Не задан git user.email. Настрой его в GitHub Desktop и повтори."
    exit 1
fi
ok "Автор коммитов: $(git config --get user.name) <$USER_EMAIL>"

if [ "$VERIFY" = "0" ]; then
    git config --local gpg.format ssh
    git config --local user.signingkey "$PUB_WIN"
    git config --local commit.gpgsign true
    git config --local tag.gpgsign true
    git config --local core.hooksPath .githooks
    ok "gpg.format = ssh (GnuPG не используется)"
    ok "Подпись коммитов и тегов включена"
    ok "core.hooksPath = .githooks"
fi

# --------------------------------------------- 4. allowed_signers
ALLOWED="$SSH_DIR/allowed_signers"
ALLOWED_WIN=$(cygpath -m "$ALLOWED" 2>/dev/null || echo "$ALLOWED")
KEY_BODY=$(echo "$PUBKEY" | awk '{print $1" "$2}')

if [ "$VERIFY" = "0" ]; then
    if [ -f "$ALLOWED" ]; then
        grep -v -F "$KEY_BODY" "$ALLOWED" > "$ALLOWED.tmp" 2>/dev/null || true
        mv "$ALLOWED.tmp" "$ALLOWED"
    fi
    printf '%s namespaces="git" %s\n' "$USER_EMAIL" "$KEY_BODY" >> "$ALLOWED"
    git config --local gpg.ssh.allowedSignersFile "$ALLOWED_WIN"
    ok 'allowed_signers обновлён — "git log --show-signature" проверяет подписи локально'
fi

# ------------------------------------------------------------- 5. Проверка
say ""
printf "${C}--- Проверка ---${N}\n"

FMT=$(git config --get gpg.format || true)
[ "$FMT" = "ssh" ] && ok "gpg.format = ssh" || bad "gpg.format = '$FMT' (ожидалось ssh)"

HOOKS=$(git config --get core.hooksPath || true)
[ "$HOOKS" = ".githooks" ] && ok "core.hooksPath = .githooks" || bad "core.hooksPath = '$HOOKS'"

HOOK="$ROOT/.githooks/pre-push"
if [ -f "$HOOK" ]; then
    if grep -q $'\r' "$HOOK"; then
        warn "В .githooks/pre-push найдены CRLF — Git Bash не сможет его запустить"
        if [ "$VERIFY" = "0" ]; then
            tr -d '\r' < "$HOOK" > "$HOOK.tmp" && mv "$HOOK.tmp" "$HOOK"
            ok "Переводы строк исправлены на LF"
        fi
    else
        ok "pre-push: переводы строк LF"
    fi
    chmod +x "$HOOK" 2>/dev/null || true
else
    bad "Не найден .githooks/pre-push"
fi

# Пробная подпись — главное доказательство, что всё сложилось
if [ "$VERIFY" = "0" ]; then
    PROBE="$(mktemp 2>/dev/null || echo /tmp/storm_probe)"
    echo "storm machine signing probe" > "$PROBE"
    if ssh-keygen -Y sign -f "$PRIV" -n git "$PROBE" >/dev/null 2>&1 && [ -f "$PROBE.sig" ]; then
        ok "Пробная подпись прошла — ключ рабочий, пароль не запрашивается"
        rm -f "$PROBE.sig"
    else
        bad "Пробная подпись не удалась"
    fi
    rm -f "$PROBE"
fi

# ------------------------------------------------- 6. Что делать дальше
say ""
printf "${C}=== ПУБЛИЧНЫЙ КЛЮЧ — добавь его на GitHub ===${N}\n"
say ""
printf "${W}%s${N}\n" "$PUBKEY"
say ""
printf "${Y}Куда: https://github.com/settings/ssh/new${N}\n"
printf "${Y}  Key type ОБЯЗАТЕЛЬНО: 'Signing Key', а не 'Authentication Key'${N}\n"
say "  Title: Storm Machine signing"
say ""
say "Без этого шага подпись будет ставиться, но GitHub не покажет 'Verified'."
say ""

if command -v clip.exe >/dev/null 2>&1; then
    printf '%s' "$PUBKEY" | clip.exe && ok "Публичный ключ скопирован в буфер обмена"
fi

say ""
printf "${C}Дальше — docs/GITHUB-SETUP.md, раздел 5.${N}\n"
say ""
