using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Snmp;
using SnmpException = StormMachine.Application.Abstractions.SnmpException;
using TimeoutException = Lextm.SharpSnmpLib.Messaging.TimeoutException;

namespace StormMachine.Snmp;

/// <summary>
/// Один разговор с устройством.
/// </summary>
/// <remarks>
/// Отдельный объект, потому что у третьей версии есть состояние: прежде чем задать
/// первый вопрос, надо узнать идентификатор машины ответчика и её счётчики времени
/// (RFC 3414 §4). Делать это перед каждым запросом — лишний круг по сети на каждое
/// поле таблицы; хранить в сеансе — ровно то, для чего сеанс и нужен.
/// <para>
/// Сеанс живёт недолго и не переиспользуется между опросами: счётчики времени
/// устаревают, и устройство на устаревшие ответит отчётом, а не данными.
/// </para>
/// </remarks>
internal sealed class SnmpSession
{
    /// <summary>Сколько строк просить за один запрос при массовом чтении.</summary>
    private const int Repetitions = 20;

    private readonly IPEndPoint _endpoint;
    private readonly SnmpCredential _credential;
    private readonly VersionCode _version;
    private readonly OctetString _identity;
    private readonly IPrivacyProvider? _privacy;
    private ISnmpMessage? _report;

    private SnmpSession(IPEndPoint endpoint, SnmpCredential credential)
    {
        _endpoint = endpoint;
        _credential = credential;

        _version = credential.Version switch
        {
            SnmpVersion.V1 => VersionCode.V1,
            SnmpVersion.V2c => VersionCode.V2,
            _ => VersionCode.V3,
        };

        _identity = new OctetString(credential.Version == SnmpVersion.V3
            ? credential.UserName ?? string.Empty
            : credential.Community ?? string.Empty);

        _privacy = credential.Version == SnmpVersion.V3 ? Privacy(credential) : null;
    }

    public static async Task<SnmpSession> OpenAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(await ResolveAsync(host, cancellationToken).ConfigureAwait(false),
            credential.Port);

        var session = new SnmpSession(endpoint, credential);

        if (credential.Version == SnmpVersion.V3)
        {
            await session.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>Читает перечисленные узлы одним запросом.</summary>
    public async Task<IReadOnlyList<Variable>> GetAsync(
        IReadOnlyList<string> oids,
        CancellationToken cancellationToken)
    {
        var asked = oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();

        return await Attempt(
            async token =>
            {
                if (_version != VersionCode.V3)
                {
                    return (IReadOnlyList<Variable>)await Messenger
                        .GetAsync(_version, _endpoint, _identity, asked, token)
                        .ConfigureAwait(false);
                }

                var request = new GetRequestMessage(
                    VersionCode.V3,
                    Messenger.NextMessageId,
                    Messenger.NextRequestId,
                    _identity,
                    OctetString.Empty,
                    asked,
                    _privacy!,
                    Messenger.MaxMessageSize,
                    _report!);

                var answer = await request
                    .GetResponseAsync(_endpoint, new UserRegistry(), token)
                    .ConfigureAwait(false);

                return (IReadOnlyList<Variable>)answer.Pdu().Variables;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Обходит ветку целиком.
    /// </summary>
    /// <remarks>
    /// Первая версия умеет только по одному узлу за запрос; вторая и третья читают
    /// пачками. Разница на таблице портов большого коммутатора — секунды против минут,
    /// и это ещё одна причина, по которой v1 стоит считать вынужденной.
    /// </remarks>
    public async Task<IReadOnlyList<Variable>> WalkAsync(
        string root,
        int limit,
        CancellationToken cancellationToken)
    {
        var found = new List<Variable>();
        var subtree = new ObjectIdentifier(root);

        await Attempt<object?>(
            async token =>
            {
                if (_version == VersionCode.V1)
                {
                    await Messenger
                        .WalkAsync(_version, _endpoint, _identity, subtree, found, WalkMode.WithinSubtree, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await Messenger.BulkWalkAsync(
                        _version,
                        _endpoint,
                        _identity,
                        OctetString.Empty,
                        subtree,
                        found,
                        Repetitions,
                        WalkMode.WithinSubtree,

                        // Пара шифрования и отчёт о машине ответчика относятся только
                        // к третьей версии. Подпись метода требует их всегда, для v2c
                        // библиотека их не читает — отсюда явное «знаю, что делаю».
                        _privacy!,
                        _report!,
                        token).ConfigureAwait(false);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);

        // Ограничение — защита от таблицы пересылки крупного коммутатора: там бывают
        // десятки тысяч записей, и вываливать их целиком в консоль незачем.
        return found.Count > limit ? [.. found.Take(limit)] : found;
    }

    /// <summary>
    /// Узнаёт идентификатор машины ответчика — обязательный первый шаг третьей версии.
    /// </summary>
    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        _report = await Attempt(
            async token =>
            {
                var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);

                return (ISnmpMessage)await discovery.GetResponseAsync(_endpoint, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Выполняет запрос с повторами и переводит отказы на язык продукта.
    /// </summary>
    /// <remarks>
    /// Повторы здесь, а не в библиотеке, потому что UDP теряет пакеты молча и один
    /// потерянный запрос неотличим от выключенного SNMP. Число повторов задаёт оператор:
    /// на объекте через узкий канал разумно больше, в локальной сети — ноль.
    /// </remarks>
    private async Task<T> Attempt<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        Exception? last = null;

        for (var attempt = 0; attempt <= _credential.Retries; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            deadline.CancelAfter(_credential.Timeout);

            try
            {
                return await action(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException
                                           or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
            }
            catch (ErrorException ex)
            {
                // Устройство ответило кодом ошибки: ветки нет или доступ к ней закрыт.
                // Повторять бессмысленно — ответ будет тот же.
                throw new SnmpException(Explain(ex), Reason(ex), ex);
            }
            catch (Lextm.SharpSnmpLib.SnmpException ex)
            {
                throw new SnmpException(
                    $"Ответ устройства не разобран: {ex.Message}",
                    SnmpFailure.BadAnswer,
                    ex);
            }
        }

        throw new SnmpException(
            $"Устройство {_endpoint.Address} не ответило за "
            + $"{_credential.Timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} с"
            + (_credential.Retries > 0
                ? $" и {_credential.Retries} повтор(ов)."
                : ".")
            + " Возможные причины: SNMP выключен, порт закрыт списком доступа "
            + "или учётные данные не подошли — отвергнутый запрос устройство оставляет без ответа.",
            SnmpFailure.NoAnswer,
            last);
    }

    private static string Explain(ErrorException ex) => ex.Body?.Pdu().ErrorStatus.ToInt32() switch
    {
        2 => "У устройства нет такой ветки (noSuchName).",
        5 => "Устройство отказало в доступе к этой ветке (genErr).",
        6 => "Доступ к ветке запрещён (noAccess).",
        _ => $"Устройство вернуло ошибку: {ex.Message}",
    };

    private static SnmpFailure Reason(ErrorException ex) => ex.Body?.Pdu().ErrorStatus.ToInt32() switch
    {
        2 or 6 => SnmpFailure.NoSuchObject,
        _ => SnmpFailure.Rejected,
    };

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        try
        {
            var found = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

            return found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? found.FirstOrDefault()
                   ?? throw new SnmpException($"Имя {host} не разрешилось в адрес.", SnmpFailure.UnknownHost);
        }
        catch (SocketException ex)
        {
            throw new SnmpException($"Имя {host} не разрешилось в адрес.", SnmpFailure.UnknownHost, ex);
        }
    }

    /// <summary>
    /// Собирает пару «проверка подлинности — шифрование» третьей версии.
    /// </summary>
    /// <remarks>
    /// Без проверки подлинности берётся особая пара-заглушка: она не считает хеш
    /// и не шифрует, и это единственный законный способ говорить на v3 в режиме
    /// <c>noAuthNoPriv</c>. Своя реализация здесь была бы дырой, замаскированной
    /// под совместимость.
    /// </remarks>
    private static IPrivacyProvider Privacy(SnmpCredential credential)
    {
        if (credential.AuthProtocol == SnmpAuthProtocol.None)
        {
            return DefaultPrivacyProvider.DefaultPair;
        }

        var password = new OctetString(credential.AuthPassword ?? string.Empty);

        IAuthenticationProvider authentication = credential.AuthProtocol switch
        {
#pragma warning disable CS0618 // Устарели, но встречаются на оборудовании, которое иного не умеет.
            SnmpAuthProtocol.Md5 => new MD5AuthenticationProvider(password),
            SnmpAuthProtocol.Sha1 => new SHA1AuthenticationProvider(password),
#pragma warning restore CS0618
            SnmpAuthProtocol.Sha384 => new SHA384AuthenticationProvider(password),
            SnmpAuthProtocol.Sha512 => new SHA512AuthenticationProvider(password),
            _ => new SHA256AuthenticationProvider(password),
        };

        if (credential.PrivacyProtocol == SnmpPrivacyProtocol.None)
        {
            return new DefaultPrivacyProvider(authentication);
        }

        var secret = new OctetString(credential.PrivacyPassword ?? string.Empty);

        return credential.PrivacyProtocol switch
        {
#pragma warning disable CS0618 // DES устарел; оставлен ради старого оборудования.
            SnmpPrivacyProtocol.Des => new DESPrivacyProvider(secret, authentication),
#pragma warning restore CS0618
            SnmpPrivacyProtocol.Aes192 => new AES192PrivacyProvider(secret, authentication),
            SnmpPrivacyProtocol.Aes256 => new AES256PrivacyProvider(secret, authentication),
            _ => new AESPrivacyProvider(secret, authentication),
        };
    }
}
