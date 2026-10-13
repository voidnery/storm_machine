using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StormMachine.Capture.PktMon;

/// <summary>Чем кончился вызов pktmon.</summary>
public sealed record PktMonAnswer(int Code, string Output)
{
    public bool Ok => Code == 0;

    /// <summary>Отказано в доступе: драйвер разговаривает только с администратором.</summary>
    public bool Forbidden => Code == 5;
}

/// <summary>
/// Запуск встроенного в Windows монитора пакетов.
/// </summary>
/// <remarks>
/// Отдельный класс, потому что всё общение с pktmon идёт через запуск процесса,
/// а это именно то, что в проверках подменяют: слушать живую сеть в модульном
/// тесте нельзя, а разобрать записанный ответ — можно.
/// <para>
/// Путь к программе берётся из системного каталога, а не из <c>PATH</c>: подменить
/// <c>PATH</c> может кто угодно, а продукт здесь запускает программу с правами
/// администратора.
/// </para>
/// </remarks>
public class PktMonTool
{
    /// <summary>Сколько ждать ответа на управляющую команду.</summary>
    /// <remarks>
    /// Все команды, кроме преобразования журнала, отвечают за доли секунды.
    /// Минута — не ожидание, а предел: если pktmon завис, продукт не должен
    /// зависнуть вместе с ним.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(1);

    /// <summary>Есть ли на машине встроенный монитор пакетов.</summary>
    public virtual bool Exists => Executable is not null;

    /// <summary>Версия программы — её и показывают как версию «драйвера».</summary>
    public virtual string? Version
    {
        get
        {
            if (Executable is not { } path)
            {
                return null;
            }

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);

                return string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public virtual async Task<PktMonAnswer> RunAsync(
        string arguments,
        CancellationToken cancellationToken = default)
    {
        if (Executable is not { } path)
        {
            return new PktMonAnswer(-1, "pktmon на этой машине нет.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        try
        {
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            using var limit = new CancellationTokenSource(Patience);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

            return new PktMonAnswer(process.ExitCode, output + error);
        }
        catch (OperationCanceledException)
        {
            // Отмену наверх не пускаем: остановить захват и прибрать за собой
            // нужно и тогда, когда оператор передумал ждать.
            Stop(process);

            return new PktMonAnswer(-1, "Вызов pktmon прерван.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new PktMonAnswer(-1, ex.Message);
        }
    }

    /// <summary>Строка аргументов для фильтра по порту.</summary>
    internal static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Процесс уже умер сам — ровно то, чего мы и добивались.
        }
    }

    private static string? Executable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            var path = Path.Combine(Environment.SystemDirectory, "pktmon.exe");

            return File.Exists(path) ? path : null;
        }
    }
}
