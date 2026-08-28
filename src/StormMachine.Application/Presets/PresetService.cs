using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Presets;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.Presets;

/// <summary>
/// Работа с библиотекой пресетов: проверка, сохранение, запуск, обмен.
/// </summary>
/// <remarks>
/// Проверка пресета опирается на объявление пробы — тот же источник, из которого строятся
/// формы в интерфейсе и ключи командной строки. Третье применение одного объявления:
/// добавляя пробу, мы бесплатно получаем и проверку её пресетов.
/// </remarks>
public sealed class PresetService(IPresetStore store, IProbeRegistry registry)
{
    private readonly IPresetStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProbeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public Task<IReadOnlyList<Preset>> ListAsync(PresetQuery query, CancellationToken cancellationToken = default) =>
        _store.ListAsync(query, cancellationToken);

    public Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _store.GetAsync(id, cancellationToken);

    public Task<Preset?> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
        _store.FindByNameAsync(name, cancellationToken);

    public Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        _store.GetTagsAsync(cancellationToken);

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(id, cancellationToken);

    /// <summary>
    /// Проверяет пресет.
    /// </summary>
    /// <remarks>
    /// Проба проверяется по своему объявлению — тот же источник, из которого строятся
    /// формы в интерфейсе и ключи командной строки. Сценарий проверяется по каталогу
    /// шаблонов: его шаги и пороги заданы шаблоном, и параметров у пресета сценария нет.
    /// </remarks>
    public IReadOnlyList<PresetValidationError> Validate(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var errors = new List<PresetValidationError>();

        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            errors.Add(new PresetValidationError(nameof(preset.Name), "Имя пресета не может быть пустым."));
        }

        if (preset.Kind == PresetKind.Scenario)
        {
            if (!Scenarios.ScenarioTemplates.All.Any(t =>
                    string.Equals(t.Key, preset.Subject, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new PresetValidationError(
                    nameof(preset.Subject),
                    $"Сценарий «{preset.Subject}» не найден. Доступные: "
                    + string.Join(", ", Scenarios.ScenarioTemplates.All.Select(t => t.Key))));
            }

            if (preset.Parameters.Count > 0)
            {
                errors.Add(new PresetValidationError(
                    nameof(preset.Parameters),
                    "У пресета сценария параметров нет: шаги и пороги задаёт шаблон."));
            }

            return errors;
        }

        if (!_registry.TryGet(preset.Subject, out var probe))
        {
            errors.Add(new PresetValidationError(
                nameof(preset.Subject),
                $"Проба «{preset.Subject}» не найдена. Доступные: "
                + string.Join(", ", _registry.Descriptors.Select(d => d.Name))));

            return errors;
        }

        // Неизвестный параметр — не мелочь: он молча ничего не сделает, и оператор
        // будет думать, что измеряет одно, а измерять другое.
        var declared = probe.Descriptor.Parameters
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in preset.Parameters.Keys)
        {
            if (!declared.Contains(name))
            {
                errors.Add(new PresetValidationError(
                    name,
                    $"Проба «{preset.Subject}» не знает параметра «{name}». "
                    + $"Известные: {string.Join(", ", declared)}"));
            }
        }

        var request = ToRequest(preset);

        foreach (var error in probe.Validate(request))
        {
            errors.Add(new PresetValidationError(error.ParameterName, error.Message));
        }

        return errors;
    }

    /// <summary>Сохраняет пресет после проверки.</summary>
    public async Task<Preset> SaveAsync(Preset preset, CancellationToken cancellationToken = default)
    {
        var errors = Validate(preset);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Пресет не прошёл проверку: "
                + string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}")));
        }

        return await _store.SaveAsync(preset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Готовит запрос на выполнение по пресету.</summary>
    public static ProbeRequest ToRequest(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in preset.Parameters)
        {
            parameters[key] = value;
        }

        return new ProbeRequest
        {
            Target = preset.Target,
            Parameters = parameters,
        };
    }

    /// <summary>Достаёт пробу пресета. Для пресета сценария всегда ложь: пробы у него нет.</summary>
    public bool TryGetProbe(Preset preset, out IProbe probe)
    {
        ArgumentNullException.ThrowIfNull(preset);

        if (preset.Kind == PresetKind.Scenario)
        {
            probe = null!;

            return false;
        }

        return _registry.TryGet(preset.Subject, out probe);
    }

    public Task RecordRunAsync(Guid id, CancellationToken cancellationToken = default) =>
        _store.RecordRunAsync(id, cancellationToken);

    /// <summary>
    /// Создаёт пресет из фактически выполненного измерения.
    /// </summary>
    /// <remarks>
    /// Сквозной принцип «сохранить как пресет» из §2 анализа: пресет рождается не из формы,
    /// а из измерения, которое только что оказалось полезным.
    /// </remarks>
    public static Preset FromRequest(string name, string probeName, ProbeRequest request, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in request.Parameters)
        {
            parameters[key] = Stringify(value);
        }

        var now = DateTimeOffset.UtcNow;

        return new Preset
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Subject = probeName,
            Target = request.Target,
            Parameters = parameters,
            Version = 1,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    // ------------------------------------------------------------------ обмен

    public static PresetBundle ToBundle(IEnumerable<Preset> presets, string? exportedBy = null)
    {
        ArgumentNullException.ThrowIfNull(presets);

        return new PresetBundle
        {
            ExportedBy = exportedBy,
            Presets = [.. presets.Select(ToPortable)],
        };
    }

    public static PortablePreset ToPortable(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        return new PortablePreset
        {
            Name = preset.Name,
            Description = preset.Description,
            Subject = preset.Subject,
            Kind = preset.Kind == PresetKind.Scenario ? nameof(PresetKind.Scenario) : null,
            TargetKind = preset.Target.Kind.ToString(),
            TargetValue = preset.Target.Value,
            TargetLabel = preset.Target.Label,
            Parameters = new Dictionary<string, string?>(preset.Parameters, StringComparer.OrdinalIgnoreCase),
            Tags = [.. preset.Tags],
        };
    }

    /// <summary>
    /// Вносит набор в библиотеку.
    /// </summary>
    /// <remarks>
    /// Совпадение по имени считается тем же пресетом и обновляется, а не задваивается:
    /// библиотека из десяти «ping шлюза (1)…(10)» бесполезна. Непрошедшие проверку
    /// пропускаются с объяснением — импорт не должен падать целиком из-за одной записи.
    /// </remarks>
    public async Task<PresetImportReport> ImportAsync(
        PresetBundle bundle,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (bundle.FormatVersion > PresetBundle.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Файл сохранён более новой версией продукта (формат {bundle.FormatVersion}, "
                + $"поддерживается {PresetBundle.CurrentFormatVersion}). Обнови Storm Machine.");
        }

        var added = 0;
        var updated = 0;
        var skipped = 0;
        var problems = new List<string>();

        foreach (var portable in bundle.Presets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Preset candidate;
            try
            {
                candidate = FromPortable(portable);
            }
            catch (Exception ex)
            {
                skipped++;
                problems.Add($"«{portable.Name}»: {ex.Message}");
                continue;
            }

            var errors = Validate(candidate);
            if (errors.Count > 0)
            {
                skipped++;
                problems.Add($"«{portable.Name}»: {string.Join("; ", errors.Select(e => e.Message))}");
                continue;
            }

            var existing = await _store.FindByNameAsync(portable.Name, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                added++;
                continue;
            }

            if (!overwrite)
            {
                skipped++;
                continue;
            }

            await _store
                .SaveAsync(candidate with { Id = existing.Id, CreatedUtc = existing.CreatedUtc }, cancellationToken)
                .ConfigureAwait(false);

            updated++;
        }

        return new PresetImportReport
        {
            Added = added,
            Updated = updated,
            Skipped = skipped,
            Problems = problems,
        };
    }

    private static Preset FromPortable(PortablePreset portable)
    {
        ArgumentNullException.ThrowIfNull(portable);

        if (!Enum.TryParse<TargetKind>(portable.TargetKind, ignoreCase: true, out var kind))
        {
            throw new FormatException($"Неизвестный вид цели «{portable.TargetKind}».");
        }

        var now = DateTimeOffset.UtcNow;

        return new Preset
        {
            Id = Guid.NewGuid(),
            Name = portable.Name,
            Description = portable.Description,
            Subject = portable.Subject,
            Kind = string.Equals(portable.Kind, nameof(PresetKind.Scenario), StringComparison.OrdinalIgnoreCase)
                ? PresetKind.Scenario
                : PresetKind.Probe,
            Target = new Target
            {
                Kind = kind,
                Value = portable.TargetValue,
                Label = portable.TargetLabel,
            },
            Parameters = new Dictionary<string, string?>(portable.Parameters, StringComparer.OrdinalIgnoreCase),
            Tags = [.. portable.Tags],
            Version = 1,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
