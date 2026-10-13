using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace StormMachine.App.Services;

/// <summary>
/// Перезапуск продукта с правами администратора.
/// </summary>
/// <remarks>
/// Половина уровня 2 и часть уровня 0 упираются ровно в это: драйвер захвата
/// разговаривает только с администратором, сырые сокеты у многих тоже. Экран
/// возможностей знал об этом и писал «перезапустите от имени администратора» —
/// то есть называл действие, которое сам же мог выполнить. Замечание оператора
/// про консольные команды в окне — того же рода: если возможность есть, её
/// делают, а не пересказывают.
/// <para>
/// Отказ в запросе прав — обычное дело, а не беда: человек увидел окно UAC
/// и передумал. Продукт остаётся работать и говорит об этом одной строкой.
/// </para>
/// </remarks>
internal static class Elevation
{
    /// <summary>Код, которым Windows сообщает, что человек закрыл окно UAC.</summary>
    private const int Cancelled = 1223;

    /// <summary>Уже запущены с правами.</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>
    /// Запускает вторую копию с правами и закрывает эту.
    /// </summary>
    /// <returns>Строка для оператора, если не вышло; <c>null</c> — уходим на перезапуск.</returns>
    public static string? Restart()
    {
        if (Environment.ProcessPath is not { Length: > 0 } program)
        {
            return "Не удалось определить, откуда запущен продукт, — перезапустите вручную.";
        }

        try
        {
            // Verb «runas» и есть запрос прав: без UseShellExecute он не действует.
            Process.Start(new ProcessStartInfo(program)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(program) ?? string.Empty,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return "Запрос прав отклонён — продукт продолжает работать без них.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"Перезапустить не вышло: {ex.Message}";
        }

        // Закрываемся обычным путём, а не Environment.Exit: на выходе клиент
        // останавливает планировщик и закрывает базу. Две копии, одновременно
        // пишущие в один файл, — не то, чем стоит платить за полсекунды.
        // Полное имя: в этой сборке «Application» — ещё и наш слой приложения.
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }

        return null;
    }
}
