using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Profiles;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Перенос настроек между машинами.
/// </summary>
/// <remarks>
/// Закрывает три однотипных долга разом — расписание (И-14), эталоны (И-15) и профили
/// (И-16). У пресетов выгрузка была с И-5, у остальных не было, и вопрос у всех один:
/// «я настроил у себя, разворачиваю у заказчика».
/// <para>
/// Команда заведена в корне, а не внутри <c>profiles</c>: она переносит не профили,
/// а настройки целиком, и спрятать её в раздел одного из трёх видов значило бы
/// сделать её ненаходимой для двух остальных.
/// </para>
/// </remarks>
internal static class SettingsTransferCommands
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command(
            "settings",
            "Перенос настроек между машинами: профили, мониторы, эталоны.");

        command.Subcommands.Add(BuildExport(services));
        command.Subcommands.Add(BuildImport(services));

        command.SetAction((_, _) =>
        {
            Console.WriteLine("storm settings export --в настройки.json   выгрузить");
            Console.WriteLine("storm settings import настройки.json       загрузить");
            Console.WriteLine();
            Console.WriteLine("Переносятся профили окружения, мониторы и эталоны.");
            Console.WriteLine("Пресеты переносятся своей командой: storm presets export.");
            Console.WriteLine();
            Console.WriteLine(SettingsTransfer.SecretsNote);

            return Task.FromResult(0);
        });

        return command;
    }

    private static Command BuildExport(IServiceProvider services)
    {
        var output = new Option<string>("--в", "--out")
        {
            Description = "Куда сохранить файл.",
            DefaultValueFactory = _ => "storm-настройки.json",
        };

        var command = new Command("export", "Выгрузить настройки в файл JSON.") { output };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var transfer = services.GetRequiredService<SettingsTransfer>();
            var bundle = await transfer.ExportAsync(cancellationToken).ConfigureAwait(false);

            if (bundle.IsEmpty)
            {
                Console.WriteLine("Переносить нечего: ни профилей, ни мониторов, ни эталонов.");

                return 0;
            }

            var path = parse.GetValue(output)!;

            await File.WriteAllTextAsync(path, SettingsTransfer.Write(bundle), cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"Выгружено в {Path.GetFullPath(path)}: {bundle.Describe()}.");
            Console.WriteLine();

            // Сказать здесь, а не оставить выясняться при загрузке: оператор,
            // перенёсший мониторы и обнаруживший на новой машине, что опрашивать
            // оборудование нечем, решит, что перенос сломался.
            Console.WriteLine(SettingsTransfer.SecretsNote);

            return 0;
        });

        return command;
    }

    private static Command BuildImport(IServiceProvider services)
    {
        var file = new Argument<string>("файл") { Description = "Файл, выгруженный на другой машине." };

        var keep = new Option<bool>("--не-трогать-существующие", "--keep")
        {
            Description = "Пропускать те настройки, которые уже есть, вместо обновления.",
        };

        var command = new Command("import", "Загрузить настройки из файла JSON.") { file, keep };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var path = parse.GetValue(file)!;

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Файл не найден: {Path.GetFullPath(path)}");

                return 1;
            }

            var transfer = services.GetRequiredService<SettingsTransfer>();

            Domain.Profiles.SettingsBundle bundle;

            try
            {
                bundle = SettingsTransfer.Read(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Файл не разобран: {ex.Message}");

                return 1;
            }

            Console.WriteLine($"В файле: {bundle.Describe()}. Выгружено "
                              + $"{bundle.ExportedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}"
                              + (bundle.ProductVersion is { Length: > 0 } version ? $", версией {version}" : string.Empty));
            Console.WriteLine();

            var report = await transfer
                .ImportAsync(bundle, overwrite: !parse.GetValue(keep), cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"Добавлено {report.Added}, обновлено {report.Updated}, "
                              + $"пропущено {report.Skipped}.");

            if (report.Problems.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Не перенеслось:");

                foreach (var problem in report.Problems)
                {
                    Console.WriteLine($"  ! {problem}");
                }
            }

            if (report.Added + report.Updated > 0)
            {
                Console.WriteLine();

                // Профиль приезжает неактивным всегда: смена профиля меняет пороги
                // и состав работающих мониторов, а делать это молча значит поменять
                // смысл измерений за спиной оператора. Говорится только когда профили
                // в файле были — совет про то, чего не приезжало, сбивает (стенд И-24).
                if (bundle.Profiles.Count > 0)
                {
                    Console.WriteLine("Профили приехали неактивными — выберите нужный: storm profiles use <имя>.");
                }

                Console.WriteLine(SettingsTransfer.SecretsNote);
            }

            return 0;
        });

        return command;
    }
}
