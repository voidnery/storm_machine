namespace StormMachine.ArchTests;

/// <summary>
/// Архитектурные правила как тесты.
/// </summary>
/// <remarks>
/// Правило, записанное только в документе, нарушается через полгода и никто этого не замечает.
/// Здесь оно роняет сборку. Источник правил — docs/ARCHITECTURE.md §3 и
/// docs/01-analysis.md §8.2.
/// </remarks>
public sealed class ArchitectureRulesTests
{
    /// <summary>
    /// Пакеты, разрешённые слою приложения. Только абстракции: конкретные реализации
    /// внедряются из корня композиции.
    /// </summary>
    private static readonly string[] ApplicationAllowedPackages =
    [
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
    ];

    /// <summary>Проекты инфраструктуры, о которых слой представления знать не должен.</summary>
    private static readonly string[] InfrastructureProjects =
    [
        "StormMachine.Probes",
        "StormMachine.Storage",
        "StormMachine.Platform",
        "StormMachine.Discovery",
        "StormMachine.Reporting",
        "StormMachine.Scheduling",
    ];

    [Fact(DisplayName = "Правило 1: Domain не зависит ни от чего")]
    public void Domain_HasNoDependencies()
    {
        var domain = RepositoryLayout.FindProject("StormMachine.Domain");
        Assert.NotNull(domain);

        Assert.True(
            domain.ProjectReferences.Count == 0,
            $"Domain ссылается на проекты: {string.Join(", ", domain.ProjectReferences)}. "
            + "Доменная модель обязана оставаться независимой.");

        Assert.True(
            domain.PackageReferences.Count == 0,
            $"Domain ссылается на пакеты: {string.Join(", ", domain.PackageReferences)}. "
            + "Ноль внешних зависимостей — условие того, что модель переживёт смену любой библиотеки.");
    }

    [Fact(DisplayName = "Правило 2: Application ссылается только на Domain")]
    public void Application_ReferencesOnlyDomain()
    {
        var application = RepositoryLayout.FindProject("StormMachine.Application");
        Assert.NotNull(application);

        var unexpected = application.ProjectReferences
            .Where(r => r != "StormMachine.Domain")
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Application ссылается на посторонние проекты: {string.Join(", ", unexpected)}. "
            + "Зависимости направлены внутрь: инфраструктура реализует порты, а не наоборот.");

        var forbiddenPackages = application.PackageReferences
            .Where(p => !ApplicationAllowedPackages.Contains(p, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            forbiddenPackages.Count == 0,
            $"Application ссылается на пакеты вне списка разрешённых: {string.Join(", ", forbiddenPackages)}. "
            + $"Разрешены только абстракции: {string.Join(", ", ApplicationAllowedPackages)}.");
    }

    [Fact(DisplayName = "Правило 3: представление не знает об инфраструктуре")]
    public void Presentation_DoesNotReferenceInfrastructure()
    {
        foreach (var name in new[] { "StormMachine.App", "StormMachine.Cli" })
        {
            var project = RepositoryLayout.FindProject(name);
            if (project is null)
            {
                continue;
            }

            var leaks = project.ProjectReferences
                .Where(r => InfrastructureProjects.Contains(r, StringComparer.Ordinal))
                .ToList();

            Assert.True(
                leaks.Count == 0,
                $"{name} напрямую ссылается на инфраструктуру: {string.Join(", ", leaks)}. "
                + "Доступ только через Application и внедрение зависимостей — иначе ядро прирастёт к интерфейсу "
                + "и server-вариант потребует переписывания.");
        }
    }

    [Fact(DisplayName = "Правило 4: SharpPcap только в plugins/")]
    public void CaptureLibraries_StayInPlugins()
    {
        var offenders = RepositoryLayout.SourceProjects
            .Where(p => p.PackageReferences.Any(pkg =>
                pkg.Contains("SharpPcap", StringComparison.OrdinalIgnoreCase)
                || pkg.Contains("PacketDotNet", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Библиотеки захвата пакетов просочились в src/: {string.Join(", ", offenders)}. "
            + "Они под LGPL-3.0 и должны оставаться отдельной опциональной сборкой в plugins/ — "
            + "это одновременно изоляция лицензии и работоспособность trimming при публикации.");
    }

    [Fact(DisplayName = "Правило 5: Avalonia только в StormMachine.App")]
    public void Avalonia_StaysInGuiProject()
    {
        var offenders = RepositoryLayout.SourceProjects
            .Where(p => p.Name != "StormMachine.App")
            .Where(p => p.PackageReferences.Any(pkg => pkg.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Ссылка на Avalonia вне графического клиента: {string.Join(", ", offenders)}. "
            + "Ядро не должно знать о существовании UI.");
    }

    [Fact(DisplayName = "Правило 6: значения задержки из системных API не используются")]
    public void SystemProvidedLatency_IsNeverUsed()
    {
        // PingReply.RoundtripTime возвращает ЦЕЛЫЕ миллисекунды. На стенде это дало
        // 6 различимых значений на 300 проб против 285 у собственного таймера.
        // В локальной сети такой источник превращает джиттер, PDV и MOS в мусор.
        // docs/02-research.md, R-10; принцип 8 в docs/01-analysis.md §8.2.
        var offenders = new List<string>();

        foreach (var file in RepositoryLayout.SourceFiles("src"))
        {
            // Комментарии не в счёт: упоминание запрещённого API в пояснении, почему он
            // запрещён, — это документация, а не нарушение.
            var code = RepositoryLayout.StripComments(File.ReadAllText(file));
            if (code.Contains("RoundtripTime", StringComparison.Ordinal))
            {
                offenders.Add(RepositoryLayout.Relative(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Использование PingReply.RoundtripTime: {string.Join(", ", offenders)}. "
            + "Задержку измеряем только собственным таймером высокого разрешения (IHighResolutionClock). "
            + "Системный API даёт целые миллисекунды и в локальной сети округляет почти всё в ноль.");
    }

    [Fact(DisplayName = "Правило 7: QuestPDF не выходит за пределы Reporting")]
    public void QuestPdf_StaysInReporting()
    {
        var offenders = RepositoryLayout.SourceProjects
            .Where(p => p.Name != "StormMachine.Reporting")
            .Where(p => p.PackageReferences.Any(pkg => pkg.Contains("QuestPDF", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"QuestPDF используется вне Reporting: {string.Join(", ", offenders)}. "
            + "Движок отчётов спрятан за IReportRenderer, чтобы его замена стоила день, а не месяц.");
    }

    [Fact(DisplayName = "Структура репозитория на месте")]
    public void Repository_HasExpectedProjects()
    {
        Assert.NotEmpty(RepositoryLayout.SourceProjects);

        foreach (var required in new[]
                 {
                     "StormMachine.Domain",
                     "StormMachine.Application",
                     "StormMachine.Cli",
                     "StormMachine.App",
                 })
        {
            Assert.True(
                RepositoryLayout.FindProject(required) is not null,
                $"Не найден обязательный проект {required}");
        }
    }
}
