using StormMachine.Application.Abstractions;

namespace StormMachine.Alerting.UnitTests;

/// <summary>
/// Настройка почтового канала.
/// </summary>
/// <remarks>
/// Канал, молча не отправивший письмо, хуже отсутствующего: на него рассчитывают.
/// Поэтому проверяется не отправка — для неё нужен сервер, — а то, что канал честно
/// говорит о своей ненастроенности и не превращает пустую настройку в опасное
/// умолчание.
/// </remarks>
public sealed class EmailChannelTests
{
    [Fact]
    public async Task WithoutSettings_ChannelSaysExactlyWhatIsMissing()
    {
        var channel = new EmailAlertChannel(new FakeSettings());
        await channel.RefreshAsync();

        Assert.False(channel.IsConfigured);

        var missing = channel.MissingConfiguration;

        Assert.NotNull(missing);
        Assert.Contains(AlertSettings.SmtpHost, missing, StringComparison.Ordinal);
        Assert.Contains(AlertSettings.SmtpFrom, missing, StringComparison.Ordinal);
        Assert.Contains(AlertSettings.SmtpTo, missing, StringComparison.Ordinal);
    }

    /// <summary>Настроенный канал не жалуется — иначе жалоба обесценится.</summary>
    [Fact]
    public async Task WithSettings_ChannelIsQuiet()
    {
        var channel = new EmailAlertChannel(Configured());
        await channel.RefreshAsync();

        Assert.True(channel.IsConfigured);
        Assert.Null(channel.MissingConfiguration);
    }

    /// <summary>Половина настроек — это не настройка: канал называет недостающее.</summary>
    [Fact]
    public async Task WithHalfTheSettings_ChannelNamesOnlyWhatIsStillMissing()
    {
        var settings = new FakeSettings
        {
            [AlertSettings.SmtpHost] = "smtp.example.org",
            [AlertSettings.SmtpFrom] = "storm@example.org",
        };

        var channel = new EmailAlertChannel(settings);
        await channel.RefreshAsync();

        Assert.False(channel.IsConfigured);
        Assert.Contains(AlertSettings.SmtpTo, channel.MissingConfiguration!, StringComparison.Ordinal);
        Assert.DoesNotContain(AlertSettings.SmtpHost, channel.MissingConfiguration!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Отправка без настроек падает, а не делает вид, что письмо ушло.
    /// </summary>
    /// <remarks>
    /// Ошибку доставки канал обязан выбросить, а не проглотить: проглоченная означает,
    /// что оператор считает себя оповещённым, не будучи оповещённым.
    /// </remarks>
    [Fact]
    public async Task SendingWithoutSettings_Throws()
    {
        var channel = new EmailAlertChannel(new FakeSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SendAsync(AlertFixture.Notification()));
    }

    [Fact]
    public async Task NotConfiguredChannel_StillNamesItself()
    {
        var channel = new EmailAlertChannel(new FakeSettings());
        await channel.RefreshAsync();

        // Имя нужно правилу оповещения, и оно не зависит от настроенности:
        // иначе ненастроенный канал нельзя было бы даже упомянуть в правиле.
        Assert.Equal("почта", channel.Name);
        Assert.False(string.IsNullOrWhiteSpace(channel.Title));
    }

    [Fact]
    public async Task WebhookChannel_SaysWhereToSetTheAddress()
    {
        using var http = new HttpClient();
        var channel = new WebhookAlertChannel(new FakeSettings(), http);
        await channel.RefreshAsync();

        Assert.False(channel.IsConfigured);
        Assert.Contains(AlertSettings.WebhookUrl, channel.MissingConfiguration!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookChannel_WithAddressIsConfigured()
    {
        using var http = new HttpClient();
        var settings = new FakeSettings { [AlertSettings.WebhookUrl] = "https://example.org/hook" };

        var channel = new WebhookAlertChannel(settings, http);
        await channel.RefreshAsync();

        Assert.True(channel.IsConfigured);
        Assert.Null(channel.MissingConfiguration);
    }

    private static FakeSettings Configured() => new()
    {
        [AlertSettings.SmtpHost] = "smtp.example.org",
        [AlertSettings.SmtpFrom] = "storm@example.org",
        [AlertSettings.SmtpTo] = "ops@example.org, oncall@example.org",
    };
}
