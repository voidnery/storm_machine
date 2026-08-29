using StormMachine.Cli.Rendering;
using StormMachine.Domain.Measurements;

namespace StormMachine.Cli.UnitTests;

/// <summary>
/// Единый словарь понятий для показа.
/// </summary>
/// <remarks>
/// Заведён в И-3 ровно потому, что расхождение уже началось: журнал печатал тип
/// адаптера машинным именем перечисления. Проверок у словаря при этом не было
/// до И-19 — то есть защита от повторения проверялась только вниманием.
/// </remarks>
public sealed class DescribeTests
{
    /// <summary>
    /// Ни одно значение перечисления не печатается машинным именем.
    /// </summary>
    /// <remarks>
    /// Проверка идёт по всем значениям сразу, а не по перечисленным вручную:
    /// новое значение перечисления должно ломать этот тест, а не тихо доезжать
    /// до оператора в виде «TtlExpired».
    /// </remarks>
    [Fact]
    public void EveryAdapterKind_HasARussianName()
    {
        foreach (var kind in Enum.GetValues<AdapterKind>())
        {
            var text = Describe.AdapterKind(kind);

            Assert.False(string.IsNullOrWhiteSpace(text), $"{kind} не назван");
            Assert.NotEqual(kind.ToString(), text);
        }
    }

    [Fact]
    public void EverySampleStatus_HasARussianName()
    {
        foreach (var status in Enum.GetValues<SampleStatus>())
        {
            var text = Describe.SampleStatus(status);

            Assert.False(string.IsNullOrWhiteSpace(text), $"{status} не назван");
            Assert.NotEqual(status.ToString(), text);
        }
    }

    [Theory]
    [InlineData(MeasurementUnit.Milliseconds, " мс")]
    [InlineData(MeasurementUnit.MegabitsPerSecond, " Мбит/с")]
    [InlineData(MeasurementUnit.Percent, " %")]
    [InlineData(MeasurementUnit.Bytes, " байт")]
    public void UnitSuffix_NamesTheUnit(MeasurementUnit unit, string expected) =>
        Assert.Equal(expected, Describe.UnitSuffix(unit));

    /// <summary>Единица берётся из самого факта: проба знает, что она измерила.</summary>
    [Fact]
    public void UnitSuffix_ComesFromTheFactItself()
    {
        var fact = ProbeFact.Number("tls", "Срок сертификата", 30, MeasurementUnit.Milliseconds);

        Assert.Equal(" мс", Describe.UnitSuffix(fact));
    }

    /// <summary>У факта без единицы суффикса нет — приписывать ему миллисекунды нельзя.</summary>
    [Fact]
    public void UnitSuffix_IsEmptyForTextFacts() =>
        Assert.Equal(string.Empty, Describe.UnitSuffix(ProbeFact.Text("dns", "Запись", "A")));

    [Theory]
    [InlineData("dns", "DNS")]
    [InlineData("connect", "TCP")]
    [InlineData("tls", "TLS")]
    [InlineData("ttfb", "первый байт")]
    [InlineData("download", "скачивание")]
    public void PhaseName_TranslatesKnownPhases(string label, string expected) =>
        Assert.Equal(expected, Describe.PhaseName(label));

    /// <summary>
    /// Незнакомая фаза показывается как есть.
    /// </summary>
    /// <remarks>
    /// Подставить «—» вместо неизвестного значило бы скрыть от оператора то,
    /// что проба на самом деле измерила.
    /// </remarks>
    [Fact]
    public void PhaseName_KeepsUnknownLabelAsIs() =>
        Assert.Equal("handshake", Describe.PhaseName("handshake"));

    [Fact]
    public void PhaseName_ShowsADashForNothing() =>
        Assert.Equal("—", Describe.PhaseName(null));

    [Fact]
    public void Facts_AreGroupedByCategory()
    {
        var text = ConsoleCapture.Of(() => Describe.WriteFacts(
        [
            ProbeFact.Text("dns", "Запись", "A"),
            ProbeFact.Text("tls", "Протокол", "TLS 1.3"),
            ProbeFact.Text("dns", "Сервер", "192.168.1.1"),
        ]));

        Assert.Contains("[dns]", text, StringComparison.Ordinal);
        Assert.Contains("[tls]", text, StringComparison.Ordinal);

        // Оба факта категории dns идут под одним заголовком, а не двумя.
        Assert.Equal(1, text.Split("[dns]").Length - 1);
    }

    /// <summary>Предупреждающий факт помечен — иначе он потеряется среди обычных.</summary>
    [Fact]
    public void Facts_MarkWarnings()
    {
        var text = ConsoleCapture.Of(() => Describe.WriteFacts(
        [
            ProbeFact.Text("tls", "Протокол", "TLS 1.3"),
            ProbeFact.Warning("tls", "Срок", "истекает через 3 дня"),
        ]));

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var warning = lines.First(l => l.Contains("Срок", StringComparison.Ordinal));
        var plain = lines.First(l => l.Contains("Протокол", StringComparison.Ordinal));

        Assert.StartsWith("  !", warning, StringComparison.Ordinal);
        Assert.StartsWith("   ", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Facts_PrintNothingWhenThereAreNone() =>
        Assert.Equal(string.Empty, ConsoleCapture.Of(() => Describe.WriteFacts([])));

    /// <summary>Число факта показывается с единицей: «7.497» само по себе ничего не значит.</summary>
    [Fact]
    public void Facts_CarryTheirUnit()
    {
        var text = ConsoleCapture.Of(() => Describe.WriteFacts(
            [ProbeFact.Number("http", "Тело", 7.497, MeasurementUnit.Bytes)]));

        Assert.Contains("7.497 байт", text, StringComparison.Ordinal);
    }
}
