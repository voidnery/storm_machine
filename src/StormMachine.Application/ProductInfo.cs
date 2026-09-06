using System.Reflection;

namespace StormMachine.Application;

/// <summary>Сведения о продукте, попадающие в результаты измерений и в отчёты.</summary>
public static class ProductInfo
{
    public const string Name = "Storm Machine";

    /// <summary>
    /// Версия из атрибутов сборки. Источник — <c>&lt;Version&gt;</c> в Directory.Build.props,
    /// тот же, из которого хук pre-push создаёт тег релиза.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(ProductInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// Кто выпускает продукт.
    /// </summary>
    /// <remarks>
    /// Одно место на всё: подпись в строке состояния окна, автор пакета в установщике
    /// и сведения о сборке в свойствах файла. Строка приходит из <c>Directory.Build.props</c>
    /// (<c>Authors</c>) — той же, из которой её берёт упаковщик, — и потому не может
    /// разойтись с тем, что видно в установщике.
    /// </remarks>
    public static string Vendor { get; } =
        typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyCompanyAttribute>()?.Company is { Length: > 0 } company
            ? company
            : "Eterix Tech - by voidnery";

    public static string NameAndVersion => $"{Name} {Version}";

    /// <summary>Подпись для угла окна: продукт, версия и кто его выпускает.</summary>
    public static string Signature => $"{NameAndVersion} · {Vendor}";
}
