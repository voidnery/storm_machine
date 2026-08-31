using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Results;

namespace StormMachine.Application.Storage;

/// <summary>
/// Политика хранения как сохранённая настройка.
/// </summary>
/// <remarks>
/// До И-24 политика существовала только как умолчание и разовые ключи
/// <c>storm runs purge</c>: оператор, которому 90 дней сырья мало или много,
/// должен был помнить свои числа и передавать их при каждой уборке. Сохранённая
/// политика действует и при уборке на старте — иначе она была бы надписью,
/// а не политикой.
/// </remarks>
public sealed class RetentionSettings(ISettingsStore settings)
{
    public const string RawDaysKey = "retention.raw-days";
    public const string RunDaysKey = "retention.run-days";

    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Сохранённая политика; не заданное берётся из умолчания.</summary>
    public async Task<RetentionPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        var raw = await ReadDaysAsync(RawDaysKey, cancellationToken).ConfigureAwait(false);
        var run = await ReadDaysAsync(RunDaysKey, cancellationToken).ConfigureAwait(false);

        return new RetentionPolicy
        {
            RawSampleHorizon = raw is { } r ? TimeSpan.FromDays(r) : RetentionPolicy.Default.RawSampleHorizon,
            RunHorizon = run is { } n ? TimeSpan.FromDays(n) : RetentionPolicy.Default.RunHorizon,
        };
    }

    /// <summary>Сохраняет политику. Заведомо бессмысленные значения отклоняются с объяснением.</summary>
    public async Task<RetentionPolicy> SetAsync(int rawDays, int runDays, CancellationToken cancellationToken = default)
    {
        // Сообщения без имени параметра: они показываются оператору как есть,
        // и «(Parameter 'runDays')» в них — мусор чужого языка.
        if (rawDays < 1)
        {
            throw new ArgumentException("Сырые сэмплы хранятся хотя бы день — иначе журнал пуст всегда.");
        }

        if (runDays < rawDays)
        {
            throw new ArgumentException(
                "Прогоны не могут храниться меньше своих сырых сэмплов: сэмплы без прогона — сироты.");
        }

        await _settings
            .SetAsync(RawDaysKey, rawDays.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _settings
            .SetAsync(RunDaysKey, runDays.ToString(CultureInfo.InvariantCulture), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new RetentionPolicy
        {
            RawSampleHorizon = TimeSpan.FromDays(rawDays),
            RunHorizon = TimeSpan.FromDays(runDays),
        };
    }

    private async Task<int?> ReadDaysAsync(string key, CancellationToken cancellationToken)
    {
        var value = await _settings.GetAsync(key, cancellationToken).ConfigureAwait(false);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
            ? days
            : null;
    }
}
