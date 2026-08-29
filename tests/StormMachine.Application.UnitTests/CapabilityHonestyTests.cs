using StormMachine.Application.Abstractions;
using StormMachine.Application.Capabilities;
using StormMachine.Domain.Capabilities;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Экран возможностей не должен врать.
/// </summary>
/// <remarks>
/// Он ценен ровно настолько, насколько ему можно верить, и устаревшая строка на нём
/// хуже отсутствующей. Ловили дважды: после И-17 SNMP там всё ещё числился
/// «запланировано, появится в И-17», а карта сети обещала уровень 1 уже после того,
/// как он вышел. Оба раза замечено глазами при работе над следующей итерацией —
/// то есть могло и не быть замечено.
/// <para>
/// Проверки ниже закрывают не тот конкретный текст, а <b>класс отказа</b>: обещание
/// будущего рядом с тем, что уже работает, и молчание там, где надо объяснить.
/// </para>
/// </remarks>
public sealed class CapabilityHonestyTests
{
    /// <summary>
    /// «Появится в итерации N» стоит только у запланированного.
    /// </summary>
    /// <remarks>
    /// Ровно эта строка и устаревала дважды: возможность выходила, состояние ей меняли,
    /// а обещание рядом забывали убрать. Инвариант был записан комментарием у поля
    /// <c>Iteration</c> — «только для запланированных», — но ничем не удерживался.
    /// </remarks>
    [Fact]
    public async Task WorkingCapability_DoesNotPromiseItsOwnArrival()
    {
        var report = await InspectAsync();

        var lying = report.Capabilities
            .Where(c => c.State != CapabilityState.Planned && c.Iteration is not null)
            .Select(c => $"{c.Id} ({c.State}) обещает «появится в итерации {c.Iteration}»")
            .ToList();

        Assert.True(
            lying.Count == 0,
            "Возможность обещает своё появление, хотя уже не запланирована:"
            + Environment.NewLine + string.Join(Environment.NewLine, lying));
    }

    /// <summary>
    /// Недоступное объясняется, а не просто помечается недоступным.
    /// </summary>
    /// <remarks>
    /// UX-принцип 6: спрятанная или необъяснённая возможность выглядит как
    /// отсутствующая. Оператору нужно знать, что именно сделать, — иначе он пойдёт
    /// искать другой инструмент.
    /// </remarks>
    [Fact]
    public async Task BlockedCapability_SaysWhatToDoAboutIt()
    {
        var report = await InspectAsync();

        var silent = report.Capabilities
            .Where(c => !c.IsUsable && c.State != CapabilityState.Planned)
            .Where(c => string.IsNullOrWhiteSpace(c.HowToEnable))
            .Select(c => $"{c.Id} ({c.State})")
            .ToList();

        Assert.True(
            silent.Count == 0,
            "Возможность недоступна и не говорит, что сделать: "
            + string.Join(", ", silent));
    }

    /// <summary>Запланированное называет итерацию — иначе это обещание без срока.</summary>
    [Fact]
    public async Task PlannedCapability_NamesTheIteration()
    {
        var report = await InspectAsync();

        var vague = report.Capabilities
            .Where(c => c.State == CapabilityState.Planned && string.IsNullOrWhiteSpace(c.Iteration))
            .Select(c => c.Id)
            .ToList();

        Assert.True(vague.Count == 0, "Запланировано без срока: " + string.Join(", ", vague));
    }

    /// <summary>У каждой возможности есть смысл на языке задачи, а не механизма.</summary>
    [Fact]
    public async Task EveryCapability_ExplainsWhatItGives()
    {
        var report = await InspectAsync();

        Assert.NotEmpty(report.Capabilities);

        foreach (var capability in report.Capabilities)
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.Title), $"{capability.Id}: нет названия");
            Assert.False(string.IsNullOrWhiteSpace(capability.About), $"{capability.Id}: нет объяснения");
        }
    }

    /// <summary>Одинаковых идентификаторов быть не должно: строка задвоится в списке.</summary>
    [Fact]
    public async Task CapabilityIds_AreUnique()
    {
        var report = await InspectAsync();

        var duplicates = report.Capabilities
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Задвоенные возможности: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// Без драйвера уровень 2 не объявляется доступным.
    /// </summary>
    /// <remarks>
    /// Продукт не распространяет Npcap ни при каких условиях и обязан честно
    /// показывать его отсутствие: именно так его увидит большинство пользователей.
    /// </remarks>
    [Fact]
    public async Task WithoutDriver_CaptureLevelIsNotAvailable()
    {
        var report = await InspectAsync(driverInstalled: false);

        var state = report.StateOf(CapabilityLevel.Capture);

        Assert.NotEqual(CapabilityState.Available, state);
        Assert.DoesNotContain(
            report.OfLevel(CapabilityLevel.Capture),
            c => c.State == CapabilityState.Available);
    }

    /// <summary>
    /// Уровень 1 без учётных данных не объявляется доступным.
    /// </summary>
    /// <remarks>
    /// Это второй из двух пойманных случаев вранья: уровень 1 числился доступным
    /// раньше, чем у него появлялось то, без чего он не работает.
    /// </remarks>
    [Fact]
    public async Task WithoutCredentials_SnmpLevelIsNotAvailable()
    {
        var report = await InspectAsync();

        Assert.NotEqual(CapabilityState.Available, report.StateOf(CapabilityLevel.Snmp));
    }

    private static Task<CapabilityReport> InspectAsync(bool driverInstalled = false)
    {
        var inspector = new CapabilityInspector(
            new FakeRegistry(new FakeProbe(() => 1.0)),
            new FakeSystem(driverInstalled),
            new EmptyAgentStore(),
            new NullEnvironment());

        return inspector.InspectAsync();
    }
}
