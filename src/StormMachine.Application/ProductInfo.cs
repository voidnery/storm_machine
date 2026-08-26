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

    public static string NameAndVersion => $"{Name} {Version}";
}
