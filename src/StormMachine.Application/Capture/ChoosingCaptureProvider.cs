using StormMachine.Application.Abstractions;
using StormMachine.Domain.Capture;

namespace StormMachine.Application.Capture;

/// <summary>
/// Выбор источника захвата из тех, что есть на машине.
/// </summary>
/// <remarks>
/// Источников два: сторонний Npcap, если его поставили руками, и встроенный
/// в Windows pktmon, который стоит везде. Выбирать между ними должен продукт,
/// а не оператор: разница между ними — в способе получения кадров, а не
/// в том, что он увидит.
/// <para>
/// <b>Порядок предпочтения.</b> Первым спрашивается тот, кто записан раньше:
/// Npcap отдаёт кадры потоком и не гоняет их через файл, поэтому при наличии
/// он и выигрывает. Встроенный — тот, что работает у всех остальных.
/// </para>
/// <para>
/// <b>Когда не может никто</b>, выбирается не первый попавшийся отказ, а самый
/// выполнимый совет: «перезапустите от администратора» человек может выполнить
/// сам, «поставьте драйвер, который мы не распространяем» — почти никогда.
/// Показать второе, умолчав о первом, значило бы оставить человека ни с чем
/// там, где выход был.
/// </para>
/// </remarks>
public sealed class ChoosingCaptureProvider : ICaptureProvider
{
    private readonly IReadOnlyList<ICaptureProvider> _sources;

    public ChoosingCaptureProvider(IEnumerable<ICaptureProvider> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _sources = [.. sources];
    }

    public CaptureEngine Engine => Chosen().Engine;

    public CaptureRefusal Availability => Chosen().Availability;

    public string? DriverDescription => Chosen().DriverDescription;

    public IReadOnlyList<CaptureAdapter> Adapters() => Chosen().Adapters();

    public Task<CaptureResult> ListenAsync(
        CaptureAdapter adapter,
        CaptureOptions options,
        CancellationToken cancellationToken = default) =>
        Chosen().ListenAsync(adapter, options, cancellationToken);

    /// <summary>
    /// Кем слушаем прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Считается каждый раз заново и не запоминается: драйвер могли поставить,
    /// а продукт — перезапустить с правами, не закрывая окна. Запомненный ответ
    /// пережил бы оба события и врал бы ровно тогда, когда человек сделал то,
    /// о чём его попросили.
    /// </remarks>
    private ICaptureProvider Chosen()
    {
        if (_sources.Count == 0)
        {
            return Nobody.Instance;
        }

        ICaptureProvider? best = null;
        var bestWeight = -1;

        foreach (var source in _sources)
        {
            var refusal = source.Availability;

            if (refusal == CaptureRefusal.None)
            {
                return source;
            }

            var weight = Weight(refusal);

            if (weight > bestWeight)
            {
                best = source;
                bestWeight = weight;
            }
        }

        return best ?? Nobody.Instance;
    }

    /// <summary>Насколько выполним совет, который даст этот отказ.</summary>
    private static int Weight(CaptureRefusal refusal) => refusal switch
    {
        CaptureRefusal.NeedsElevation => 3,
        CaptureRefusal.NoAdapters => 2,
        CaptureRefusal.NoDriver => 1,
        _ => 0,
    };

    /// <summary>Когда источников нет вовсе — например, не Windows.</summary>
    private sealed class Nobody : ICaptureProvider
    {
        internal static readonly Nobody Instance = new();

        public CaptureEngine Engine => CaptureEngine.None;

        public CaptureRefusal Availability => CaptureRefusal.NoDriver;

        public string? DriverDescription => null;

        public IReadOnlyList<CaptureAdapter> Adapters() => [];

        public Task<CaptureResult> ListenAsync(
            CaptureAdapter adapter,
            CaptureOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Слушать нечем: источника захвата на этой машине нет.");
    }
}
