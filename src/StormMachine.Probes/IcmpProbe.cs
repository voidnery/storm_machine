using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Probes;

/// <summary>
/// Проба ICMP Echo.
/// </summary>
/// <remarks>
/// Реализована поверх <see cref="Ping"/> (системный <c>IcmpSendEcho2</c>), потому что он
/// работает <b>без прав администратора</b> — проверено экспериментально
/// (docs/02-research.md, R-01).
/// <para>
/// Время меряется <b>только</b> через <see cref="IHighResolutionClock"/>. Значение задержки,
/// которое возвращает системный API, не используется: оно целочисленное в миллисекундах
/// и в локальной сети даёт единицы различимых значений вместо сотен. Это правило
/// проверяется архитектурным тестом.
/// </para>
/// <para>
/// Raw-сокеты рассматривались и отвергнуты: на стенде они заработали без прав
/// администратора, но полагаться на это нельзя — поведение зависит от версии Windows
/// и политик, а выигрыш в точности оказался в пределах 0.002 мс.
/// </para>
/// </remarks>
public sealed class IcmpProbe(IHighResolutionClock clock, TargetResolver resolver) : IProbe
{
    public const string ParameterCount = "count";
    public const string ParameterIntervalMs = "interval";
    public const string ParameterPayloadBytes = "size";
    public const string ParameterTimeoutMs = "timeout";
    public const string ParameterTtl = "ttl";
    public const string ParameterDontFragment = "df";

    private readonly IHighResolutionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly TargetResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Icmp,
        Name = "ping",
        Title = "ICMP Echo",
        Description = "Доступность и задержка: RTT, потери, джиттер по RFC 3550, PDV.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.IcmpEcho,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = ParameterCount, Label = "Число проб", Type = ProbeParameterType.Integer,
                DefaultValue = 4, Minimum = 1, Maximum = 1_000_000,
                Description = "Сколько пакетов отправить.",
            },
            new ProbeParameter
            {
                Name = ParameterIntervalMs, Label = "Интервал, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 1000, Minimum = 1, Maximum = 600_000,
                Description = "Пауза между отправками.",
            },
            new ProbeParameter
            {
                Name = ParameterPayloadBytes, Label = "Размер данных, байт", Type = ProbeParameterType.Integer,
                DefaultValue = 32, Minimum = 0, Maximum = 65_500,
                Description = "Полезная нагрузка без заголовков IP и ICMP.",
            },
            new ProbeParameter
            {
                Name = ParameterTimeoutMs, Label = "Таймаут, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 2000, Minimum = 1, Maximum = 60_000,
                Description = "Сколько ждать ответа.",
            },
            new ProbeParameter
            {
                Name = ParameterTtl, Label = "TTL", Type = ProbeParameterType.Integer,
                DefaultValue = 128, Minimum = 1, Maximum = 255,
                Description = "Предельное число хопов.",
            },
            new ProbeParameter
            {
                Name = ParameterDontFragment, Label = "Не фрагментировать", Type = ProbeParameterType.Boolean,
                DefaultValue = false,
                Description = "Флаг DF — нужен для поиска PMTU.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ProbeValidationError>();

        foreach (var parameter in Descriptor.Parameters)
        {
            if (!request.Parameters.TryGetValue(parameter.Name, out var raw) || raw is null)
            {
                continue;
            }

            if (parameter.Type is ProbeParameterType.Boolean or ProbeParameterType.Text)
            {
                continue;
            }

            if (!TryToDouble(raw, out var value))
            {
                errors.Add(new ProbeValidationError(parameter.Name, $"Значение «{raw}» не является числом."));
                continue;
            }

            if (parameter.Minimum is { } min && value < min)
            {
                errors.Add(new ProbeValidationError(parameter.Name, $"Минимум — {min:0.###}, получено {value:0.###}."));
            }

            if (parameter.Maximum is { } max && value > max)
            {
                errors.Add(new ProbeValidationError(parameter.Name, $"Максимум — {max:0.###}, получено {value:0.###}."));
            }
        }

        return errors;
    }

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var count = request.GetParameter(ParameterCount, 4);
        var intervalMs = request.GetParameter(ParameterIntervalMs, 1000);
        var payloadBytes = request.GetParameter(ParameterPayloadBytes, 32);
        var timeoutMs = request.GetParameter(ParameterTimeoutMs, 2000);
        var ttl = request.GetParameter(ParameterTtl, 128);
        var dontFragment = request.GetParameter(ParameterDontFragment, false);

        var address = await _resolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        observer.OnResolved(address.ToString());

        // Всё, что можно выделить заранее, выделяется до цикла: буфер, параметры, объект Ping.
        // В горячем пути остаётся только то, что аллоцирует сам системный API.
        var buffer = new byte[payloadBytes];
        FillPattern(buffer);

        var options = new PingOptions(ttl, dontFragment);
        var timeout = TimeSpan.FromMilliseconds(timeoutMs);

        using var ping = new Ping();

        await WarmUpAsync(ping, buffer, cancellationToken).ConfigureAwait(false);

        for (var sequence = 0; sequence < count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sendAt = _clock.GetTimestamp();
            var timestampUtc = DateTimeOffset.UtcNow;

            Sample sample;
            try
            {
                var reply = await ping
                    .SendPingAsync(address, timeout, buffer, options, cancellationToken)
                    .ConfigureAwait(false);

                var elapsedMs = _clock.ElapsedMilliseconds(sendAt);

                sample = reply.Status switch
                {
                    IPStatus.Success => new Sample
                    {
                        Sequence = sequence,
                        TimestampUtc = timestampUtc,
                        Value = elapsedMs,
                        Status = SampleStatus.Success,
                        Ttl = reply.Options?.Ttl,
                        RespondedBy = ResponderIfDifferent(reply.Address, address),
                    },
                    IPStatus.TtlExpired or IPStatus.TimeExceeded => new Sample
                    {
                        Sequence = sequence,
                        TimestampUtc = timestampUtc,
                        Value = elapsedMs,
                        Status = SampleStatus.TtlExpired,
                        RespondedBy = reply.Address?.ToString(),
                    },
                    IPStatus.TimedOut => Sample.Failed(sequence, timestampUtc, SampleStatus.Timeout),
                    IPStatus.PacketTooBig => Sample.Failed(sequence, timestampUtc, SampleStatus.Rejected),
                    _ => Sample.Failed(sequence, timestampUtc, SampleStatus.Unreachable),
                };
            }
            catch (OperationCanceledException)
            {
                // Прерванный прогон сохраняет измеренное — сэмплы уже отданы потребителю.
                yield break;
            }
            catch (PingException)
            {
                sample = Sample.Failed(sequence, timestampUtc, SampleStatus.Error);
            }

            yield return sample;

            if (sequence + 1 < count)
            {
                await WaitUntilNextSendAsync(sendAt, intervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Выдерживает интервал от момента предыдущей отправки, а не от момента получения ответа —
    /// иначе темп «плыл» бы вслед за задержкой сети.
    /// </summary>
    /// <remarks>
    /// Гибридное ожидание: основную часть спим, последние доли миллисекунды выбираем
    /// активным ожиданием. Причина — замеры этапа исследования: <c>Thread.Sleep(1)</c>
    /// ошибается примерно на миллисекунду, и <c>timeBeginPeriod</c> этого не исправляет
    /// (docs/02-research.md, R-10).
    /// </remarks>
    private async Task WaitUntilNextSendAsync(long sendAt, int intervalMs, CancellationToken cancellationToken)
    {
        const double SpinTailMs = 2.0;

        var elapsed = _clock.ElapsedMilliseconds(sendAt);
        var remaining = intervalMs - elapsed;

        if (remaining <= 0)
        {
            return;
        }

        if (remaining > SpinTailMs)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remaining - SpinTailMs), cancellationToken).ConfigureAwait(false);
        }

        while (_clock.ElapsedMilliseconds(sendAt) < intervalMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.SpinWait(50);
        }
    }

    /// <summary>
    /// Прогревает асинхронный путь отправки на loopback перед началом измерений.
    /// </summary>
    /// <remarks>
    /// Без прогрева первые две пробы систематически завышены: на стенде это дало
    /// 21.7 и 14.5 мс при реальных 0.5–0.7 мс. Причина — компиляция кода при первом
    /// вызове; калибровка таймера её не снимает, потому что использует синхронный путь,
    /// а это другой код.
    /// <para>
    /// Прогрев идёт <b>на loopback, а не на цель</b>: лишние пакеты в измеряемую сеть
    /// не отправляются, а искажение от компиляции устраняется.
    /// </para>
    /// </remarks>
    private static async Task WarmUpAsync(Ping ping, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await ping
                .SendPingAsync(IPAddress.Loopback, TimeSpan.FromMilliseconds(500), buffer, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Прогрев — уточнение, а не условие работы. Недоступен loopback — просто
            // начинаем измерять, первые пробы будут завышены.
        }
    }

    private static string? ResponderIfDifferent(IPAddress? responder, IPAddress expected) =>
        responder is null || responder.Equals(expected) ? null : responder.ToString();

    private static void FillPattern(byte[] buffer)
    {
        // Неповторяющийся узор вместо нулей: сжатие и оптимизации на промежуточных узлах
        // не должны искажать измерение.
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)('a' + (i % 23));
        }
    }

    private static bool TryToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case double d: value = d; return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
