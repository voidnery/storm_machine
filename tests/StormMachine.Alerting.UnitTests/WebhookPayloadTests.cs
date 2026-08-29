using System.Text.Json;
using StormMachine.Alerting;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Alerting.UnitTests;

/// <summary>
/// Тело оповещения по webhook.
/// </summary>
/// <remarks>
/// Это единственный канал, через который продукт дотягивается до всего остального:
/// Telegram, Slack, корпоративный шлюз, чужой скрипт. Читает его программа, и ошибка
/// в теле не выглядит ошибкой — приёмник просто разберёт не то. До И-19 канал
/// не был покрыт ничем.
/// </remarks>
public sealed class WebhookPayloadTests
{
    private static AlertNotification Notification(
        AlertAction action = AlertAction.Raised,
        VerdictLevel level = VerdictLevel.Fail,
        string? metric = "p95",
        double? value = 120.5,
        double? threshold = 100.0,
        Guid? runId = null)
    {
        var monitorId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var monitor = new Monitor
        {
            Id = monitorId,
            Name = "Шлюз",
            Target = Target.Ip("192.168.1.1"),
            Subject = "ping",
            Schedule = Schedule.Every(TimeSpan.FromMinutes(5)),
        };

        var check = new MonitorCheck
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MonitorId = monitorId,
            StartedUtc = DateTimeOffset.UnixEpoch,
            Level = level,
            Summary = "p95 120.5 мс при пороге 100",
            Metric = metric,
            Value = value,
            Threshold = threshold,
            RunId = runId,
        };

        var alert = new AlertEvent
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            MonitorId = monitorId,
            MonitorName = "Шлюз",
            AtUtc = new DateTimeOffset(2026, 8, 29, 12, 30, 0, TimeSpan.Zero),
            Action = action,
            Level = level,
            Reason = "порог превышен три раза подряд",
        };

        return new AlertNotification(monitor, alert, check);
    }

    /// <summary>Имена состояний уходят латиницей в нижнем регистре: их читает программа.</summary>
    [Theory]
    [InlineData(AlertAction.Raised, "raised")]
    [InlineData(AlertAction.Cleared, "cleared")]
    public void Action_IsMachineReadable(AlertAction action, string expected) =>
        Assert.Equal(expected, WebhookAlertChannel.Build(Notification(action: action)).Action);

    [Theory]
    [InlineData(VerdictLevel.Pass, "pass")]
    [InlineData(VerdictLevel.Warn, "warn")]
    [InlineData(VerdictLevel.Fail, "fail")]
    [InlineData(VerdictLevel.Unknown, "unknown")]
    public void Level_IsMachineReadable(VerdictLevel level, string expected) =>
        Assert.Equal(expected, WebhookAlertChannel.Build(Notification(level: level)).Level);

    /// <summary>
    /// Время идёт в ISO 8601 с зоной.
    /// </summary>
    /// <remarks>
    /// Единственный формат, который не толкуют вдвое. «29.08.2026 12:30» на приёмнике
    /// в другой зоне означает другой момент, и заметить это по журналу невозможно.
    /// </remarks>
    [Fact]
    public void Time_CarriesItsZone()
    {
        var payload = WebhookAlertChannel.Build(Notification());

        Assert.Equal("2026-08-29T12:30:00.0000000+00:00", payload.At);
        Assert.True(DateTimeOffset.TryParse(payload.At, out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void Payload_CarriesTheNumbersThatCausedTheAlert()
    {
        var payload = WebhookAlertChannel.Build(Notification());

        Assert.Equal("p95", payload.Metric);
        Assert.Equal(120.5, payload.Value);
        Assert.Equal(100.0, payload.Threshold);
        Assert.Equal("порог превышен три раза подряд", payload.Reason);
        Assert.Equal("192.168.1.1", payload.Target);
        Assert.Equal("Шлюз", payload.Monitor);
    }

    /// <summary>
    /// Пустых полей в теле нет.
    /// </summary>
    /// <remarks>
    /// Оговорка в самом канале говорит, что настройки задаются и атрибуту, и экземпляру
    /// контекста, иначе <c>DefaultIgnoreCondition</c> из атрибута теряется и в теле
    /// появляются <c>null</c>. Там же сказано, что это <b>уже было</b> с протоколом
    /// агента. Ошибка такого рода не роняет отправку: приёмник получает поле со
    /// значением null и разбирает его как настоящее.
    /// </remarks>
    [Fact]
    public void Payload_OmitsEmptyFieldsInsteadOfSendingNulls()
    {
        var payload = WebhookAlertChannel.Build(
            Notification(metric: null, value: null, threshold: null, runId: null));

        var json = Serialize(payload);

        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("metric", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runId", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Имена полей латиницей и в нижнем верблюде — их разбирает чужой скрипт.</summary>
    [Fact]
    public void Payload_UsesCamelCaseFieldNames()
    {
        var json = Serialize(WebhookAlertChannel.Build(Notification()));

        Assert.Contains("\"monitorId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"at\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MonitorId\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Кириллица уходит как есть, а не сбежавшими последовательностями.
    /// </summary>
    /// <remarks>
    /// Имя монитора и причина попадают в сообщение, которое человек прочтёт
    /// в Telegram. <c>Шлюз</c> вместо «Шлюз» формально
    /// корректен и нечитаем.
    /// </remarks>
    [Fact]
    public void Payload_KeepsCyrillicReadable()
    {
        var json = Serialize(WebhookAlertChannel.Build(Notification()));

        Assert.Contains("Шлюз", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0428", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_CarriesTheRunItCameFrom()
    {
        var runId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        Assert.Equal(runId.ToString(), WebhookAlertChannel.Build(Notification(runId: runId)).RunId);
    }

    /// <summary>Сериализация тем же контекстом, которым пользуется сам канал.</summary>
    private static string Serialize(WebhookPayload payload)
    {
        // Настройки повторяют те, что заданы каналу: проверять надо поведение
        // канала, а не поведение сериализатора по умолчанию.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        return JsonSerializer.Serialize(payload, new WebhookJsonContext(options).WebhookPayload);
    }
}
