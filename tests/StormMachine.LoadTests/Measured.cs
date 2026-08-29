using System.Diagnostics;
using System.Globalization;

namespace StormMachine.LoadTests;

/// <summary>
/// Замер одного нагрузочного шага.
/// </summary>
/// <remarks>
/// Числа здесь важнее вердикта. Порог отвечает на вопрос «не сломалось ли», а число —
/// на вопрос «куда оно движется», и второй вопрос для нагрузки главный: рост втрое
/// за год виден только при сравнении с прошлым замером. Поэтому всё измеренное пишется
/// в протокол, а не только то, что не уложилось.
/// </remarks>
internal static class Measured
{
    private static readonly List<string> Log = [];

    public static (T Result, TimeSpan Elapsed, long AllocatedBytes) Run<T>(string what, Func<T> action)
    {
        // Полная сборка до замера: иначе в расход попадёт мусор предыдущего шага.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        var result = action();

        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report(what, watch.Elapsed, allocated);

        return (result, watch.Elapsed, allocated);
    }

    public static async Task<(T Result, TimeSpan Elapsed, long AllocatedBytes)> RunAsync<T>(
        string what,
        Func<Task<T>> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        var result = await action().ConfigureAwait(false);

        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report(what, watch.Elapsed, allocated);

        return (result, watch.Elapsed, allocated);
    }

    public static void Note(string line)
    {
        lock (Log)
        {
            Log.Add(line);
        }

        Console.WriteLine(line);
    }

    private static void Report(string what, TimeSpan elapsed, long allocated) =>
        Note($"  {what,-52} {elapsed.TotalMilliseconds,10:N0} мс  {Megabytes(allocated),9} МБ");

    public static string Megabytes(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("N1", CultureInfo.InvariantCulture);

    private static readonly HashSet<string> Started = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Складывает протокол замеров в artifacts рядом с протоколами приёмки.
    /// </summary>
    /// <remarks>
    /// Дописывает, а не перезаписывает: xunit создаёт экземпляр класса проверок
    /// на каждый метод, и вызовов сюда будет столько же, сколько проверок. Первый
    /// в процессе начинает файл заново, остальные дописывают — иначе в протоколе
    /// осталась бы только последняя проверка, как и вышло при первом запуске.
    /// </remarks>
    public static void Save(string name)
    {
        string[] lines;
        bool first;

        lock (Log)
        {
            if (Log.Count == 0)
            {
                return;
            }

            lines = [.. Log];
            Log.Clear();
            first = Started.Add(name);
        }

        var root = FindRepositoryRoot();

        if (root is null)
        {
            return;
        }

        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);

        var path = Path.Combine(artifacts, name);

        if (first)
        {
            File.WriteAllLines(path, lines);

            return;
        }

        File.AppendAllLines(path, lines);
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StormMachine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
