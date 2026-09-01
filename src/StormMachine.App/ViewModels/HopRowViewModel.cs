using System.Globalization;
using StormMachine.Domain.Measurements;
using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Строка таблицы маршрута.
/// </summary>
/// <remarks>
/// Значения обновляются на месте, а не пересозданием коллекции. При наблюдении раз
/// в секунду пересборка тридцати строк каждый раз сбрасывала бы выделение и прокрутку —
/// то есть мешала бы ровно тогда, когда оператор всматривается в конкретный хоп.
/// </remarks>
public sealed partial class HopRowViewModel(int hop) : ObservableObject
{
    public int Hop { get; } = hop;

    [ObservableProperty]
    private string _address = "*";

    /// <summary>Имя и принадлежность — заполняется, когда проба закончит опрос.</summary>
    [ObservableProperty]
    private string? _annotation;

    [ObservableProperty]
    private string _sent = "—";

    [ObservableProperty]
    private string _loss = "—";

    /// <summary>Потери числом — для полоски рядом со значением.</summary>
    [ObservableProperty]
    private double _lossPercent;

    [ObservableProperty]
    private string _min = "—";

    [ObservableProperty]
    private string _median = "—";

    [ObservableProperty]
    private string _max = "—";

    [ObservableProperty]
    private string _jitter = "—";

    [ObservableProperty]
    private string _mos = "—";

    [ObservableProperty]
    private bool _isSilent;

    [ObservableProperty]
    private bool _isDestination;

    /// <summary>Хоп, с которого потери держатся до конца маршрута.</summary>
    [ObservableProperty]
    private bool _isDegradationPoint;

    /// <summary>Несколько адресов на одном хопе: смена маршрута или балансировка.</summary>
    [ObservableProperty]
    private string? _alternateAddresses;

    /// <summary>Подпись хопа, на котором цель ответила раньше конечной точки.</summary>
    [ObservableProperty]
    private string? _shortPathNote;

    public void Update(HopStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        Address = statistics.Address ?? "*";
        Sent = statistics.Sent.ToString(CultureInfo.InvariantCulture);
        IsSilent = statistics.IsSilent;
        IsDestination = statistics.IsDestination;

        // У хопа с ранним ответом цели в колонке потерь стоит прочерк: доля пакетов,
        // ушедших длинным путём, — не потери, и цифра здесь читалась бы как авария.
        ShortPathNote = statistics.IsEarlyDestination
            ? "цель коротким путём"
            : null;

        Loss = statistics.IsEarlyDestination
            ? "—"
            : statistics.LossPercent.ToString("0", CultureInfo.InvariantCulture) + " %";

        LossPercent = statistics.IsEarlyDestination ? 0 : statistics.LossPercent;

        AlternateAddresses = statistics.Addresses.Count > 1
            ? "также отвечали: " + string.Join(", ", statistics.Addresses.Where(a => a != Address))
            : null;

        if (statistics.IsSilent)
        {
            Min = Median = Max = Jitter = Mos = "—";
            return;
        }

        Min = Format(statistics.Statistics.MinMs);
        Median = Format(statistics.Statistics.P50Ms);
        Max = Format(statistics.Statistics.MaxMs);
        Jitter = Format(statistics.Statistics.JitterRfc3550Ms);
        Mos = double.IsNaN(statistics.Voice.Mos)
            ? "—"
            : statistics.Voice.Mos.ToString("0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Время без единицы: она названа один раз подписью под таблицей маршрута.
    /// </summary>
    /// <remarks>
    /// Точность — общая для продукта, из <see cref="Units"/>: до И-24+ хопы показывали
    /// три знака всегда, а «Задержка» рядом — по своему правилу.
    /// </remarks>
    private static string Format(double value) => Units.Number(value, MeasurementUnit.Milliseconds);
}
