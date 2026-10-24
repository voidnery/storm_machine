# Security policy

Русская версия — ниже.

## Supported versions

Storm Machine is pre-1.0. Only the **latest published release** on the
[Releases](https://github.com/voidnery/storm_machine/releases) page receives fixes; there are
no maintenance branches for older alphas and betas.

## Reporting a vulnerability

Please report privately through GitHub: **Security → Report a vulnerability** on this
repository. Do not open a public issue, discussion or pull request for a vulnerability.

Include the version (Settings → About), what you did, what happened and, if you can, how to
reproduce it. Do not include real IP or MAC addresses, host names, SNMP community strings or
passwords from your network — describe their form instead ("a /24 subnet", "community: ***").

You will get an answer in the advisory thread. A fix ships as a new release; the advisory is
published together with it.

## What is in scope

- the agent protocol and pairing (TCP 47820, pairing codes, certificate pinning);
- storage of secrets — SNMP credentials and notification passwords are encrypted with DPAPI
  for the current Windows user ([docs/STATE.md](docs/STATE.md));
- the portable data folder and the Windows services `StormMonitor` and `StormAgent`;
- release integrity: archives are checked against `SHA256SUMS.txt`; release tags are signed.

Storm Machine is a diagnostic tool for networks you own or administer. Using its discovery
or capture against networks you have no permission to test is misuse, not a vulnerability.

---

# Политика безопасности

## Поддерживаемые версии

Продукт до 1.0. Исправления получает только **последний опубликованный выпуск** на странице
[Releases](https://github.com/voidnery/storm_machine/releases); веток сопровождения для
прошлых альф и бет нет.

## Как сообщить об уязвимости

Приватно через GitHub: **Security → Report a vulnerability** в этом репозитории. Публичные
issue, обсуждения и pull request для уязвимостей не открывайте.

Укажите версию («Настройки → О продукте»), что делали, что получили и, если можете, как
повторить. Реальных IP- и MAC-адресов, имён узлов, строк сообщества SNMP и паролей своей
сети не присылайте — пишите их форму («подсеть /24», «community: ***»).

Ответ придёт в обсуждении уведомления. Исправление выходит новым выпуском, уведомление
публикуется вместе с ним.

## Что входит в область

- протокол агента и сопряжение (TCP 47820, коды сопряжения, закрепление сертификата);
- хранение секретов — учётные данные SNMP и пароли оповещений зашифрованы DPAPI
  для текущего пользователя Windows ([docs/STATE.md](docs/STATE.md));
- переносимая папка данных и службы Windows `StormMonitor` и `StormAgent`;
- целостность выпусков: архивы сверяются с `SHA256SUMS.txt`, теги выпусков подписаны.

Storm Machine — инструмент диагностики сетей, которыми вы владеете или которые
администрируете. Обнаружение и захват в чужой сети без разрешения — злоупотребление,
а не уязвимость.
