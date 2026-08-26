# Публикация и релизы через GitHub Desktop — пояснительная записка

**Для кого:** оператор проекта
**Что настроено:** две ветки, автоматические подписанные теги версий, CI, релизы
**Время на первичную настройку:** ~15 минут

---

## 1. Что здесь происходит в двух словах

| Ветка | Назначение |
|-------|-----------|
| **`beta`** | Рабочая. Сюда идут все итерации разработки. Теги не создаются |
| **`master`** | Релизная. Каждая переливка сюда = новая версия с **подписанным тегом** |

**Механизм релиза:** поднимаешь версию в одном файле, сливаешь `beta` в `master`
локально через GitHub Desktop, нажимаешь Push. В этот момент git-хук автоматически
создаёт подписанный тег `vX.Y.Z` и отправляет его на GitHub. GitHub Actions подхватывает
тег и собирает черновик релиза с бинарниками.

Ты контролируешь ровно две вещи: **когда поднять версию** и **когда слить в master**.
Всё остальное происходит само.

---

## 2. Почему подпись SSH, а не GPG

Ты правильно указал на поломку: сборка **GnuPG 2.4 в составе Git for Windows** пытается
работать через `keyboxd`, но сам бинарник в поставку не входит — отсюда обращение
к несуществующему на Windows пути `/usr/lib/gnupg/keyboxd`, и подпись падает с ошибками
вида `gpg: can't connect to the keyboxd`.

**Мы эту проблему не обходим — мы её исключаем.** Подпись делается SSH-ключом:

```
gpg.format = ssh
```

Git 2.34+ подписывает коммиты и теги SSH-ключами, GitHub такие подписи проверяет
и показывает значок **Verified**. GnuPG в этой схеме не участвует вообще.

Проверено на твоей машине: `gpg` в системе отсутствует, `%APPDATA%\gnupg\common.conf`
нет, git версии 2.53. Всё сходится — поломке просто негде проявиться.

**Побочная выгода:** нет keyring, нет агента с паролем, нет истечения ключей.

Если когда-нибудь понадобится именно GPG — см. §10.

---

## 3. Почему инструменты — `.cmd` и `sh`, а не PowerShell

При проверке окружения выяснилось ещё одно ограничение, которое сломало бы всё
на первом же шаге:

```
        Scope ExecutionPolicy
        ----- ---------------
MachinePolicy       AllSigned     ← групповая политика
  CurrentUser    Unrestricted
 LocalMachine    Unrestricted
```

**`MachinePolicy = AllSigned`** задана групповой политикой домена и перекрывает все
остальные области, включая ключ `-ExecutionPolicy Bypass`. Любой неподписанный
`.ps1` на этой машине запустить нельзя:

```
File tools\setup-repo.ps1 cannot be loaded. The file is not digitally signed.
```

Поэтому инструменты написаны как скрипты **Git Bash** (`.sh`) с тонкими обёртками
**`.cmd`**. На пакетные файлы и на скрипты Git Bash политика выполнения PowerShell
не распространяется. Побочная выгода: `.sh` — та же среда, в которой работает
git-хук `pre-push`, то есть одна технология вместо двух.

> **Отдельная ловушка.** Команда `bash` в PATH на этой машине указывает
> на `C:\Windows\System32\bash.exe` — это **WSL**, совсем другая среда.
> Обёртки `.cmd` находят именно `bash.exe` из состава Git for Windows
> (`C:\Program Files\Git\bin\bash.exe`), проверяя его рядом с реально используемым
> `git.exe`. Не запускай `.sh` через `bash` из PATH.

---

## 4. Шаг 1 — настройка подписи (делается один раз)

### 4.1 Запусти скрипт

Открой обычную командную строку (`cmd`) в папке проекта и выполни:

```bash
tools\setup-repo.cmd
```

Скрипт сделает:

1. Инициализирует git-репозиторий с веткой `master`, если его ещё нет.
2. Создаст выделенный ключ подписи `~/.ssh/storm_signing` **без пароля**.
3. Настроит подпись коммитов и тегов — **только для этого репозитория**,
   глобальные настройки не трогаются.
4. Включит версионируемые хуки: `core.hooksPath = .githooks`.
5. Пропишет `allowed_signers`, чтобы подписи проверялись и локально.
6. Сделает пробную подпись и покажет, всё ли сложилось.
7. Скопирует публичный ключ в буфер обмена.

> **Почему ключ без пароля.** Хук подписывает тег автоматически в момент push.
> Ключ с паролем вызвал бы невидимый запрос в GUI GitHub Desktop, и push бы завис.
> Ключ используется **только для подписи** — доступа к серверам он не даёт,
> в `authorized_keys` не добавляется. Обычная практика для ключей автоматизации.
>
> Хочешь использовать существующий ключ (у тебя есть `id_ed25519`
> и `net_gui_client_signing`):
> ```bash
> tools\setup-repo.cmd --reuse-key net_gui_client_signing
> ```

### 4.2 Добавь ключ на GitHub

1. Открой https://github.com/settings/ssh/new
2. **Key type: `Signing Key`** ← это принципиально. Не `Authentication Key`.
3. Title: `Storm Machine signing`
4. Key: вставь из буфера (скрипт уже скопировал) → **Add SSH key**

> Хочешь тем же ключом ещё и авторизоваться — добавь его **вторым** ключом
> с типом `Authentication Key`. GitHub хранит их раздельно.

Без этого шага подпись будет ставиться, но GitHub не покажет **Verified**.

---

## 5. Шаг 2 — первая публикация и ветки

### 5.1 Добавь репозиторий в GitHub Desktop

1. **File → Add local repository…**
2. Укажи `D:\Temp_Danya\Projects\storm_machine`
3. GitHub Desktop увидит репозиторий, созданный скриптом из §4

### 5.2 Первый коммит

1. В левой панели будут все файлы проекта
2. Summary: `chore: инфраструктура репозитория, документы этапов 1-5`
3. **Commit to master**

Коммит будет подписан автоматически.

### 5.3 Публикация

1. **Publish repository**
2. Name: `storm-machine`
3. Description: `Network testing and diagnostics workstation for Windows`
4. **Сними галочку `Keep this code private`** — репозиторий должен быть публичным
   (условие бесплатной подписи SignPath Foundation и набора репутации SmartScreen)
5. **Publish repository**

Ветка `master` станет веткой по умолчанию на GitHub. Так и задумано: Pull Request'ы
из `beta` будут по умолчанию нацелены на неё.

> Тег на этом шаге **не создастся**: версия в `Directory.Build.props` сейчас `0.0.0`,
> а это признак предрелизного состояния. Хук такие версии сознательно пропускает.

### 5.4 Создай ветку beta

1. **Current Branch → New Branch**
2. Name: `beta`, Base: `master` → **Create branch**
3. **Publish branch**

Дальше вся работа идёт в `beta`.

---

## 6. Шаг 3 — работа и релиз

### 6.1 Обычная работа

Работаем в `beta`. Коммиты и push — как обычно, через GitHub Desktop.
Теги не создаются, релизы не собираются. Ничего специального делать не нужно.

### 6.2 Релиз: переливка beta → master

**Шаг 1. Подними версию** (находясь в ветке `beta`):

```bash
tools\release.cmd --version 0.1.0
```

**Шаг 2. Закоммить это в `beta`:**
Summary `release: version 0.1.0` → **Commit to beta** → **Push origin**

**Шаг 3. Переключись на master:** **Current Branch → master**

**Шаг 4. Влей beta:** **Branch → Merge into current branch… → beta**

**Шаг 5. Нажми Push origin.**

В этот момент срабатывает хук: читает версию `0.1.0`, создаёт **подписанный
аннотированный тег `v0.1.0`** и отправляет его. GitHub Actions видит тег и собирает
черновик релиза с бинарниками и контрольными суммами.

### 6.3 ⚠️ Критично: почему merge делается локально, а не на GitHub

Если слить `beta` в `master` **кнопкой Merge на сайте GitHub**, push произойдёт
на стороне GitHub — **локальный хук не сработает, и тег не будет создан.**

Поэтому канонический механизм переливки — **локальный merge в GitHub Desktop**
(шаги 3–5 выше).

Если хочется предварительно посмотреть изменения как Pull Request:

1. В `beta` нажми **Preview Pull Request** или **Create Pull Request** — откроется браузер
2. Посмотри diff, пройди чек-лист из шаблона PR
3. **Не нажимай Merge на сайте.** Вернись в GitHub Desktop и сделай шаги 3–5
4. GitHub сам закроет Pull Request как merged, увидев коммиты в `master`

Так ты получаешь и обзор изменений, и рабочую подпись тега.

### 6.4 Первый релиз сделай из командной строки

GitHub Desktop показывает вывод хуков не всегда заметно. Чтобы своими глазами увидеть,
что подпись сработала, самый первый релизный push сделай так:

1. **Repository → Open in Command Prompt**
2. Выполни:

```bash
git push origin master
```

Ожидаемый вывод:

```
pre-push: создаю подписанный тег v0.1.0
pre-push: тег v0.1.0 подписан и опубликован.
```

Дальше можно спокойно пользоваться кнопкой Push в GitHub Desktop.

---

## 7. Шаг 4 — проверка, что подпись работает

**Локально:**

```bash
git tag -v v0.1.0
```

Ожидаемо: `Good "git" signature for <твой email>`

**История коммитов с подписями:**

```bash
git log --show-signature -3
```

**На GitHub:** страница репозитория → **Releases** или **Tags**. Рядом с тегом
и коммитами должен быть зелёный значок **Verified**. Нет значка — ключ не добавлен
как `Signing Key` (см. §4.2).

---

## 8. Защита ветки master (рекомендуется, 2 минуты)

1. Репозиторий на GitHub → **Settings → Rules → Rulesets → New branch ruleset**
2. Name: `master protection`, Enforcement status: **Active**
3. Target branches → **Add target** → **Include by pattern** → `master`
4. Отметь:
   - **Restrict deletions**
   - **Block force pushes**
5. **Create**

> **Не включай** «Require a pull request before merging». Это правило запретит
> локальный push в `master` и сломает механизм подписи тегов из §6.

---

## 9. Если что-то пошло не так

| Симптом | Причина | Что делать |
|---------|---------|-----------|
| Push прошёл, тега нет | Версия `0.0.0` | Это норма. `tools\release.cmd --version 0.1.0` |
| `тег vX.Y.Z уже существует` | Версия не поднята с прошлого релиза | Подними версию |
| `ВНИМАНИЕ: подпись не настроена` | Не запускался `setup-repo.cmd` | `tools\setup-repo.cmd` из корня проекта |
| `Git Bash not found` | Git for Windows не установлен или установлен нестандартно | Поставь с git-scm.com/download/win |
| Скрипт падает со странными ошибками синтаксиса | Запустил `.sh` через `bash` из PATH, а это WSL | Запускай только через `tools\*.cmd` |
| Хук вообще не запускается | Не задан `core.hooksPath` | `git config --local core.hooksPath .githooks` |
| Хук не запускается, путь задан | В `.githooks/pre-push` попали CRLF | `tools\setup-repo.cmd --verify` — обнаружит и исправит |
| Тег создан, но push отклонён | Кто-то обновил `master` | `git push --delete origin vX.Y.Z`, `git tag -d vX.Y.Z`, `git pull`, повтори |
| Нет значка **Verified** | Ключ не добавлен как `Signing Key` | §4.2. Тип именно `Signing`, не `Authentication` |
| GitHub Desktop не коммитит: ошибка подписи | Ключ требует пароль | Удали `~/.ssh/storm_signing*` и запусти `tools\setup-repo.cmd` заново |
| Релиз собрался, но он черновик | Так задумано | GitHub → **Releases** → открой черновик, проверь → **Publish release** |

**Полная перенастройка подписи:**

```bash
git config --local --unset gpg.format & git config --local --unset user.signingkey & tools\setup-repo.cmd
```

---

## 10. Если всё-таки понадобится GPG

**Причина поломки.** Git for Windows поставляется с GnuPG 2.4, который при наличии
`use-keyboxd` в `%APPDATA%\gnupg\common.conf` пытается запустить демон `keyboxd`.
Бинарник демона в поставку Git for Windows не включён, и GnuPG обращается
к `/usr/lib/gnupg/keyboxd` — пути, которого на Windows нет.

**Три обхода, от лучшего к худшему:**

1. **Не использовать GnuPG** — то, что мы и сделали. SSH-подпись.
2. **Поставить полноценный Gpg4win** и увести git на него:
   ```bash
   git config --global gpg.program "C:/Program Files (x86)/GnuPG/bin/gpg.exe"
   ```
   У Gpg4win свой keyboxd на месте, и путь `/usr/lib/gnupg/` не используется.
3. **Отключить keyboxd**: в `%APPDATA%\gnupg\common.conf` удалить (или закомментировать `#`)
   строку `use-keyboxd`. Ключи должны лежать в `pubring.kbx` или `private-keys-v1.d`.
   После правки — `gpgconf --kill all`.

---

## 11. Шпаргалка

| Задача | Команда / действие |
|--------|--------------------|
| Настроить подпись (один раз) | `tools\setup-repo.cmd` |
| Проверить настройку | `tools\setup-repo.cmd --verify` |
| Использовать существующий ключ | `tools\setup-repo.cmd --reuse-key <имя>` |
| Посмотреть состояние релиза | `tools\release.cmd --check` |
| Поднять версию | `tools\release.cmd --version 0.2.0` |
| Обычная работа | Коммиты в `beta` через GitHub Desktop |
| Релиз | `master` → **Branch → Merge into current branch → beta** → **Push origin** |
| Проверить подпись тега | `git tag -v v0.1.0` |
| Отменить ошибочный тег | `git push --delete origin v0.1.0` затем `git tag -d v0.1.0` |

---

## 12. Что настроено в файлах

| Файл | Назначение |
|------|-----------|
| `Directory.Build.props` | **Единственный источник версии** + детерминированная сборка |
| `.githooks/pre-push` | Автоматический подписанный тег при push в `master` |
| `tools/setup-repo.cmd` + `.sh` | Настройка SSH-подписи и хуков |
| `tools/release.cmd` + `.sh` | Смена версии и подсказка по шагам релиза |
| `.github/workflows/ci.yml` | Сборка и тесты на `windows-latest` при push и PR |
| `.github/workflows/release.yml` | Сборка бинарников и черновик релиза по тегу `v*` |
| `.github/pull_request_template.md` | Чек-лист ритуала закрытия итерации |
| `.gitattributes` | LF в `.sh` и хуках, CRLF в `.cmd` — иначе они не запустятся |
| `LICENSE` | MIT — условие бесплатной подписи SignPath Foundation |
