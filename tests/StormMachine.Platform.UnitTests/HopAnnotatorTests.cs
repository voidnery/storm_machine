using System.Net;
using StormMachine.Application.Abstractions;
using StormMachine.Platform.Geo;

namespace StormMachine.Platform.UnitTests;

/// <summary>
/// Проверки обогащения узлов маршрута.
/// </summary>
/// <remarks>
/// Обратный DNS здесь намеренно не проверяется: он ходит в сеть, и тест на нём был бы
/// проверкой чужого резолвера, а не нашего кода. Проверяется то, что от нас зависит:
/// распознавание частных адресов, честная деградация без базы и подпись для таблицы.
/// </remarks>
public sealed class HopAnnotatorTests
{
    private sealed class FakeAsnDatabase(params (string Prefix, AsnRecord Record)[] entries) : IAsnDatabase
    {
        public bool IsAvailable => entries.Length > 0;

        public string Location => "тест";

        public string? Description => IsAvailable ? "тестовая база" : null;

        public AsnRecord? Lookup(IPAddress address)
        {
            var text = address.ToString();

            foreach (var (prefix, record) in entries)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return record;
                }
            }

            return null;
        }
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    public void PrivateRanges_AreRecognised(string address) =>
        Assert.True(HopAnnotator.IsPrivate(IPAddress.Parse(address)), $"{address} должен считаться частным.");

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.254")]
    [InlineData("100.128.0.1")]
    [InlineData("192.169.0.1")]
    public void PublicRanges_AreNotMistakenForPrivate(string address) =>
        Assert.False(HopAnnotator.IsPrivate(IPAddress.Parse(address)), $"{address} не является частным.");

    [Fact]
    public async Task PrivateAddress_IsNotLookedUp()
    {
        var annotator = new HopAnnotator(new FakeAsnDatabase(("10.", new AsnRecord(1, "Не должно попасть", null))));

        var result = await annotator.AnnotateAsync(["10.0.0.1"]);

        var annotation = result["10.0.0.1"];
        Assert.True(annotation.IsPrivate);
        Assert.Null(annotation.AsNumber);
        Assert.Equal(HopAnnotation.PrivateLabel, annotation.Describe());
    }

    [Fact]
    public async Task WithoutDatabase_DegradesQuietly()
    {
        var annotator = new HopAnnotator(new FakeAsnDatabase());

        var result = await annotator.AnnotateAsync(["203.0.113.1"]);

        Assert.False(annotator.HasAsnData);
        Assert.Null(annotator.Attribution);
        Assert.Null(result["203.0.113.1"].AsNumber);
    }

    [Fact]
    public async Task WithDatabase_ReportsAutonomousSystemAndAttribution()
    {
        var annotator = new HopAnnotator(
            new FakeAsnDatabase(("203.0.113.", new AsnRecord(64500, "Пример-Телеком", "Германия"))));

        var result = await annotator.AnnotateAsync(["203.0.113.1"]);
        var annotation = result["203.0.113.1"];

        Assert.True(annotator.HasAsnData);
        Assert.NotNull(annotator.Attribution);
        Assert.Equal(64500, annotation.AsNumber);
        Assert.Equal("Пример-Телеком", annotation.AsOrganization);
        Assert.Contains("AS64500", annotation.Describe(), StringComparison.Ordinal);
        Assert.Contains("Германия", annotation.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedAddresses_AreAnnotatedOnce()
    {
        var annotator = new HopAnnotator(new FakeAsnDatabase(("203.0.113.", new AsnRecord(64500, "Пример", null))));

        var result = await annotator.AnnotateAsync(["203.0.113.1", "203.0.113.1", "203.0.113.1"]);

        Assert.Single(result);
    }

    [Fact]
    public async Task MalformedAddress_DoesNotThrow()
    {
        var annotator = new HopAnnotator(new FakeAsnDatabase());

        var result = await annotator.AnnotateAsync(["не адрес", "   "]);

        Assert.Single(result);
        Assert.False(result["не адрес"].HasAnything);
    }

    [Fact]
    public void Describe_IsEmptyWhenNothingKnown() =>
        Assert.Empty(new HopAnnotation { Address = "203.0.113.1" }.Describe());
}
