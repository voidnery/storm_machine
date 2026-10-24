using StormMachine.App.Services;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Какую версию продукт назовёт новой.
/// </summary>
/// <remarks>
/// Ошибка здесь видна не сразу и всегда в одну из двух сторон: либо человеку на стабильной
/// версии предлагают альфу, либо альфа-10 считается старше альфы-9 только потому, что
/// строки так сравнились. Обе проверяются здесь, а не на живом GitHub.
/// </remarks>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.2.2-alpha.3", "0.2.2", -1)]
    [InlineData("0.2.2-alpha.9", "0.2.2-alpha.10", -1)]
    [InlineData("0.2.2-alpha.3", "0.2.2-beta.1", -1)]
    [InlineData("0.2.2-alpha", "0.2.2-alpha.1", -1)]
    [InlineData("0.2.10", "0.2.9", 1)]
    [InlineData("v0.3.0-beta.1", "0.3.0-beta.1+abc", 0)]
    public void Versions_FollowSemVerOrder(string left, string right, int expected)
    {
        Assert.True(ReleaseVersion.TryParse(left, out var a));
        Assert.True(ReleaseVersion.TryParse(right, out var b));

        Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("beta")]
    [InlineData("0.2")]
    [InlineData("0.2.x")]
    [InlineData("0.2.2-")]
    [InlineData("0.2.2-alpha..1")]
    public void Garbage_IsNotAVersion(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Prerelease_SeesNewerPrereleases()
    {
        var releases = new[] { ("v0.2.0-alpha.2", true), ("v0.3.0-beta.2", true), ("v0.3.0-beta.1", true) };

        Assert.Equal("0.3.0-beta.2", UpdateService.Newer(releases, "0.3.0-beta.1"));
    }

    [Fact]
    public void Stable_IsNotOfferedPrereleases()
    {
        var releases = new[] { ("v1.1.0-beta.1", true), ("v1.0.1", false) };

        Assert.Equal("1.0.1", UpdateService.Newer(releases, "1.0.0"));
    }

    [Fact]
    public void SameOrOlder_IsNotNews()
    {
        var releases = new[] { ("v0.3.0-beta.1", true), ("v0.2.0-alpha.2", true), ("nightly", false) };

        Assert.Null(UpdateService.Newer(releases, "0.3.0-beta.1"));
    }

    [Fact]
    public void ReleasePage_IsBuiltFromParsedVersion()
    {
        var service = new UpdateService();

        Assert.Equal(UpdateService.ReleasesUrl + "/releases", service.ReleasePage);

        service.AvailableVersion = "0.3.0-beta.2";

        Assert.Equal(UpdateService.ReleasesUrl + "/releases/tag/v0.3.0-beta.2", service.ReleasePage);
    }
}
