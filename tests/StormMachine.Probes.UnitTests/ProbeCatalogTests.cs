using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки согласованности набора проб.
/// </summary>
/// <remarks>
/// Замысел «интерфейс строится по объявлению» работает ровно до тех пор, пока объявления
/// корректны. Ошибка в паспорте пробы — повторяющееся имя, параметр без значения по
/// умолчанию, границы наоборот — проявилась бы не при сборке, а при попытке запустить
/// команду. Эти тесты ловят такое на месте.
/// </remarks>
public sealed class ProbeCatalogTests
{
    private static IReadOnlyList<IProbe> CreateAllProbes()
    {
        var clock = new FakeClock();
        var environment = new FakeEnvironment();
        var resolver = new TargetResolver(environment);

        return
        [
            new IcmpProbe(clock, resolver),
            new TcpConnectProbe(clock, resolver),
            new UdpProbe(clock, resolver),
            new DnsProbe(clock, environment),
            new TlsProbe(clock),
            new HttpProbe(clock),
            new TracerouteProbe(clock, resolver),
        ];
    }

    [Fact]
    public void EveryProbe_HasUniqueName()
    {
        var names = CreateAllProbes().Select(p => p.Descriptor.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryProbe_HasUniqueKind()
    {
        var kinds = CreateAllProbes().Select(p => p.Descriptor.Kind).ToList();

        Assert.Equal(kinds.Count, kinds.Distinct().Count());
    }

    [Fact]
    public void EveryProbe_DeclaresMethodology()
    {
        foreach (var probe in CreateAllProbes())
        {
            var methodology = probe.Descriptor.Methodology;

            Assert.False(
                string.IsNullOrWhiteSpace(methodology.Reference) || methodology.Reference == "—",
                $"Проба «{probe.Descriptor.Name}» не указывает методику. "
                + "Отчёт без методики — просто картинка (требование C-08a).");
        }
    }

    [Fact]
    public void EveryParameter_HasDefaultValue()
    {
        // Значение по умолчанию обязательно: и командная строка, и будущая форма
        // в графическом клиенте строят поле ввода по объявлению, и им нужно что подставить.
        foreach (var probe in CreateAllProbes())
        {
            foreach (var parameter in probe.Descriptor.Parameters)
            {
                Assert.True(
                    parameter.DefaultValue is not null,
                    $"Параметр «{parameter.Name}» пробы «{probe.Descriptor.Name}» без значения по умолчанию.");
            }
        }
    }

    [Fact]
    public void EveryNumericParameter_HasSaneBounds()
    {
        foreach (var probe in CreateAllProbes())
        {
            foreach (var parameter in probe.Descriptor.Parameters)
            {
                if (parameter.Type is ProbeParameterType.Boolean or ProbeParameterType.Text or ProbeParameterType.Choice)
                {
                    continue;
                }

                Assert.True(parameter.Minimum.HasValue, $"«{probe.Descriptor.Name}.{parameter.Name}»: нет нижней границы.");
                Assert.True(parameter.Maximum.HasValue, $"«{probe.Descriptor.Name}.{parameter.Name}»: нет верхней границы.");
                Assert.True(
                    parameter.Minimum < parameter.Maximum,
                    $"«{probe.Descriptor.Name}.{parameter.Name}»: границы перепутаны местами.");

                var defaultValue = Convert.ToDouble(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(defaultValue, parameter.Minimum!.Value, parameter.Maximum!.Value);
            }
        }
    }

    [Fact]
    public void Validate_RejectsValueBelowMinimum()
    {
        var probe = new IcmpProbe(new FakeClock(), new TargetResolver(new FakeEnvironment()));

        var errors = probe.Validate(new ProbeRequest
        {
            Target = Target.Ip("127.0.0.1"),
            Parameters = new Dictionary<string, object?> { ["count"] = 0 },
        });

        Assert.Contains(errors, e => e.ParameterName == "count");
    }

    [Fact]
    public void Validate_AcceptsEmptyParameters()
    {
        // Пустой запрос должен проходить: у всех параметров есть значения по умолчанию.
        foreach (var probe in CreateAllProbes())
        {
            var errors = probe.Validate(new ProbeRequest { Target = Target.Host("example.com") });

            Assert.True(
                errors.Count == 0,
                $"Проба «{probe.Descriptor.Name}» отвергла запрос без параметров: "
                + string.Join(", ", errors.Select(e => $"{e.ParameterName}: {e.Message}")));
        }
    }

    [Fact]
    public void DnsProbe_RejectsGatewayTarget()
    {
        // Цель DNS-пробы — имя для разрешения, а не адрес назначения.
        // Эта проба единственная в наборе, где цель означает другое.
        var probe = new DnsProbe(new FakeClock(), new FakeEnvironment());

        var errors = probe.Validate(new ProbeRequest { Target = Target.Gateway("шлюз") });

        Assert.Contains(errors, e => e.ParameterName == "target");
    }

    [Fact]
    public void ShapesCoverAllFourFamilies()
    {
        // Итерация И-2 существует ради вывода: формы результата несводимы друг к другу.
        // Если однажды все пробы окажутся одной формы, вывод стоит перепроверить.
        var shapes = CreateAllProbes().Select(p => p.Descriptor.Shape).Distinct().ToList();

        Assert.Contains(ProbeResultShape.ScalarSeries, shapes);
        Assert.Contains(ProbeResultShape.PhasedTiming, shapes);
        Assert.Contains(ProbeResultShape.ComparedSeries, shapes);
        Assert.Contains(ProbeResultShape.PathTrace, shapes);
    }

    private sealed class FakeClock : IHighResolutionClock
    {
        public double ResolutionNanoseconds => 100;

        public double CalibrationBaselineMs => 0;

        public long GetTimestamp() => 0;

        public double ElapsedMilliseconds(long startTimestamp) => 0;

        public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) => 0;

        public Task CalibrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : INetworkEnvironment
    {
        public bool IsElevated => false;

        public IReadOnlyList<NetworkAdapter> GetAdapters() => [];

        public NetworkAdapter? GetPrimaryAdapter() => null;
    }
}
