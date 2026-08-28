using Velopack;
using Velopack.Sources;

// Спайк-07. Переживает ли Velopack обрезку.
//
// Вопрос ровно тот же, что убил Quartz в спайке-06: библиотека, которая читает
// JSON рефлексией, после PublishTrimmed молчит и возвращает пустоту — без единого
// предупреждения на сборке. Проверять это надо на опубликованном бинарнике,
// а не на отладочном: отладочный не обрезан и скажет «всё хорошо».
//
// Запуск:
//   spike07 feed <каталог-с-релизами>   — прочитать ленту обновлений
//   spike07 check <каталог-с-релизами>  — полная проверка обновления

VelopackApp.Build().Run();

var mode = args.Length > 0 ? args[0] : "feed";
var directory = args.Length > 1 ? args[1] : "Releases";

Console.WriteLine($"spike07, версия {VelopackRuntimeInfo.VelopackNugetVersion}");
Console.WriteLine($"каталог релизов: {Path.GetFullPath(directory)}");
Console.WriteLine();

try
{
    if (mode == "feed")
    {
        // Прямая проверка разбора ленты: именно здесь ломается обрезка.
        var source = new SimpleFileSource(new DirectoryInfo(directory));
        var feed = await source.GetReleaseFeed(Velopack.Logging.NullVelopackLogger.Instance, "Spike07", "win").ConfigureAwait(false);

        Console.WriteLine($"Записей в ленте: {feed.Assets.Length}");

        foreach (var asset in feed.Assets)
        {
            Console.WriteLine($"  {asset.PackageId} {asset.Version} · {asset.Type} · {asset.FileName}");
        }

        Console.WriteLine(feed.Assets.Length == 0
            ? "ПУСТО — разбор ленты после обрезки не работает."
            : "Лента прочитана.");

        return feed.Assets.Length == 0 ? 1 : 0;
    }

    var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(directory)));

    Console.WriteLine($"установлено как приложение: {manager.IsInstalled}");
    Console.WriteLine($"текущая версия: {manager.CurrentVersion?.ToString() ?? "не определена"}");

    var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);

    Console.WriteLine(update is null
        ? "Обновлений нет."
        : $"Есть обновление: {update.TargetFullRelease.Version}");

    if (update is not null && mode == "download")
    {
        // Скачивание проверяет то, что обрезка ломает чаще всего после разбора JSON:
        // сверку контрольной суммы и применение разностного пакета.
        await manager.DownloadUpdatesAsync(update, p => Console.Write($"  {p}%")).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("Пакет скачан и собран. Применение делает Update.exe — он на Rust и обрезки не касается.");
    }

    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"ОШИБКА {ex.GetType().Name}: {ex.Message}");

    return 2;
}

