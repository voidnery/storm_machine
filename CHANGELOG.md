# Changelog

The release history of Storm Machine lives in one place — **[docs/RELEASES.md](docs/RELEASES.md)**,
newest first. This file is only a pointer: two copies of the same history would drift apart.

История выпусков ведётся в одном месте — **[docs/RELEASES.md](docs/RELEASES.md)**, новые записи
сверху. Этот файл — только указатель: две копии одной истории однажды разойдутся.

- **Версия** — SemVer, единственный источник — `<Version>` в
  [`Directory.Build.props`](Directory.Build.props). Отсюда её берут сборки (окно
  «О продукте», свойства файлов), имя переносимого архива и хук `.githooks/pre-push`,
  создающий подписанный тег `vX.Y.Z`. Файла `VERSION` нет намеренно: он был бы второй копией.
- **Раздел версии обязателен.** У текущей `<Version>` должен быть раздел `## X.Y.Z…`
  в `docs/RELEASES.md`: это проверяют архитектурные тесты (`ReleaseNotesTests`), а конвейер
  выпуска без раздела — и с незаполненными местами `[[ВПИСАТЬ: …]]` — не собирает черновик.
  Это то же требование, что стандарт репозиториев Eterix (ERS/1 §4) ставит к `CHANGELOG.md`.
