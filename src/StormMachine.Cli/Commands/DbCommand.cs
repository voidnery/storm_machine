using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;

namespace StormMachine.Cli.Commands;

/// <summary>
/// <c>storm db</c> — файл базы: проверка целостности и лечение.
/// </summary>
/// <remarks>
/// Появилась после первого настоящего повреждения рабочей базы (И-24). До неё
/// у оператора не было ни способа узнать, что база битая, ни способа вылечиться,
/// кроме удаления всей истории измерений.
/// </remarks>
internal static class DbCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("db", "Файл базы: проверка целостности и лечение.")
        {
            CreateCheck(services),
            CreateRepair(services),
        };

        return command;
    }

    private static Command CreateCheck(IServiceProvider services)
    {
        var command = new Command("check", "Проверить файл базы на повреждения.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var maintenance = services.GetRequiredService<IDatabaseMaintenance>();

            Console.WriteLine($"База: {maintenance.DatabasePath}");

            if (!File.Exists(maintenance.DatabasePath))
            {
                Console.WriteLine("Файла ещё нет — он появится при первой записи. Проверять нечего.");
                return 0;
            }

            var health = await maintenance.CheckAsync(cancellationToken).ConfigureAwait(false);

            if (health.IsHealthy)
            {
                Console.WriteLine("Целостность в порядке.");
                return 0;
            }

            Console.WriteLine("База повреждена — файл читается не весь. Что нашла проверка:");
            foreach (var finding in health.Findings.Take(10))
            {
                Console.WriteLine($"  {finding}");
            }

            if (health.Findings.Count > 10)
            {
                Console.WriteLine($"  … и ещё {health.Findings.Count - 10}.");
            }

            Console.WriteLine();
            Console.WriteLine("Лечение: storm db repair — читаемое переносится в новый файл,");
            Console.WriteLine("повреждённый целиком сохраняется в резервной папке рядом с базой.");
            Console.WriteLine("Перед лечением закройте клиент и остановите службу мониторов, если она есть.");

            return 1;
        });

        return command;
    }

    private static Command CreateRepair(IServiceProvider services)
    {
        var command = new Command("repair", "Пересобрать базу: читаемое — в новый файл, повреждённый — в резервную папку.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var maintenance = services.GetRequiredService<IDatabaseMaintenance>();

            Console.WriteLine($"База: {maintenance.DatabasePath}");

            if (!File.Exists(maintenance.DatabasePath))
            {
                Console.WriteLine("Файла ещё нет — лечить нечего.");
                return 0;
            }

            DatabaseRepairReport report;
            try
            {
                report = await maintenance.RepairAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                Console.WriteLine($"Файл базы занят другим процессом: {e.Message}");
                Console.WriteLine("Закройте клиент и остановите службу мониторов, затем повторите.");
                return 1;
            }

            Console.WriteLine("Готово. Пересобранная база на прежнем месте, целостность проверена.");
            Console.WriteLine($"Повреждённый файл сохранён: {report.BackupPath}");
            Console.WriteLine();
            Console.WriteLine($"Перенесено: прогонов {report.RunsKept}, "
                + $"сэмплов {report.SamplesKept.ToString("N0", CultureInfo.InvariantCulture)}.");

            if (report.RunsWithoutSamples > 0)
            {
                Console.WriteLine($"Без сырых сэмплов остались {report.RunsWithoutSamples} прогонов: "
                    + "их агрегаты целы, сводка и отчёты работают, графика по сырью не будет.");
            }

            if (report.RunsLost > 0)
            {
                Console.WriteLine($"Потеряны целиком {report.RunsLost} прогонов — "
                    + "от них остались только агрегаты без строки журнала, они удалены.");
            }

            foreach (var table in report.PartialTables)
            {
                Console.WriteLine($"Таблица перенесена не целиком — {table}.");
            }

            if (report.RunsWithoutSamples == 0 && report.RunsLost == 0 && report.PartialTables.Count == 0)
            {
                Console.WriteLine("Потерь нет: всё содержимое прочиталось.");
            }

            return 0;
        });

        return command;
    }
}
