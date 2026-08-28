using StormMachine.Protocol;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Проверки кода сопряжения и личности стороны.
/// </summary>
/// <remarks>
/// Главное здесь — симметрия доказательства. Звонить может любая сторона, поэтому
/// «свой отпечаток, потом чужой» дало бы двум сторонам разные значения при том, что
/// обе правы, и сопряжение не сошлось бы никогда. Порядок задаётся сортировкой,
/// и тест это закрепляет.
/// </remarks>
public sealed class PairingTests
{
    private const string ThumbprintA = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555FFFF6666AAAA7777BBBB8888";
    private const string ThumbprintB = "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE6666FFFF7777AAAA8888BBBB";

    [Fact]
    public void Generate_UsesAlphabetWithoutLookalikes()
    {
        // Ноль и «O», единица и «I» путаются на слух и на глаз, а код читают вслух.
        for (var i = 0; i < 200; i++)
        {
            var code = PairingCode.Generate();

            Assert.Equal(PairingCode.Length, code.Length);
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
            Assert.DoesNotContain('L', code);
        }
    }

    [Fact]
    public void Generate_DoesNotRepeatItself()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => PairingCode.Generate()).ToHashSet(StringComparer.Ordinal);

        Assert.True(codes.Count > 190, $"Из 200 кодов различных всего {codes.Count} — источник случайности подозрителен.");
    }

    [Theory]
    [InlineData("abc-def", "ABCDEF")]
    [InlineData("ABC DEF", "ABCDEF")]
    [InlineData(" a b c ", "ABC")]
    public void Normalize_IgnoresHowItWasTyped(string typed, string expected) =>
        Assert.Equal(expected, PairingCode.Normalize(typed));

    [Fact]
    public void ForHumans_SplitsInHalf() => Assert.Equal("ACD-EFG", PairingCode.ForHumans("ACDEFG"));

    [Fact]
    public void Proof_IsTheSameFromBothSides()
    {
        // Каждая сторона считает «свой отпечаток и чужой» — и получает одно и то же.
        var fromA = PairingCode.Prove("ACDEFG", ThumbprintA, ThumbprintB);
        var fromB = PairingCode.Prove("ACDEFG", ThumbprintB, ThumbprintA);

        Assert.Equal(fromA, fromB);
    }

    [Fact]
    public void Proof_IgnoresHowTheCodeWasTyped() =>
        Assert.Equal(
            PairingCode.Prove("ACDEFG", ThumbprintA, ThumbprintB),
            PairingCode.Prove("acd-efg", ThumbprintA, ThumbprintB));

    [Fact]
    public void Proof_IsBoundToTheCertificatePair()
    {
        // Перехваченное доказательство не годится для сопряжения с другим сертификатом —
        // значит его нечего и перехватывать.
        var other = "9999AAAA8888BBBB7777CCCC6666DDDD5555EEEE4444FFFF3333AAAA2222BBBB";

        Assert.NotEqual(
            PairingCode.Prove("ACDEFG", ThumbprintA, ThumbprintB),
            PairingCode.Prove("ACDEFG", ThumbprintA, other));
    }

    [Fact]
    public void Verify_AcceptsTheRightCode() =>
        Assert.True(PairingCode.Verify(
            PairingCode.Prove("ACDEFG", ThumbprintB, ThumbprintA),
            "ACDEFG",
            ThumbprintA,
            ThumbprintB));

    [Fact]
    public void Verify_RejectsTheWrongCode() =>
        Assert.False(PairingCode.Verify(
            PairingCode.Prove("ACDEFG", ThumbprintB, ThumbprintA),
            "QQQQQQ",
            ThumbprintA,
            ThumbprintB));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Verify_RejectsMissingProof(string? proof) =>
        Assert.False(PairingCode.Verify(proof, "ACDEFG", ThumbprintA, ThumbprintB));

    [Fact]
    public void Identity_HasPrivateKeyAndStableThumbprint()
    {
        var identity = PeerIdentity.Create("storm-agent");

        Assert.True(identity.Certificate.HasPrivateKey);
        Assert.Equal(64, identity.Thumbprint.Length);
        Assert.Equal(identity.Thumbprint, PeerIdentity.ThumbprintOf(identity.Certificate.RawData));
    }

    [Fact]
    public void Identity_TwoCertificatesWithTheSameNameDiffer()
    {
        // Субъект подделать нечего не стоит. Отпечаток — нет, и доверие строится на нём.
        var first = PeerIdentity.Create("storm-agent");
        var second = PeerIdentity.Create("storm-agent");

        Assert.Equal(first.Certificate.Subject, second.Certificate.Subject);
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void Identity_SurvivesRestart()
    {
        // Новая личность означала бы потерю всех сопряжений — а сопряжение агента
        // на чужой площадке стоит поездки.
        var path = Path.Combine(Path.GetTempPath(), $"storm-identity-{Guid.NewGuid():N}.pfx");

        try
        {
            var first = PeerIdentity.LoadOrCreate(path, "storm-agent");
            var second = PeerIdentity.LoadOrCreate(path, "storm-agent");

            Assert.Equal(first.Thumbprint, second.Thumbprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThumbprintForHumans_IsGroupedForReadingAloud()
    {
        var grouped = PeerIdentity.Group("AAAABBBBCCCC");

        Assert.Equal("AAAA BBBB CCCC", grouped);
    }
}
