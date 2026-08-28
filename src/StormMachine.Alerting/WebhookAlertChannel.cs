using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Application;
using StormMachine.Application.Abstractions;

namespace StormMachine.Alerting;

/// <summary>Тело запроса webhook.</summary>
/// <remarks>
/// Плоское и предсказуемое: приёмник — чаще всего чужой скрипт или интеграция,
/// и вложенные структуры там разбирают неохотно. Имена полей латиницей по той же
/// причине: их читает программа, а не человек.
/// </remarks>
public sealed record WebhookPayload
{
    public required string Monitor { get; init; }

    public required string MonitorId { get; init; }

    public required string Target { get; init; }

    /// <summary>raised, cleared, repeated.</summary>
    public required string Action { get; init; }

    /// <summary>pass, warn, fail, unknown.</summary>
    public required string Level { get; init; }

    public required string Reason { get; init; }

    public string? Summary { get; init; }

    public string? Metric { get; init; }

    public double? Value { get; init; }

    public double? Threshold { get; init; }

    /// <summary>Время в ISO 8601 с зоной — единственный формат, который не толкуют вдвое.</summary>
    public required string At { get; init; }

    public string? RunId { get; init; }
}

/// <summary>
/// Оповещение через HTTP POST.
/// </summary>
/// <remarks>
/// Канал, через который продукт дотягивается до всего остального: Telegram, Slack,
/// корпоративный шлюз, собственный скрипт. Поэтому он и сделан раньше остальных —
/// один webhook закрывает больше случаев, чем три специализированных канала.
/// </remarks>
public sealed class WebhookAlertChannel(ISettingsStore settings, HttpClient http) : IAlertChannel
{
    /// <summary>
    /// Контекст с настройками.
    /// </summary>
    /// <remarks>
    /// Настройки задаются экземпляру контекста, а не отдельным объектом при вызове:
    /// перегрузка <c>Serialize</c> с <c>JsonSerializerOptions</c> помечена
    /// несовместимой с обрезкой, и публикация с ней не собирается. Ровно та же
    /// оговорка, что в хранилище и в протоколе агента.
    /// </remarks>
    private static readonly WebhookJsonContext Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    private string? _url;
    private string? _authorization;

    public string Name => "webhook";

    public string Title => "HTTP POST на заданный адрес";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_url);

    public string? MissingConfiguration => IsConfigured
        ? null
        : $"не задан адрес — «storm alerts set {AlertSettings.WebhookUrl} <адрес>»";

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _url = await _settings.GetAsync(AlertSettings.WebhookUrl, cancellationToken).ConfigureAwait(false);
        _authorization = await _settings
            .GetAsync(AlertSettings.WebhookAuthorization, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (_url is null)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(_url))
        {
            throw new InvalidOperationException("Адрес webhook не задан.");
        }

        var payload = Build(notification);
        var json = JsonSerializer.Serialize(payload, Json.WebhookPayload);

        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        }

        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("StormMachine", ProductInfo.Version));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Тело ответа попадает в сообщение об ошибке: приёмники объясняют отказ
            // именно там, а код состояния сам по себе не говорит, что чинить.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tail = body.Length > 200 ? body[..200] + "…" : body;

            throw new HttpRequestException(
                $"приёмник ответил {(int)response.StatusCode} {response.ReasonPhrase}"
                + (string.IsNullOrWhiteSpace(tail) ? string.Empty : $": {tail}"));
        }
    }

    internal static WebhookPayload Build(AlertNotification notification) => new()
    {
        Monitor = notification.Monitor.Name,
        MonitorId = notification.Monitor.Id.ToString(),
        Target = notification.Monitor.Target.Value,
        Action = notification.Event.Action.ToString().ToLowerInvariant(),
        Level = notification.Event.Level.ToString().ToLowerInvariant(),
        Reason = notification.Event.Reason,
        Summary = notification.Check.Summary,
        Metric = notification.Check.Metric,
        Value = notification.Check.Value,
        Threshold = notification.Check.Threshold,
        At = notification.Event.AtUtc.ToString("O", CultureInfo.InvariantCulture),
        RunId = notification.Check.RunId?.ToString(),
    };
}

/// <summary>
/// Контекст сериализации тела webhook.
/// </summary>
/// <remarks>
/// Сгенерирован исходниками: клиенты публикуются с обрезкой, и рефлексивная
/// сериализация при ней не собирается вовсе. Настройки задаются и атрибуту,
/// и экземпляру — иначе <c>DefaultIgnoreCondition</c> из атрибута теряется,
/// и в теле появляются поля со значением null. Это уже было с протоколом агента.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebhookPayload))]
internal sealed partial class WebhookJsonContext : JsonSerializerContext
{
}
