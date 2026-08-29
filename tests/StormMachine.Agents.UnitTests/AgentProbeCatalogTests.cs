using StormMachine.Agents;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Agents;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Agents.UnitTests;

/// <summary>
/// Согласованность паспортов проб, требующих агента.
/// </summary>
/// <remarks>
/// Такие же проверки давно стоят у семи проб из <c>StormMachine.Probes</c>, а эти три
/// под них не попадали: они живут в другом проекте, у которого до И-19 не было тестов
/// вовсе. Между тем именно им проверка нужнее — запустить их руками нельзя без второй
/// машины с сопряжённым агентом, и ошибка в паспорте всплыла бы позже всего.
/// <para>
/// Замысел «интерфейс строится по объявлению» (принцип 1) работает ровно до тех пор,
/// пока объявления корректны: параметр без значения по умолчанию — это пустое поле
/// в форме, границы наоборот — ползунок, который никуда не двигается.
/// </para>
/// </remarks>
public sealed class AgentProbeCatalogTests
{
    private static IReadOnlyList<IProbe> CreateAgentProbes()
    {
        var directory = new AgentDirectory(new EmptyAgentStore());
        var registry = new Lazy<IProbeRegistry>(() => new EmptyRegistry());

        return
        [
            new ThroughputProbe(directory),
            new ChannelQualityProbe(directory),
            new BufferbloatProbe(directory, registry),
        ];
    }

    [Fact]
    public void EveryProbe_HasUniqueName()
    {
        var names = CreateAgentProbes().Select(p => p.Descriptor.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Каждая проба называет методику.
    /// </summary>
    /// <remarks>
    /// Отчёт со ссылкой на стандарт — аргумент в споре с провайдером, отчёт
    /// без методики — просто картинка.
    /// </remarks>
    [Fact]
    public void EveryProbe_NamesItsMethodology()
    {
        foreach (var probe in CreateAgentProbes())
        {
            var methodology = probe.Descriptor.Methodology;

            Assert.False(
                string.IsNullOrWhiteSpace(methodology.Name),
                $"Проба «{probe.Descriptor.Name}» не указывает методику.");

            Assert.False(
                string.IsNullOrWhiteSpace(methodology.Reference),
                $"Методика пробы «{probe.Descriptor.Name}» без ссылки на стандарт.");
        }
    }

    /// <summary>Пустое поле в форме — не вопрос оператору, а требование выдумать ответ.</summary>
    [Fact]
    public void EveryParameter_HasADefault()
    {
        foreach (var probe in CreateAgentProbes())
        {
            foreach (var parameter in probe.Descriptor.Parameters)
            {
                Assert.True(
                    parameter.DefaultValue is not null,
                    $"Параметр «{parameter.Name}» пробы «{probe.Descriptor.Name}» без значения по умолчанию.");
            }
        }
    }

    /// <summary>Границы не должны стоять наоборот, а значение по умолчанию — вне них.</summary>
    [Fact]
    public void EveryParameter_HasSaneBounds()
    {
        foreach (var probe in CreateAgentProbes())
        {
            foreach (var parameter in probe.Descriptor.Parameters)
            {
                if (parameter.Minimum is not { } min || parameter.Maximum is not { } max)
                {
                    continue;
                }

                Assert.True(
                    min < max,
                    $"«{probe.Descriptor.Name}.{parameter.Name}»: нижняя граница не меньше верхней.");

                if (parameter.DefaultValue is not null
                    && double.TryParse(
                        Convert.ToString(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value))
                {
                    Assert.True(
                        value >= min && value <= max,
                        $"«{probe.Descriptor.Name}.{parameter.Name}»: значение по умолчанию вне границ.");
                }
            }
        }
    }

    /// <summary>Параметр выбора обязан перечислить, из чего выбирают.</summary>
    [Fact]
    public void EveryChoiceParameter_ListsItsChoices()
    {
        foreach (var probe in CreateAgentProbes())
        {
            foreach (var parameter in probe.Descriptor.Parameters
                         .Where(p => p.Type == ProbeParameterType.Choice))
            {
                Assert.True(
                    parameter.Choices is { Count: > 0 },
                    $"«{probe.Descriptor.Name}.{parameter.Name}»: выбор без вариантов.");

                Assert.Contains(
                    Convert.ToString(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture),
                    parameter.Choices!);
            }
        }
    }

    /// <summary>Каждый параметр объяснён: форма строится по объявлению, и подпись — часть его.</summary>
    [Fact]
    public void EveryParameter_IsExplained()
    {
        foreach (var probe in CreateAgentProbes())
        {
            foreach (var parameter in probe.Descriptor.Parameters)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(parameter.Label),
                    $"«{probe.Descriptor.Name}.{parameter.Name}»: нет подписи.");

                Assert.False(
                    string.IsNullOrWhiteSpace(parameter.Description),
                    $"«{probe.Descriptor.Name}.{parameter.Name}»: нет объяснения.");
            }
        }
    }

    /// <summary>
    /// Все три объявляют, что им нужен агент.
    /// </summary>
    /// <remarks>
    /// По этому флагу клиент решает, показывать ли пробу как доступную. Проба,
    /// забывшая его выставить, предложит оператору измерение, которое заведомо
    /// не состоится.
    /// </remarks>
    [Fact]
    public void EveryProbe_DeclaresThatItNeedsAnAgent()
    {
        foreach (var probe in CreateAgentProbes())
        {
            Assert.True(
                probe.Descriptor.RequiresAgent,
                $"Проба «{probe.Descriptor.Name}» требует агента, но не говорит об этом.");
        }
    }

    /// <summary>
    /// Ни одна не требует прав администратора.
    /// </summary>
    /// <remarks>
    /// Измерение до агента идёт обычными сокетами. Заявленные лишние права заставили бы
    /// оператора перезапускать продукт без причины.
    /// </remarks>
    [Fact]
    public void NoProbe_AsksForElevationItDoesNotNeed()
    {
        foreach (var probe in CreateAgentProbes())
        {
            Assert.False(
                probe.Descriptor.RequiresElevation,
                $"Проба «{probe.Descriptor.Name}» просит права администратора без нужды.");
        }
    }

    /// <summary>
    /// Целью может быть только имя агента, а не подсеть или шлюз.
    /// </summary>
    /// <remarks>
    /// Измерение идёт между двумя точками, и вторая — сопряжённый агент. Подсеть
    /// как цель здесь бессмысленна, и сказать об этом надо при проверке параметров,
    /// а не отказом посреди прогона.
    /// </remarks>
    [Theory]
    [InlineData(TargetKind.Subnet)]
    [InlineData(TargetKind.DefaultGateway)]
    public void Probes_RejectTargetsThatCannotBeAnAgent(TargetKind kind)
    {
        var target = kind == TargetKind.Subnet
            ? Target.Subnet("192.168.1.0/24")
            : Target.Gateway();

        foreach (var probe in CreateAgentProbes())
        {
            var errors = probe.Validate(new ProbeRequest
            {
                Target = target,
                Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            });

            Assert.True(
                errors.Count > 0,
                $"Проба «{probe.Descriptor.Name}» приняла цель {kind}, которой не может быть агент.");
        }
    }

    private sealed class EmptyRegistry : IProbeRegistry
    {
        public IReadOnlyList<ProbeDescriptor> Descriptors => [];

        public bool TryGet(string name, out IProbe found)
        {
            found = null!;

            return false;
        }
    }

    private sealed class EmptyAgentStore : IAgentStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<byte[]?> LoadIdentityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task SaveIdentityAsync(byte[] container, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteAgent>>([]);

        public Task<RemoteAgent?> FindAsync(string thumbprintOrName, CancellationToken cancellationToken = default) =>
            Task.FromResult<RemoteAgent?>(null);

        public Task SaveAsync(RemoteAgent agent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ForgetAsync(string thumbprint, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
