using StormMachine.Domain.Outside;

namespace StormMachine.Application.Abstractions;

/// <summary>Что и куда спрашивать, чтобы увидеть сеть снаружи.</summary>
public sealed record OutsideRequest
{
    /// <summary>Серверы STUN. Пусто — список по умолчанию из реализации.</summary>
    public IReadOnlyList<string> StunServers { get; init; } = [];

    /// <summary>Имя, на котором проверяется готовность к IPv6.</summary>
    public string Ipv6Target { get; init; } = "example.com";

    public int TimeoutMs { get; init; } = 2000;

    /// <summary>Проверять готовность к IPv6. Отдельным флагом: это ещё одно обращение наружу.</summary>
    public bool CheckIpv6 { get; init; } = true;
}

/// <summary>
/// Взгляд на сеть снаружи: внешний адрес, трансляция, принадлежность, IPv6.
/// </summary>
/// <remarks>
/// Порт отдельный от <see cref="IProbe"/> намеренно: у этой проверки нет ряда измерений
/// и нет длительности как предмета интереса. Её результат — набор утверждений о состоянии,
/// а не выборка чисел, и загонять его в форму пробы значило бы завести пятую форму
/// результата ради одного случая.
/// <para>
/// Обращается к чужим серверам. Поэтому вызывается только по явной команде оператора
/// и никогда — фоном.
/// </para>
/// </remarks>
public interface IOutsideView
{
    Task<OutsideView> LookAsync(OutsideRequest request, CancellationToken cancellationToken = default);
}
