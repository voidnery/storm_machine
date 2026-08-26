namespace StormMachine.ArchTests;

/// <summary>
/// Проверка самой проверки.
/// </summary>
/// <remarks>
/// Правило «не использовать значения задержки из системных API» игнорирует комментарии.
/// Если бы оно игнорировало слишком много, оно перестало бы что-либо ловить и при этом
/// продолжало гореть зелёным — худший вид негодного теста. Здесь фиксируется,
/// что настоящее обращение к API оно всё ещё видит.
/// </remarks>
public sealed class CommentStrippingTests
{
    [Fact]
    public void RealUsage_IsDetected()
    {
        const string Code = "var rtt = reply.RoundtripTime;";

        Assert.Contains("RoundtripTime", RepositoryLayout.StripComments(Code), StringComparison.Ordinal);
    }

    [Fact]
    public void LineComment_IsIgnored()
    {
        const string Code = "// не используем PingReply.RoundtripTime — целые миллисекунды\nvar x = 1;";

        Assert.DoesNotContain("RoundtripTime", RepositoryLayout.StripComments(Code), StringComparison.Ordinal);
    }

    [Fact]
    public void XmlDocComment_IsIgnored()
    {
        const string Code = "/// <summary>Заменяет PingReply.RoundtripTime.</summary>\npublic int Value;";

        Assert.DoesNotContain("RoundtripTime", RepositoryLayout.StripComments(Code), StringComparison.Ordinal);
    }

    [Fact]
    public void BlockComment_IsIgnored()
    {
        const string Code = "/* RoundtripTime\n   всё ещё RoundtripTime */\nvar x = 1;";

        Assert.DoesNotContain("RoundtripTime", RepositoryLayout.StripComments(Code), StringComparison.Ordinal);
    }

    [Fact]
    public void CodeAfterBlockComment_Survives()
    {
        const string Code = "/* пояснение */ var rtt = reply.RoundtripTime;";

        Assert.Contains("RoundtripTime", RepositoryLayout.StripComments(Code), StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBeforeLineComment_Survives()
    {
        const string Code = "var rtt = reply.RoundtripTime; // так делать нельзя";

        Assert.Contains("RoundtripTime", RepositoryLayout.StripComments(Code), StringComparison.Ordinal);
    }
}
