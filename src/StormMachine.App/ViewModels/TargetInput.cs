using StormMachine.Domain.Targets;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Разбор цели, введённой в поле экрана.
/// </summary>
/// <remarks>
/// Одно правило — одно место (как <c>IpAddressOrder</c>): «gateway» и «шлюз»
/// означают динамическую цель, остальное разбирает домен. До И-24 это правило
/// жило двумя копиями в страницах задержки и пути; третья копия для прогонщика
/// проб стала бы поводом им разойтись.
/// </remarks>
internal static class TargetInput
{
    public static Target Parse(string raw)
    {
        var trimmed = raw.Trim();

        return trimmed.Equals("gateway", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("шлюз", StringComparison.OrdinalIgnoreCase)
            ? Target.Gateway("шлюз по умолчанию")
            : Target.Parse(trimmed);
    }
}
