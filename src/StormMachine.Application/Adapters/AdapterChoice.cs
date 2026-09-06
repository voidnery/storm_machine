using StormMachine.Application.Abstractions;

namespace StormMachine.Application.Adapters;

/// <summary>
/// С какого сетевого адаптера продукт меряет.
/// </summary>
/// <remarks>
/// До этого адаптер выбирался сам — тот, у которого маршрут по умолчанию. На машине
/// с одной сетевой картой это тот же выбор, что сделал бы человек. На машине
/// с Hyper-V, VPN или двумя картами — не тот: маршрут по умолчанию уходит через
/// виртуальный коммутатор, продукт честно предупреждает «измерение через виртуальный
/// коммутатор», и на этом всё. Предупреждать о том, что нельзя изменить, — половина
/// дела; вторая половина — дать выбрать.
///
/// Выбор хранится в базе, а не в памяти окна: он относится к машине, а не к сеансу,
/// и консоль обязана меряться оттуда же, откуда окно. Пустое значение означает
/// «как раньше» — по маршруту по умолчанию.
/// </remarks>
public sealed class AdapterChoice(ISettingsStore settings)
{
    /// <summary>Ключ настройки. Один на консоль и окно.</summary>
    public const string Key = "measurement.adapter";

    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// Опознание выбранного адаптера; <c>null</c> — выбирается автоматически.
    /// </summary>
    /// <remarks>
    /// Читается синхронно многими вызывающими на каждом обращении к окружению,
    /// поэтому держится в памяти, а из базы поднимается один раз при запуске.
    /// </remarks>
    public string? ChosenId { get; private set; }

    /// <summary>Выбор не сделан — адаптер определяется маршрутом по умолчанию.</summary>
    public bool IsAutomatic => string.IsNullOrEmpty(ChosenId);

    /// <summary>Поднимает сохранённый выбор. Вызывается при запуске, до показа окна.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _settings.GetAsync(Key, cancellationToken).ConfigureAwait(false);

        ChosenId = string.IsNullOrWhiteSpace(stored) ? null : stored.Trim();
    }

    /// <summary>
    /// Запоминает выбор оператора. <c>null</c> возвращает к автоматическому.
    /// </summary>
    public async Task ChooseAsync(string? adapterId, CancellationToken cancellationToken = default)
    {
        var value = string.IsNullOrWhiteSpace(adapterId) ? null : adapterId.Trim();

        if (value is null)
        {
            await _settings.RemoveAsync(Key, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _settings.SetAsync(Key, value, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        ChosenId = value;
    }
}
