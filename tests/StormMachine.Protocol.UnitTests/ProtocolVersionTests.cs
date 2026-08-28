using StormMachine.Protocol;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Проверки правила совместимости.
/// </summary>
/// <remarks>
/// Клиент и агент обновляются порознь: агент живёт на чужой машине, куда никто
/// не пойдёт ради обновления. Значит несовместимость — обычная ситуация, а не
/// исключительная, и её сообщение должно называть, какую именно сторону обновлять.
/// Обновление агента на площадке — это поездка, и ошибиться в направлении дорого.
/// </remarks>
public sealed class ProtocolVersionTests
{
    [Fact]
    public void SameMajor_IsCompatible() => Assert.True(ProtocolVersion.IsCompatibleWith(ProtocolVersion.Major));

    [Fact]
    public void DifferentMajor_IsNot()
    {
        Assert.False(ProtocolVersion.IsCompatibleWith(ProtocolVersion.Major + 1));
        Assert.False(ProtocolVersion.IsCompatibleWith(ProtocolVersion.Major - 1));
    }

    [Fact]
    public void MinorDoesNotMatter()
    {
        // Младшая версия меняется при добавлении полей, которых старая сторона
        // просто не увидит. Ломать связь из-за них незачем.
        Assert.Empty(ProtocolVersion.Explain(ProtocolVersion.Major, 99, "storm/будущее"));
    }

    [Fact]
    public void OlderPeer_IsToldToUpdateTheAgent()
    {
        var text = ProtocolVersion.Explain(ProtocolVersion.Major - 1, 0, "storm/старый");

        Assert.Contains("обновить агента", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NewerPeer_IsToldToUpdateTheClient()
    {
        var text = ProtocolVersion.Explain(ProtocolVersion.Major + 1, 0, "storm/новый");

        Assert.Contains("обновить клиент", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Explanation_NamesBothVersionsAndTheProduct()
    {
        var text = ProtocolVersion.Explain(ProtocolVersion.Major + 1, 7, "storm/новый");

        Assert.Contains($"{ProtocolVersion.Major}.{ProtocolVersion.Minor}", text, StringComparison.Ordinal);
        Assert.Contains($"{ProtocolVersion.Major + 1}.7", text, StringComparison.Ordinal);
        Assert.Contains("storm/новый", text, StringComparison.Ordinal);
    }
}
