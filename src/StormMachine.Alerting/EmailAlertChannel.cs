using System.Globalization;
using System.Net;
using System.Net.Mail;
using StormMachine.Application.Abstractions;

namespace StormMachine.Alerting;

/// <summary>
/// Оповещение письмом.
/// </summary>
/// <remarks>
/// Используется <see cref="SmtpClient"/> из базовой библиотеки: обычный SMTP
/// с STARTTLS или неявным TLS и с обычной аутентификацией по паролю. Этого хватает
/// для внутреннего релея и для ящика с паролем приложения.
/// <para>
/// <b>Чего он не умеет, сказано здесь, а не выяснится при отладке:</b> современного
/// входа через OAuth 2.0 в нём нет. Ящики Gmail и Microsoft 365 с включённой
/// двухфакторной защитой примут его только по паролю приложения, а при запрещённой
/// «базовой аутентификации» — не примут вовсе. Для таких случаев есть webhook:
/// он доводит до любого шлюза, который уже умеет всё нужное.
/// </para>
/// <para>
/// Пароль лежит зашифрованным (<see cref="ISecretProtector"/>) и в открытом виде
/// в базе не появляется.
/// </para>
/// </remarks>
public sealed class EmailAlertChannel(ISettingsStore settings) : IAlertChannel
{
    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private string? _host;
    private int _port = 587;
    private bool _tls = true;
    private string? _user;
    private string? _password;
    private string? _from;
    private string[] _to = [];

    public string Name => "почта";

    public string Title => "Письмо через SMTP";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_host)
        && !string.IsNullOrWhiteSpace(_from)
        && _to.Length > 0;

    public string? MissingConfiguration
    {
        get
        {
            if (IsConfigured)
            {
                return null;
            }

            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(_host))
            {
                missing.Add(AlertSettings.SmtpHost);
            }

            if (string.IsNullOrWhiteSpace(_from))
            {
                missing.Add(AlertSettings.SmtpFrom);
            }

            if (_to.Length == 0)
            {
                missing.Add(AlertSettings.SmtpTo);
            }

            return "не заданы: " + string.Join(", ", missing);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _host = await _settings.GetAsync(AlertSettings.SmtpHost, cancellationToken).ConfigureAwait(false);
        _from = await _settings.GetAsync(AlertSettings.SmtpFrom, cancellationToken).ConfigureAwait(false);
        _user = await _settings.GetAsync(AlertSettings.SmtpUser, cancellationToken).ConfigureAwait(false);
        _password = await _settings.GetAsync(AlertSettings.SmtpPassword, cancellationToken).ConfigureAwait(false);

        var port = await _settings.GetAsync(AlertSettings.SmtpPort, cancellationToken).ConfigureAwait(false);

        _port = int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed is > 0 and <= 65535
            ? parsed
            : 587;

        var tls = await _settings.GetAsync(AlertSettings.SmtpTls, cancellationToken).ConfigureAwait(false);

        // По умолчанию шифруем. Выключение — осознанное действие оператора,
        // а не то, что случается само из-за незаполненной настройки.
        _tls = tls is null || IsYes(tls);

        var to = await _settings.GetAsync(AlertSettings.SmtpTo, cancellationToken).ConfigureAwait(false);

        _to = to is null
            ? []
            : [.. to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public async Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_host is null)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!IsConfigured)
        {
            throw new InvalidOperationException(MissingConfiguration);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_from!),
            Subject = notification.Subject,
            Body = notification.Body,
            IsBodyHtml = false,
        };

        foreach (var address in _to)
        {
            message.To.Add(address);
        }

        using var client = new SmtpClient(_host, _port)
        {
            EnableSsl = _tls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds,
        };

        if (!string.IsNullOrWhiteSpace(_user))
        {
            client.Credentials = new NetworkCredential(_user, _password ?? string.Empty);
        }
        else
        {
            // Без учётных данных — значит без них, а не «попробовать текущего
            // пользователя Windows»: молчаливая попытка войти чужим именем
            // выглядит в журнале почтового сервера подозрительнее отказа.
            client.UseDefaultCredentials = false;
        }

        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsYes(string value) =>
        value.Trim() is "да" or "yes" or "true" or "1" or "on";
}
