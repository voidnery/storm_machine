namespace StormMachine.Domain.Measurements;

/// <summary>
/// Как продукт называет тип сетевого адаптера.
/// </summary>
/// <remarks>
/// Словарь один на консоль, окно и отчёт — по той же причине, что
/// <see cref="Units"/> и <see cref="StormMachine.Domain.Results.VerdictWording"/>:
/// расхождение стоит доверия. К И-24+ копий было семь, и они уже разошлись —
/// четыре печатали «не определён», три «тип не определён». Слово это попадает
/// и в PDF-отчёт заказчику, и в строку состояния клиента, и в вывод <c>storm env</c>;
/// разные слова там означают для читателя разные вещи.
/// <para>
/// Тип — не украшение: через виртуальный коммутатор p99 на стенде оказался в 18 раз
/// выше p50, и именно это слово объясняет оператору, почему числа такие.
/// </para>
/// </remarks>
public static class AdapterWording
{
    /// <summary>Название типа: «физический», «виртуальный коммутатор».</summary>
    public static string Kind(AdapterKind kind) => kind switch
    {
        AdapterKind.Physical => "физический",
        AdapterKind.Wireless => "беспроводной",
        AdapterKind.Virtual => "виртуальный коммутатор",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => "туннель",
        AdapterKind.Loopback => "loopback",
        _ => "тип не определён",
    };

    /// <summary>
    /// Вносит ли такой адаптер собственную задержку и дрожание.
    /// </summary>
    /// <remarks>
    /// Один и тот же список из трёх типов проверялся в четырёх местах вручную.
    /// Появится седьмой тип — решение о доверии к нему принимается здесь, а не
    /// в четырёх независимых условиях, из которых обновят три.
    /// </remarks>
    public static bool IsUntrustworthy(AdapterKind kind) =>
        kind is AdapterKind.Virtual or AdapterKind.Vpn or AdapterKind.Tunnel;
}
