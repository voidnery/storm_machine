namespace StormMachine.ArchTests;

/// <summary>
/// Готовность слоя приложения к работе за сетевым API.
/// </summary>
/// <remarks>
/// Обязательство И-19 из плана: сможет ли <c>Application</c> работать за сетевым API
/// без правок домена. Вопрос не праздный — вариант <c>server</c> (сервер плюс
/// web-панель) записан в замысел продукта с самого начала, и ядро всё это время
/// проектировалось «не знающим о GUI» именно ради него.
/// <para>
/// «Не знать о GUI» — условие необходимое, но не достаточное. Консольный клиент
/// доказывает его по построению: если <c>storm</c> работает, значит слой приложения
/// от графики не зависит. А вот от <b>локального интерактивного процесса</b> он может
/// зависеть, оставаясь при этом безразличным к GUI: обращение к <c>Console</c>,
/// к текущему каталогу, изменяемое статическое состояние — всё это не мешает консоли
/// и ломается ровно тогда, когда за тем же кодом окажутся десять одновременных
/// запросов по сети.
/// </para>
/// <para>
/// Проверки ниже фиксируют то, что уже верно, чтобы оно не перестало быть верным
/// незаметно. Ответ на вопрос плана: да, при соблюдении этих правил.
/// </para>
/// </remarks>
public sealed class ServerReadinessTests
{
    /// <summary>Проекты, которые в server-варианте окажутся за сетевым API.</summary>
    private static readonly string[] BehindTheApi =
    [
        "StormMachine.Domain",
        "StormMachine.Application",
    ];

    /// <summary>
    /// Ядро не разговаривает с человеком напрямую.
    /// </summary>
    /// <remarks>
    /// <c>Console</c> в слое приложения означает, что ответ уходит в поток вывода
    /// процесса, а не тому, кто спросил. В консоли это совпадает, за сетевым API —
    /// нет: сообщение попадёт в журнал сервера, а клиент не увидит ничего.
    /// <para>
    /// Это не умозрительная опасность. Ровно так и было найдено в И-19: пробы агента
    /// писали ход сопряжения в <c>Console</c>, и графический клиент, у которого консоли
    /// нет, терял его целиком. Сервер потерял бы его так же.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Server: ядро не пишет в консоль")]
    public void CoreLayers_DoNotTalkToTheProcessConsole()
    {
        var offenders = FilesUsing("Console.");

        Assert.True(
            offenders.Count == 0,
            $"Обращение к Console в слое, который окажется за сетевым API: {string.Join(", ", offenders)}. "
            + "За сетевым API вывод процесса — не ответ клиенту: он уйдёт в журнал сервера, "
            + "а спросивший не увидит ничего.");
    }

    /// <summary>
    /// Ядро не опирается на окружение процесса.
    /// </summary>
    /// <remarks>
    /// Текущий каталог, папки профиля, переменные окружения — свойства машины,
    /// на которой идёт процесс. У локального клиента это машина оператора, у сервера —
    /// чужая машина в стойке, и путь, вычисленный из её профиля, окажется не тем.
    /// Такие вещи обязаны приходить снаружи, через порт.
    /// </remarks>
    [Fact(DisplayName = "Server: ядро не опирается на окружение процесса")]
    public void CoreLayers_DoNotDependOnProcessEnvironment()
    {
        var offenders = new List<string>();

        foreach (var forbidden in new[]
                 {
                     "Environment.CurrentDirectory",
                     "Environment.GetFolderPath",
                     "Environment.GetEnvironmentVariable",
                     "Directory.GetCurrentDirectory",
                     "Environment.MachineName",
                     "Environment.UserName",
                 })
        {
            offenders.AddRange(FilesUsing(forbidden).Select(f => $"{f} ({forbidden})"));
        }

        Assert.True(
            offenders.Count == 0,
            $"Обращение к окружению процесса в ядре: {string.Join(", ", offenders)}. "
            + "У сервера это чужая машина в стойке, а не машина оператора: "
            + "такие значения обязаны приходить снаружи, через порт.");
    }

    /// <summary>
    /// В ядре нет изменяемого статического состояния.
    /// </summary>
    /// <remarks>
    /// Один процесс — один оператор, и статическое поле сходит за состояние сеанса.
    /// За сетевым API сеансов столько, сколько подключившихся, и общее статическое
    /// поле превращается в чужие данные в чужом ответе. Найти такое потом по
    /// жалобе «иногда показывает не мои измерения» практически невозможно.
    /// </remarks>
    [Fact(DisplayName = "Server: в ядре нет изменяемого статического состояния")]
    public void CoreLayers_HaveNoMutableStaticState()
    {
        var offenders = new List<string>();

        foreach (var project in BehindTheApi)
        {
            foreach (var file in RepositoryLayout.SourceFiles(Path.Combine("src", project)))
            {
                var code = RepositoryLayout.StripComments(File.ReadAllText(file));

                foreach (var line in code.Split('\n'))
                {
                    var text = line.Trim();

                    if (!text.Contains("static", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Константы и readonly-поля состоянием не являются: их нельзя
                    // изменить, а значит нельзя и перепутать между сеансами.
                    if (text.Contains("readonly", StringComparison.Ordinal)
                        || text.Contains("const", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Методы, классы и вычисляемые свойства (=>) состояния не держат.
                    if (text.Contains('(') || text.Contains("=>", StringComparison.Ordinal)
                        || text.Contains("class", StringComparison.Ordinal)
                        || text.Contains("partial", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Остаётся статическое поле или автосвойство с сеттером.
                    if (text.EndsWith(';') || text.Contains("set;", StringComparison.Ordinal))
                    {
                        offenders.Add($"{RepositoryLayout.Relative(file)}: {text}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Изменяемое статическое состояние в ядре: {string.Join("; ", offenders)}. "
            + "За сетевым API сеансов столько, сколько подключившихся, и общее поле "
            + "станет чужими данными в чужом ответе.");
    }

    /// <summary>
    /// Ядро не привязано к потоку.
    /// </summary>
    /// <remarks>
    /// <c>ThreadStatic</c>, <c>AsyncLocal</c> и явное управление потоками — способы
    /// протащить контекст мимо параметров. У одного оператора за одним экраном это
    /// работает; на пуле запросов сервера контекст утекает между ними.
    /// </remarks>
    [Fact(DisplayName = "Server: ядро не привязано к потоку")]
    public void CoreLayers_AreNotThreadAffine()
    {
        var offenders = new List<string>();

        foreach (var forbidden in new[] { "ThreadStatic", "AsyncLocal", "new Thread(", "Thread.CurrentThread" })
        {
            offenders.AddRange(FilesUsing(forbidden).Select(f => $"{f} ({forbidden})"));
        }

        Assert.True(
            offenders.Count == 0,
            $"Привязка к потоку в ядре: {string.Join(", ", offenders)}. "
            + "На пуле запросов сервера такой контекст утекает между запросами.");
    }

    /// <summary>
    /// Ядро не читает и не пишет файлы само.
    /// </summary>
    /// <remarks>
    /// Доступ к диску — дело инфраструктуры за портом. Ядро, открывающее файл, привязано
    /// к файловой системе той машины, где оно исполняется, и в server-варианте это
    /// окажется не та машина.
    /// <para>
    /// Отдельно отмечено, что <see cref="IOException"/> в ядре ловят: это не работа
    /// с файлами, а честная обработка отказа порта, за которым файл всё-таки есть.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Server: ядро не трогает файловую систему")]
    public void CoreLayers_DoNotTouchTheFileSystem()
    {
        var offenders = new List<string>();

        foreach (var forbidden in new[] { "File.Read", "File.Write", "File.Open", "new FileStream", "Directory.Create" })
        {
            offenders.AddRange(FilesUsing(forbidden).Select(f => $"{f} ({forbidden})"));
        }

        Assert.True(
            offenders.Count == 0,
            $"Работа с файловой системой в ядре: {string.Join(", ", offenders)}. "
            + "Диск — дело инфраструктуры за портом: у сервера это чужая машина.");
    }

    /// <summary>
    /// Долгие операции ядра принимают токен отмены.
    /// </summary>
    /// <remarks>
    /// Принцип 7 сформулирован ради оператора, нажавшего «стоп». Для сервера у него
    /// появляется второй смысл: клиент, закрывший соединение, обязан освободить работу,
    /// которую заказал, — иначе брошенные запросы копятся, пока сервер не встанет.
    /// </remarks>
    [Fact(DisplayName = "Server: долгие операции ядра отменяемы")]
    public void CoreLayers_AcceptCancellation()
    {
        var offenders = new List<string>();

        foreach (var project in BehindTheApi)
        {
            foreach (var file in RepositoryLayout.SourceFiles(Path.Combine("src", project)))
            {
                var code = RepositoryLayout.StripComments(File.ReadAllText(file));

                foreach (var match in System.Text.RegularExpressions.Regex.Matches(
                             code,
                             @"public\s+(?:async\s+)?(?:Task|ValueTask|IAsyncEnumerable)[^\n=]*?\s(\w+Async)\s*\(([^)]*)\)",
                             System.Text.RegularExpressions.RegexOptions.Singleline)
                         .Cast<System.Text.RegularExpressions.Match>())
                {
                    var name = match.Groups[1].Value;

                    // Освобождение и остановка отмены не принимают по существу:
                    // DisposeAsync обязан завершиться, а StopAsync сам и есть остановка.
                    if (name is "DisposeAsync" or "StopAsync")
                    {
                        continue;
                    }

                    if (!match.Groups[2].Value.Contains("CancellationToken", StringComparison.Ordinal))
                    {
                        offenders.Add($"{RepositoryLayout.Relative(file)}: {name}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Долгая операция ядра без токена отмены: {string.Join(", ", offenders)}. "
            + "Клиент, закрывший соединение, обязан освободить заказанную работу — "
            + "иначе брошенные запросы копятся, пока сервер не встанет.");
    }

    /// <summary>Файлы слоёв за API, где встречается указанная подстрока кода.</summary>
    private static List<string> FilesUsing(string fragment)
    {
        var found = new List<string>();

        foreach (var project in BehindTheApi)
        {
            foreach (var file in RepositoryLayout.SourceFiles(Path.Combine("src", project)))
            {
                // Комментарии не в счёт: упоминание запрещённого в объяснении,
                // почему оно запрещено, — документация, а не нарушение.
                var code = RepositoryLayout.StripComments(File.ReadAllText(file));

                if (code.Contains(fragment, StringComparison.Ordinal))
                {
                    found.Add(RepositoryLayout.Relative(file));
                }
            }
        }

        return found;
    }
}
