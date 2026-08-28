using System.Security.Cryptography;
using System.Text;
using StormMachine.Application.Abstractions;

namespace StormMachine.Platform;

/// <summary>
/// Защита секретов средствами Windows (DPAPI).
/// </summary>
/// <remarks>
/// Шифротекст привязан к учётной записи пользователя. Это даёт ровно одно свойство,
/// но нужное: файл базы, скопированный на другую машину или открытый под другой
/// учётной записью, пароль не отдаст. Базы присылают в поддержку и кладут в архивы —
/// пароль от почтового ящика в них попадать не должен.
/// <para>
/// Оборотная сторона названа прямо, а не спрятана: перенос установки на другую машину
/// секреты не переносит, и задавать их придётся заново. Обещать здесь большее нечем —
/// хранилище, переживающее смену машины, требует мастер-пароля, которого у продукта нет.
/// </para>
/// <para>
/// Дополнительная энтропия привязывает шифротекст к нашему продукту: чужая программа
/// под той же учётной записью расшифровать значение не сможет, даже получив файл.
/// </para>
/// </remarks>
public sealed class WindowsSecretProtector : ISecretProtector
{
    /// <summary>Метка версии в начале строки — чтобы формат можно было сменить, не гадая.</summary>
    private const string Prefix = "dpapi1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StormMachine.Secrets.v1");

    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain),
            Entropy,
            DataProtectionScope.CurrentUser);

        return Prefix + Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue[Prefix.Length..]);

            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Значение зашифровано другой учётной записью или на другой машине.
            // Это ожидаемый случай, а не сбой: секрет надо задать заново.
            return null;
        }
    }
}
