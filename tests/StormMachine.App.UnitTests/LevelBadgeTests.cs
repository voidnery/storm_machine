using StormMachine.App.ViewModels;
using StormMachine.Domain.Capabilities;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Надпись об уровнях в шапке говорит о состоянии машины.
/// </summary>
/// <remarks>
/// Была константой «Уровень 0 — ядро» и не менялась ни от запуска с правами,
/// ни от заведённых учётных данных SNMP. Нашёл это оператор: надпись, которая
/// не может стать другой, ничего не сообщает — а выглядит как сообщение
/// о состоянии, и в этом её вред.
/// </remarks>
public sealed class LevelBadgeTests
{
    [Theory(DisplayName = "Надпись складывается из того, что работает")]
    [InlineData(false, false, "Уровень 0 — ядро")]
    [InlineData(true, false, "Уровни 0 и 1 — ядро и SNMP")]
    [InlineData(false, true, "Уровни 0 и 2 — ядро и захват")]
    [InlineData(true, true, "Все три уровня")]
    public void Badge_FollowsTheMachine(bool snmp, bool capture, string expected)
    {
        // Уровни независимы: захват требует прав, SNMP — учётных данных. «Уровни 0 и 2»
        // — такое же законное состояние машины, как и «все три», и показывать вместо
        // него «верхний достигнутый» значило бы соврать про SNMP.
        Assert.Equal(expected, LevelBadge.Describe(snmp, capture));
    }

    [Fact(DisplayName = "Подсказка называет, чего не хватает остальным уровням")]
    public void Hint_NamesWhatIsMissing()
    {
        // Недоступное не прячется, а объясняется: человек, прочитавший «уровень 0»,
        // должен узнать, что сделать для остальных, не уходя с этого экрана.
        Assert.Contains("учётные данные SNMP", LevelBadge.Hint(snmp: false, capture: true), StringComparison.Ordinal);
        Assert.Contains("права администратора", LevelBadge.Hint(snmp: true, capture: false), StringComparison.Ordinal);
        Assert.Contains("все три", LevelBadge.Hint(snmp: true, capture: true), StringComparison.OrdinalIgnoreCase);

        // Сама шкала остаётся в подсказке всегда: раздел «Разработка» временный,
        // и после его удаления объяснять уровни было бы больше негде.
        Assert.Contains("Уровень 1 добавляет опрос", LevelBadge.Hint(snmp: false, capture: false), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Уровень считается доступным только когда доступен целиком")]
    public void Level_IsAvailableOnlyWhenWhole()
    {
        // Правило домена, на которое опирается шапка: уровень, где работает половина,
        // обещал бы SNMP там, где учётных данных нет.
        var report = new CapabilityReport
        {
            IsElevated = false,
            Capabilities =
            [
                Capability("snmp", CapabilityLevel.Snmp, CapabilityState.Available),
                Capability("snmp.ports", CapabilityLevel.Snmp, CapabilityState.NeedsCredentials),
            ],
        };

        Assert.NotEqual(CapabilityState.Available, report.StateOf(CapabilityLevel.Snmp));
    }

    private static Capability Capability(string id, CapabilityLevel level, CapabilityState state) => new()
    {
        Id = id,
        Title = id,
        About = "для проверки",
        Level = level,
        State = state,
    };
}
