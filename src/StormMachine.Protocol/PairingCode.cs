using System.Security.Cryptography;
using System.Text;

namespace StormMachine.Protocol;

/// <summary>
/// Код сопряжения: разовое подтверждение того, что стороны свели вместе намеренно.
/// </summary>
/// <remarks>
/// Код не скрывает содержимое — содержимое уже закрыто TLS. Он отвечает на другой вопрос:
/// тот ли это собеседник, которого оператор имел в виду. Без него сопряжение прошло бы
/// с кем угодно, кто дозвонился первым, а на общей сети это не гипотетический случай.
/// <para>
/// Доказательство — HMAC от кода по отпечаткам обеих сторон. Отпечатки, а не случайное
/// число, потому что доказательство должно связывать код именно с этой парой
/// сертификатов: перехваченное доказательство не годится для сопряжения с другим
/// сертификатом, а значит его нечего и перехватывать.
/// </para>
/// <para>
/// Алфавит без похожих знаков. Код читают вслух и набирают руками, и разница между
/// нулём и буквой «O» в этот момент стоит одной неудачной поездки на площадку.
/// </para>
/// </remarks>
public static class PairingCode
{
    /// <summary>Без 0/O, 1/I/L, 2/Z, 5/S, 8/B — их путают на слух и на глаз.</summary>
    private const string Alphabet = "ACDEFGHJKMNPQRTUVWXY34679";

    /// <summary>Длина кода. Шесть знаков из 25 — около 244 миллионов вариантов.</summary>
    public const int Length = 6;

    /// <summary>Сколько живёт код. Дольше — растёт окно, в которое можно попробовать чужой.</summary>
    public static TimeSpan Lifetime => TimeSpan.FromMinutes(10);

    public static string Generate()
    {
        var code = new char[Length];

        for (var i = 0; i < Length; i++)
        {
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(code);
    }

    /// <summary>Приводит к виду, в котором коды сравниваются: без пробелов и дефисов, в верхнем регистре.</summary>
    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        var builder = new StringBuilder(code.Length);

        foreach (var c in code)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Код в виде, удобном для чтения вслух.</summary>
    public static string ForHumans(string code)
    {
        var normalized = Normalize(code);

        return normalized.Length == Length
            ? $"{normalized[..3]}-{normalized[3..]}"
            : normalized;
    }

    /// <summary>
    /// Доказательство знания кода, связанное с парой сертификатов.
    /// </summary>
    /// <remarks>
    /// Отпечатки складываются в порядке сортировки, а не «свой, потом чужой»: иначе
    /// стороны считали бы разное и доказательство не сошлось бы никогда — при том, что
    /// обе стороны правы. Направление соединения на порядок влиять не должно.
    /// </remarks>
    public static string Prove(string code, string ownThumbprint, string peerThumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownThumbprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerThumbprint);

        var (first, second) = string.CompareOrdinal(ownThumbprint, peerThumbprint) <= 0
            ? (ownThumbprint, peerThumbprint)
            : (peerThumbprint, ownThumbprint);

        var key = Encoding.UTF8.GetBytes(Normalize(code));
        var message = Encoding.UTF8.GetBytes($"storm-pairing\n{first}\n{second}");

        return Convert.ToHexString(HMACSHA256.HashData(key, message));
    }

    /// <summary>
    /// Сверяет доказательство.
    /// </summary>
    /// <remarks>
    /// Сравнение постоянного времени. Разница во времени сравнения обычных строк
    /// подсказывает, сколько знаков совпало, — и подбор кода из шести знаков
    /// превращается из перебора миллионов вариантов в перебор десятков.
    /// </remarks>
    public static bool Verify(string? proof, string code, string ownThumbprint, string peerThumbprint)
    {
        if (string.IsNullOrEmpty(proof))
        {
            return false;
        }

        var expected = Prove(code, ownThumbprint, peerThumbprint);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(proof),
            Encoding.ASCII.GetBytes(expected));
    }
}
