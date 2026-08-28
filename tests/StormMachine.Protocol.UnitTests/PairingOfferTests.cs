using StormMachine.Protocol;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Проверки предложения сопряжения.
/// </summary>
/// <remarks>
/// Эти правила существуют потому, что до них продукт врал. Агент сообщал «код действует
/// ограниченное время», а код жил до перезапуска и не гас даже после того, как им
/// воспользовались: услышавший его сопрягался вторым, и оператор об этом не узнавал —
/// у него всё прошло успешно.
/// </remarks>
public sealed class PairingOfferTests
{
    [Fact]
    public void FreshOffer_IsUsable()
    {
        var offer = PairingOffer.Issue();

        Assert.False(offer.IsUsed);
        Assert.False(offer.IsExpired);
        Assert.Equal(offer.Code, offer.CodeIfValid);
        Assert.Null(offer.ExplainIfSpent());
    }

    [Fact]
    public void UsedOffer_StopsWorking()
    {
        var offer = PairingOffer.Issue();

        Assert.True(offer.Consume());
        Assert.True(offer.IsUsed);
        Assert.Null(offer.CodeIfValid);
        Assert.Contains("уже использован", offer.ExplainIfSpent() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsumeTwice_IsRefusedTheSecondTime()
    {
        // Признак того, что код погашен именно этим сопряжением, а не вторым:
        // без него два одновременных звонка оба сочли бы себя первыми.
        var offer = PairingOffer.Issue();

        Assert.True(offer.Consume());
        Assert.False(offer.Consume());
    }

    [Fact]
    public void ExpiredOffer_StopsWorking()
    {
        var offer = PairingOffer.Issue(TimeSpan.Zero);

        Assert.True(offer.IsExpired);
        Assert.Null(offer.CodeIfValid);
        Assert.Equal(TimeSpan.Zero, offer.Remaining);
        Assert.Contains("Срок кода истёк", offer.ExplainIfSpent() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Remaining_ShrinksButStaysPositiveWhileValid()
    {
        var offer = PairingOffer.Issue(TimeSpan.FromMinutes(10));

        Assert.True(offer.Remaining > TimeSpan.FromMinutes(9));
        Assert.True(offer.Remaining <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void For_NormalizesWhatTheOperatorTyped()
    {
        var offer = PairingOffer.For("acd-efg", TimeSpan.FromMinutes(1));

        Assert.Equal("ACDEFG", offer.Code);
        Assert.Equal("ACD-EFG", offer.ForHumans);
    }

    [Fact]
    public void DefaultLifetime_IsTheOneDeclaredByTheProtocol() =>
        Assert.Equal(PairingCode.Lifetime, PairingOffer.Issue().Lifetime);

    [Fact]
    public void EveryOffer_HasItsOwnCode()
    {
        var codes = Enumerable.Range(0, 50)
            .Select(_ => PairingOffer.Issue().Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(codes.Count > 45, $"Из 50 предложений различных кодов всего {codes.Count}.");
    }
}
