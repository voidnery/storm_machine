using StormMachine.Platform;

namespace StormMachine.Platform.UnitTests;

/// <summary>
/// Защита секретов средствами Windows.
/// </summary>
/// <remarks>
/// Проверяется не стойкость DPAPI, а наш обёрточный слой: метка версии, поведение
/// на чужом значении и то, что зашифрованное не совпадает с исходным. Последнее
/// звучит очевидно ровно до того дня, когда шифрование забыли включить.
/// </remarks>
public sealed class WindowsSecretProtectorTests
{
    private readonly WindowsSecretProtector _protector = new();

    [Fact(DisplayName = "Значение расшифровывается обратно")]
    public void RoundTrip()
    {
        var secret = "пароль от почты 12345";

        Assert.Equal(secret, _protector.Unprotect(_protector.Protect(secret)));
    }

    [Fact(DisplayName = "Зашифрованное не совпадает с исходным")]
    public void CipherDiffersFromPlain()
    {
        var secret = "пароль";
        var cipher = _protector.Protect(secret);

        Assert.DoesNotContain(secret, cipher, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Формат помечен версией — его можно будет сменить, не гадая")]
    public void HasVersionPrefix() =>
        Assert.StartsWith("dpapi1:", _protector.Protect("что угодно"), StringComparison.Ordinal);

    [Fact(DisplayName = "Чужое значение возвращается как нерасшифрованное, а не бросает")]
    public void ForeignValueReturnsNull()
    {
        Assert.Null(_protector.Unprotect("просто строка"));
        Assert.Null(_protector.Unprotect("dpapi1:не base64"));
        Assert.Null(_protector.Unprotect("dpapi1:AAAA"));
    }

    [Fact(DisplayName = "Пустая строка шифруется и возвращается пустой")]
    public void EmptyString() => Assert.Equal(string.Empty, _protector.Unprotect(_protector.Protect(string.Empty)));
}
