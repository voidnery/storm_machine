using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StormMachine.App.Services;

/// <summary>
/// Журнал клиента: предупреждения и сбои — в файл рядом с базой.
/// </summary>
/// <remarks>
/// У консоли есть консоль, у окна её нет. До этого клиент собирал журнал
/// (<c>ILogger</c> раздавался всем службам), но ни одного приёмника к нему не было
/// подключено: <c>AddLogging</c> задавал только уровень. Каждый вызов
/// <c>LogError</c> уходил в пустоту, и «планировщик не запустился» узнать было
/// неоткуда — ни оператору, ни тому, кто разбирает случай на чужой машине.
///
/// Отсюда правило: <b>у сбоя в окне два адреса</b> — экран и файл. На экране
/// оператор видит, что произошло сейчас; в файле остаётся то, что понадобится
/// через час, когда клиент уже перезапущен, а вопрос «почему у меня не работает»
/// ещё жив.
///
/// Журнал лежит рядом с базой намеренно: это одно место, где продукт держит своё,
/// и оператору достаточно помнить один путь. Размер ограничен — журнал диагностики
/// не имеет права разрастаться на машине, где продукт работает месяцами.
/// </remarks>
public sealed class FileLogProvider : ILoggerProvider
{
    /// <summary>Дальше этого размера журнал переезжает в «старый», и файл начинается заново.</summary>
    private const long LimitBytes = 512 * 1024;

    private readonly object _gate = new();
    private readonly Func<string> _databasePath;

    private string? _path;
    private bool _asking;

    /// <summary>
    /// Журнал заводится там же, где база: одно место, где продукт держит своё.
    /// </summary>
    /// <remarks>
    /// Путь берётся <b>отложенно</b>, и это не украшение. Спросить хранилище при
    /// сборке нельзя: хранилищу нужен журнал (<c>ILogger</c> в его конструкторе),
    /// журналу — хранилище, и контейнер уходит в рекурсию, роняя клиент целиком
    /// без единого сообщения — ровно тем молчанием, против которого журнал
    /// и заведён. К первой записи хранилище уже собрано, и вопрос безопасен.
    /// <para>
    /// Вычислять путь самому — тоже нельзя: чтение переменных окружения в ядре
    /// запрещено (<c>ServerReadinessTests</c>), у сервера это чужая машина в стойке.
    /// Знает путь тот, кто им владеет; журнал только спрашивает.
    /// </para>
    /// </remarks>
    public FileLogProvider(Func<string> databasePath) =>
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));

    /// <summary>
    /// Путь к журналу; пусто, если хранилище о нём ещё не спросили или спросить нельзя.
    /// </summary>
    private string? Resolve()
    {
        if (_path is not null)
        {
            return _path;
        }

        // Защита от повторного входа: если хранилище на пути к ответу само что-то
        // напишет в журнал, вопрос вернётся сюда же. Такая запись теряется — это
        // дешевле, чем зациклиться на ней.
        if (_asking)
        {
            return null;
        }

        try
        {
            _asking = true;

            var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath()));

            _path = Path.Combine(
                string.IsNullOrEmpty(directory) ? AppContext.BaseDirectory : directory,
                "клиент.log");

            return _path;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>
    /// Путь к журналу — его показывают в настройках, чтобы файл можно было найти.
    /// </summary>
    /// <remarks>
    /// Названо как у хранилища (<c>IRunStore.Location</c>), а не «Path»: свойство
    /// с таким именем закрыло бы собой класс <c>System.IO.Path</c> внутри этого типа.
    /// </remarks>
    public string Location => Resolve() ?? "журнал ещё не заведён";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Append(string line)
    {
        // Журнал не имеет права уронить то, о чём он рассказывает: недоступный файл,
        // занятый каталог и полный диск — это молчание журнала, а не отказ клиента.
        try
        {
            lock (_gate)
            {
                if (Resolve() is not { } path)
                {
                    return;
                }

                var file = new FileInfo(path);

                if (file.Exists && file.Length > LimitBytes)
                {
                    File.Move(path, path + ".старый", overwrite: true);
                }
                else if (!file.Exists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private sealed class FileLogger(FileLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            var text = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture))
                .Append("  ")
                .Append(Word(logLevel))
                .Append("  ")
                .Append(Short(category))
                .Append("  ")
                .AppendLine(formatter(state, exception));

            if (exception is not null)
            {
                text.AppendLine(exception.ToString());
            }

            owner.Append(text.ToString());
        }

        /// <summary>Уровень словами: журнал читает человек, а не разбирает программа.</summary>
        private static string Word(LogLevel level) => level switch
        {
            LogLevel.Critical => "отказ   ",
            LogLevel.Error => "ошибка  ",
            _ => "внимание",
        };

        /// <summary>Из полного имени типа нужна последняя часть — остальное шум.</summary>
        private static string Short(string category) =>
            category.LastIndexOf('.') is var dot && dot >= 0 && dot < category.Length - 1
                ? category[(dot + 1)..]
                : category;
    }
}
