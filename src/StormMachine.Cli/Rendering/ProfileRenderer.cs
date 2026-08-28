using System.Globalization;
using StormMachine.Domain.Profiles;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ профилей окружения.
/// </summary>
/// <remarks>
/// Приметы текущей сети печатаются рядом со списком: без них непонятно, почему
/// продукт узнаёт одно место и не узнаёт другое, и оператор остаётся с догадкой
/// вместо объяснения.
/// </remarks>
internal static class ProfileRenderer
{
    public static void WriteList(IReadOnlyList<NetworkProfile> profiles, NetworkSignature current)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(current);

        Console.WriteLine($"Сейчас вокруг: {current.Describe()}");
        Console.WriteLine();

        if (profiles.Count == 0)
        {
            Console.WriteLine("Профилей нет. Продукт работает и без них — но тогда в журнале");
            Console.WriteLine("не будет видно, из какого места сделано измерение.");
            Console.WriteLine();
            Console.WriteLine("Запомнить это место:");
            Console.WriteLine("  storm profiles add \"офис\" --отсюда --порог \"p95 < 50\"");

            return;
        }

        Console.WriteLine($"  {"профиль",-22} {"состав",-34} приметы");

        foreach (var profile in profiles)
        {
            var mark = profile.IsActive ? "*" : " ";

            Console.WriteLine(
                $"{mark} {Cut(profile.Name, 22),-22} {Cut(profile.Describe(), 34),-34} "
                + Cut(profile.Signature.Describe(), 40));
        }

        Console.WriteLine();

        if (!profiles.Any(p => p.IsActive))
        {
            Console.WriteLine("Активного профиля нет: измерения идут без пометки о месте.");
        }
    }

    public static void WriteDetails(NetworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Console.WriteLine();
        Console.WriteLine($"Профиль   : {profile.Name}{(profile.IsActive ? "  (активен)" : string.Empty)}");

        if (!string.IsNullOrWhiteSpace(profile.Description))
        {
            Console.WriteLine($"Описание  : {profile.Description}");
        }

        Console.WriteLine($"Приметы   : {profile.Signature.Describe()}");

        if (profile.Targets.Count > 0)
        {
            Console.WriteLine($"Цели      : {string.Join(", ", profile.Targets)}");
        }

        if (profile.Thresholds.Count > 0)
        {
            Console.WriteLine($"Пороги    : {string.Join(", ", profile.Thresholds.Select(t => t.Describe()))}");
        }

        Console.WriteLine(profile.Monitors.Count > 0
            ? $"Мониторов : {profile.Monitors.Count.ToString(CultureInfo.InvariantCulture)}"
            : "Мониторов : нет");

        Console.WriteLine();
    }

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "\u2026";
}
