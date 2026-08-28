using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace StormMachine.Protocol;

/// <summary>
/// Личность стороны: сертификат и его отпечаток.
/// </summary>
/// <remarks>
/// Сертификат самоподписанный и живёт в файле рядом с продуктом, а не в хранилище
/// Windows: агент — портативный бинарь на чужой машине, и записи в хранилище ему никто
/// не разрешит. Спайк-05 проверил, что этого достаточно.
/// <para>
/// Доверие строится на отпечатке, а не на цепочке. Это не упрощение: у портативного
/// агента нет и не может быть центра сертификации, поэтому проверка по цепочке отвергает
/// и настоящий сертификат тоже — спайк-05 показал это прямо. Отпечаток же подделать
/// нельзя, а субъект и срок — можно, и подделка с тем же <c>CN</c> отвергается.
/// </para>
/// </remarks>
public sealed class PeerIdentity
{
    /// <summary>Пароль контейнера PFX. Файл защищён правами файловой системы, а не им.</summary>
    /// <remarks>
    /// Секрета здесь нет и быть не может: пароль пришлось бы хранить рядом с тем, что он
    /// защищает. Он существует потому, что формат PFX его требует, и назван так, чтобы
    /// никто не принял его за меру безопасности.
    /// </remarks>
    private const string ContainerPassword = "not-a-secret";

    private PeerIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Thumbprint = ThumbprintOf(certificate.RawData);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 сертификата в шестнадцатеричном виде — то, что запоминает собеседник.</summary>
    public string Thumbprint { get; }

    /// <summary>Отпечаток, разбитый на группы: его читают вслух и сверяют глазами.</summary>
    public string ThumbprintForHumans => Group(Thumbprint);

    public static string ThumbprintOf(byte[] rawCertificateData) =>
        Convert.ToHexString(SHA256.HashData(rawCertificateData));

    /// <summary>Отпечаток группами по четыре знака — чтобы его можно было сверить глазами.</summary>
    public static string Group(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);

        var parts = new List<string>((thumbprint.Length / 4) + 1);

        for (var at = 0; at < thumbprint.Length; at += 4)
        {
            parts.Add(thumbprint.Substring(at, Math.Min(4, thumbprint.Length - at)));
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Берёт личность из файла или создаёт новую.
    /// </summary>
    /// <remarks>
    /// Новая личность означает новый отпечаток, а значит — потерю всех сопряжений.
    /// Поэтому файл не перезаписывается, пока сертификат не истёк: удалить его должен
    /// человек, осознанно, а не программа при первом же непонятном сбое чтения.
    /// </remarks>
    public static PeerIdentity LoadOrCreate(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (File.Exists(path))
        {
            var existing = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), ContainerPassword);

            if (existing.NotAfter > DateTime.Now.AddDays(1))
            {
                return new PeerIdentity(existing);
            }

            existing.Dispose();
        }

        var (identity, container) = CreateWithContainer(name);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Байты берутся те же, из которых собрана личность. Повторно экспортировать
        // уже загруженный сертификат нельзя: ключ, поднятый из PFX, не помечен
        // экспортируемым, и Windows отвечает «ключ не может быть использован».
        File.WriteAllBytes(path, container);

        return identity;
    }

    public static PeerIdentity Create(string name) => CreateWithContainer(name).Identity;

    /// <summary>Личность из сохранённого контейнера PFX.</summary>
    public static PeerIdentity FromContainer(byte[] container)
    {
        ArgumentNullException.ThrowIfNull(container);

        return new PeerIdentity(X509CertificateLoader.LoadPkcs12(container, ContainerPassword));
    }

    /// <summary>
    /// Создаёт личность и отдаёт контейнер, из которого она собрана.
    /// </summary>
    /// <remarks>
    /// Контейнер отдаётся сразу, потому что повторно экспортировать уже загруженный
    /// сертификат нельзя: ключ, поднятый из PFX, не помечен экспортируемым, и Windows
    /// отвечает «ключ не может быть использован». Тот, кто сохраняет личность, обязан
    /// сохранить именно эти байты.
    /// </remarks>
    public static (PeerIdentity Identity, byte[] Container) CreateWithContainer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var key = RSA.Create(2048);

        var request = new CertificateRequest(
            $"CN={name}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        // Оба назначения: сторона бывает и сервером, и клиентом — направление
        // соединения выбирается при сопряжении, а не устройством продукта.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")],
                critical: false));

        var alternative = new SubjectAlternativeNameBuilder();
        alternative.AddDnsName(name);
        alternative.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternative.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));

        // Круг через PFX обязателен: сертификат, собранный в памяти, хранит закрытый ключ
        // не так, как ожидает SslStream на Windows, и сервер его не находит. Выяснено
        // спайком-05 — без этого рукопожатие падало с невнятной ошибкой.
        var container = certificate.Export(X509ContentType.Pfx, ContainerPassword);
        var exported = X509CertificateLoader.LoadPkcs12(container, ContainerPassword);

        certificate.Dispose();

        return (new PeerIdentity(exported), container);
    }
}
