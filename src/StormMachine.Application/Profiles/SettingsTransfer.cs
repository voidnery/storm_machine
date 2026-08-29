using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Reports;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Profiles;

/// <summary>
/// Перенос настроек между машинами.
/// </summary>
/// <remarks>
/// Один механизм на три однотипных долга — расписание (И-14), эталоны (И-15) и профили
/// (И-16). Три отдельные выгрузки разошлись бы по формату и по поведению при повторной
/// загрузке, а вопрос у них один: «я настроил у себя, разворачиваю у заказчика».
/// <para>
/// Формат человекочитаемый и с отступами, как у пресетов: файл можно открыть, поправить
/// и отдать коллеге. Контекст сериализации сгенерирован исходниками — клиенты
/// публикуются с обрезкой, и рефлексивная сериализация сломалась бы у пользователя,
/// а не при сборке.
/// </para>
/// </remarks>
public sealed class SettingsTransfer(
    IProfileStore profiles,
    IMonitorStore monitors,
    IBaselineStore baselines,
    IScenarioStore scenarios)
{
    private static readonly SettingsJsonContext Context =
        new(new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            // Без этого «Офис заказчика» превращается в вереницу escape-последовательностей
            // и файл перестаёт быть человекочитаемым, ради чего он и задуман.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    private readonly IProfileStore _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly IMonitorStore _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
    private readonly IBaselineStore _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));

    /// <summary>
    /// Собранные оператором сценарии.
    /// </summary>
    /// <remarks>
    /// Переносятся охотнее профиля: профиль описывает место и на другой машине скорее
    /// всего другой, а цепочка проверок — способ работы, и он у оператора один.
    /// Шаблоны при этом не выгружаются: они часть продукта и есть на любой машине.
    /// </remarks>
    private readonly IScenarioStore _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));

    /// <summary>
    /// Что нельзя выгрузить и почему.
    /// </summary>
    /// <remarks>
    /// Говорится при выгрузке, а не выясняется при загрузке. Оператор, перенёсший
    /// мониторы и обнаруживший на новой машине, что опрашивать оборудование нечем,
    /// решит, что перенос сломался, — а он сработал ровно так, как должен.
    /// </remarks>
    public const string SecretsNote =
        "Учётные данные SNMP и пароли каналов оповещения в файл не попадают: они зашифрованы "
        + "ключом вашей учётной записи и на другой машине не расшифруются. Их надо завести "
        + "заново — «storm snmp creds add» и «storm alerts set».";

    /// <summary>Собирает всё, что можно перенести.</summary>
    public async Task<SettingsBundle> ExportAsync(CancellationToken cancellationToken = default)
    {
        var allProfiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        var allMonitors = await _monitors.ListAsync(cancellationToken).ConfigureAwait(false);

        var allBaselines = await _baselines
            .ListAsync(new BaselineQuery { Limit = 10_000 }, cancellationToken)
            .ConfigureAwait(false);

        var allScenarios = await _scenarios.ListAsync(cancellationToken).ConfigureAwait(false);

        return new SettingsBundle
        {
            ProductVersion = ProductInfo.Version,

            // Признак активности не переносится: активен профиль или нет — свойство
            // машины, а не настройки. Приехавший профиль, объявивший себя активным,
            // молча поменял бы пороги на чужой машине.
            Profiles = [.. allProfiles.Select(p => p with { IsActive = false })],

            // Назначенный срок тоже не переносится: он посчитан от часов той машины,
            // и на новой оказался бы либо в далёком прошлом, либо в будущем.
            // Планировщик назначит его сам при первом запуске.
            Monitors = [.. allMonitors.Select(m => m with { NextDueUtc = null })],

            // Ссылка на прогон остаётся — но её осмысленность на новой машине под
            // вопросом: журнал туда не едет. Обнулять нельзя: если базу перенесли
            // целиком, ссылка рабочая, и терять её было бы обидно.
            Baselines = [.. allBaselines],

            // Шаблоны не выгружаются: они часть продукта и есть на любой машине.
            // Едет только собранное руками.
            Scenarios = [.. allScenarios],
        };
    }

    /// <summary>
    /// Загружает настройки из файла.
    /// </summary>
    /// <remarks>
    /// Опознание идёт по идентификатору: повторная загрузка того же файла обновляет
    /// настройки на месте, а не заводит их вторыми копиями. Имя для этого не годится —
    /// его меняют.
    /// <para>
    /// Одна непригодная запись не отменяет остальные: перенести девять настроек
    /// из десяти полезнее, чем отказаться от всех. Но и молчать о десятой нельзя.
    /// </para>
    /// </remarks>
    /// <param name="bundle">Что загружать.</param>
    /// <param name="overwrite">Обновлять ли уже существующие. Ложь — пропускать их.</param>
    public async Task<SettingsImportReport> ImportAsync(
        SettingsBundle bundle,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (bundle.FormatVersion > SettingsBundle.CurrentFormatVersion)
        {
            throw new FormatException(
                $"Файл сохранён более новой версией продукта (формат {bundle.FormatVersion}, "
                + $"поддерживается {SettingsBundle.CurrentFormatVersion}). Обновите Storm Machine.");
        }

        var added = 0;
        var updated = 0;
        var skipped = 0;
        var problems = new List<string>();

        foreach (var profile in bundle.Profiles)
        {
            var errors = profile.Validate();

            if (errors.Count > 0)
            {
                problems.Add($"профиль «{profile.Name}»: {string.Join("; ", errors)}");
                skipped++;

                continue;
            }

            var existing = await _profiles.GetAsync(profile.Id, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !overwrite)
            {
                skipped++;

                continue;
            }

            // Активность не приезжает вместе с профилем ни при каких условиях:
            // приехавший профиль, объявивший себя активным, молча поменял бы пороги.
            await _profiles
                .SaveAsync(profile with { IsActive = existing?.IsActive ?? false }, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        foreach (var monitor in bundle.Monitors)
        {
            var errors = monitor.Validate();

            if (errors.Count > 0)
            {
                problems.Add($"монитор «{monitor.Name}»: {string.Join("; ", errors)}");
                skipped++;

                continue;
            }

            var existing = await _monitors.GetAsync(monitor.Id, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !overwrite)
            {
                skipped++;

                continue;
            }

            // Пресет, из которого монитор заведён, на новую машину не едет: библиотека
            // пресетов переносится своим механизмом. Ссылка на отсутствующий пресет
            // не ломает монитор — параметры лежат в нём самом, — но показывать родство
            // с тем, чего здесь нет, незачем.
            var arriving = monitor with
            {
                NextDueUtc = null,
                PresetId = null,
            };

            await _monitors.SaveAsync(arriving, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        foreach (var baseline in bundle.Baselines)
        {
            var existing = await _baselines.GetAsync(baseline.Id, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !overwrite)
            {
                skipped++;

                continue;
            }

            await _baselines.SaveAsync(baseline, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        foreach (var scenario in bundle.Scenarios)
        {
            if (scenario.Steps.Count == 0)
            {
                // Пустая цепочка — не настройка, а заготовка: переносить её незачем,
                // но и молчать нельзя, иначе оператор будет искать её на новой машине.
                problems.Add($"сценарий «{scenario.Name}»: шагов нет, переносить нечего");
                skipped++;

                continue;
            }

            var existing = await _scenarios.GetAsync(scenario.Id, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !overwrite)
            {
                skipped++;

                continue;
            }

            await _scenarios.SaveAsync(scenario, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        return new SettingsImportReport
        {
            Added = added,
            Updated = updated,
            Skipped = skipped,
            Problems = problems,
        };
    }

    public static string Write(SettingsBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        return JsonSerializer.Serialize(bundle, Context.SettingsBundle);
    }

    public static SettingsBundle Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize(json, Context.SettingsBundle)
               ?? throw new FormatException("Файл пуст или не является набором настроек Storm Machine.");
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SettingsBundle))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
