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
        "StormMachine.Snmp",
        "StormMachine.Agents",
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

    [Fact(DisplayName = "Правило 1а: Protocol не зависит от проектов")]
    public void Protocol_HasNoProjectDependencies()
    {
        var protocol = RepositoryLayout.FindProject("StormMachine.Protocol");
        Assert.NotNull(protocol);

        Assert.True(
            protocol.ProjectReferences.Count == 0,
            $"Protocol ссылается на проекты: {string.Join(", ", protocol.ProjectReferences)}. "
            + "Формат провода обязан пережить любую перестройку доменной модели, "
            + "а агент — собираться в маленький самостоятельный бинарь.");
    }

    [Fact(DisplayName = "Правило 1б: агент знает только протокол")]
    public void Agent_ReferencesOnlyProtocol()
    {
        var agent = RepositoryLayout.FindProject("StormMachine.Agent");
        Assert.NotNull(agent);

        var unexpected = agent.ProjectReferences
            .Where(r => r != "StormMachine.Protocol")
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Агент ссылается на посторонние проекты: {string.Join(", ", unexpected)}. "
            + "Он живёт на чужой машине и обязан оставаться портативным: продукт целиком "
            + "туда не поедет.");
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

    [Fact(DisplayName = "Правило 2а: каналы оповещения — обычная инфраструктура")]
    public void Alerting_ReferencesOnlyApplication()
    {
        var alerting = RepositoryLayout.FindProject("StormMachine.Alerting");
        Assert.NotNull(alerting);

        var unexpected = alerting.ProjectReferences
            .Where(r => r != "StormMachine.Application")
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Alerting ссылается на посторонние проекты: {string.Join(", ", unexpected)}. "
            + "Каналы — реализация порта IAlertChannel и знают ровно столько же, "
            + "сколько хранилище или пробы.");
    }

    [Fact(DisplayName = "Правило 2б: захват пакетов живёт только в плагине")]
    public void Capture_LivesOnlyInPlugins()
    {
        // Условие приёмки И-18. Уровень 2 необязателен: продукт обязан работать
        // полностью и без драйвера захвата, который не входит в поставку ни при каких
        // условиях. Ссылка на SharpPcap, просочившаяся в src/, сделала бы захват
        // частью ядра — и первое же обращение к нему у пользователя без Npcap
        // перестало бы быть его добровольным выбором.
        var leaks = RepositoryLayout.SourceProjects
            .Where(p => p.PackageReferences.Any(r =>
                r.Contains("SharpPcap", StringComparison.OrdinalIgnoreCase)
                || r.Contains("PacketDotNet", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"Захват пакетов просочился в src/: {string.Join(", ", leaks)}. "
            + "SharpPcap разрешён только в plugins/.");

        var npcap = RepositoryLayout.PluginProjects
            .FirstOrDefault(p => p.Name == "StormMachine.Capture.Npcap");

        Assert.NotNull(npcap);

        Assert.Contains(
            npcap!.PackageReferences,
            r => r.Contains("SharpPcap", StringComparison.OrdinalIgnoreCase));

        // Разбор кадров общий для источников: байты одни и те же, кто бы их
        // ни принёс. Поэтому библиотека разбора живёт ровно в одном проекте —
        // иначе разборов стало бы два, и однажды они разошлись бы.
        var parsers = RepositoryLayout.PluginProjects
            .Where(p => p.PackageReferences.Any(r =>
                r.Contains("PacketDotNet", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["StormMachine.Capture.Frames"], parsers);

        // Встроенный в Windows источник не тянет ничего: pktmon входит в систему,
        // а кадры разбирает общий проект. Появившаяся здесь зависимость означала бы,
        // что уровень 2 опять чего-то требует от машины оператора.
        var builtIn = RepositoryLayout.PluginProjects
            .FirstOrDefault(p => p.Name == "StormMachine.Capture.PktMon");

        Assert.NotNull(builtIn);
        Assert.Empty(builtIn!.PackageReferences);

        // Плагины видят слой приложения и общий разбор — и ничего сверх того:
        // реализовать порт и разобрать байты — вся их работа.
        var unexpected = RepositoryLayout.PluginProjects
            .SelectMany(p => p.ProjectReferences.Select(r => (Plugin: p.Name, Reference: r)))
            .Where(x => !x.Reference.Contains("StormMachine.Application", StringComparison.Ordinal)
                        && !x.Reference.Contains("StormMachine.Capture.Frames", StringComparison.Ordinal))
            .Select(x => $"{x.Plugin} → {x.Reference}")
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Плагин захвата ссылается на посторонние проекты: {string.Join(", ", unexpected)}.");
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

    /// <summary>
    /// Рисование живёт только в окнах.
    /// </summary>
    /// <remarks>
    /// Окон с И-26 два: клиент оператора и окно агента. Второе появилось потому,
    /// что человек на чужой площадке участвует в измерении наравне с оператором
    /// и видел при этом бегущие строки консоли.
    /// <para>
    /// Правило от этого не ослабло, а стало точнее: Avalonia разрешена проектам,
    /// которые и есть окна, и запрещена всем остальным. Ядро, инфраструктура и сам
    /// агент о рисовании по-прежнему не знают — агент собирается под пять платформ,
    /// и шестьдесят мегабайт Skia в каждой сборке платили бы все за удобство одной.
    /// </para>
    /// </remarks>
    [Fact(DisplayName = "Правило 5: Avalonia только в окнах")]
    public void Avalonia_StaysInGuiProjects()
    {
        string[] windows = ["StormMachine.App", "StormMachine.Agent.Gui"];

        var offenders = RepositoryLayout.SourceProjects
            .Where(p => !windows.Contains(p.Name, StringComparer.Ordinal))
            .Where(p => p.PackageReferences.Any(pkg => pkg.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Ссылка на Avalonia вне окон: {string.Join(", ", offenders)}. "
            + "Ядро не должно знать о существовании UI.");

        // Окно агента видит агента — и ничего больше. Ссылка на ядро продукта
        // сделала бы из него второй клиент: он показывает ту точку измерения,
        // рядом с которой запущен, а не чужие измерения и не историю оператора.
        var window = RepositoryLayout.SourceProjects
            .FirstOrDefault(p => p.Name == "StormMachine.Agent.Gui");

        Assert.NotNull(window);

        var unexpected = window!.ProjectReferences
            .Where(r => !string.Equals(r, "StormMachine.Agent", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Окно агента ссылается на лишнее: {string.Join(", ", unexpected)}.");
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

    [Fact(DisplayName = "Правило 8: у каждой страницы есть представление")]
    public void EveryPageViewModel_IsMappedInViewLocator()
    {
        // Ошибка, которую иначе не поймать до запуска: новая страница добавлена,
        // а сопоставление с представлением забыто. Проявилось бы надписью
        // «Нет представления для …» у пользователя, а не при сборке.
        var locatorPath = RepositoryLayout
            .SourceFiles("src")
            .FirstOrDefault(f => Path.GetFileName(f) == "ViewLocator.cs");

        Assert.True(locatorPath is not null, "Не найден ViewLocator.cs");

        var locator = File.ReadAllText(locatorPath!);

        var pageViewModels = RepositoryLayout
            .SourceFiles("src")
            .SelectMany(file => System.Text.RegularExpressions.Regex
                .Matches(RepositoryLayout.StripComments(File.ReadAllText(file)), @"class\s+(\w*PageViewModel)\b")
                .Select(m => m.Groups[1].Value))
            .Where(name => name != "PageViewModel")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(pageViewModels);

        var missing = pageViewModels
            .Where(name => !locator.Contains(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Страницы без представления в ViewLocator: {string.Join(", ", missing)}.");
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
                     "StormMachine.Protocol",
                     "StormMachine.Agent",
                     "StormMachine.Alerting",
                 })
        {
            Assert.True(
                RepositoryLayout.FindProject(required) is not null,
                $"Не найден обязательный проект {required}");
        }
    }
}
