using System.Diagnostics;

namespace StormMachine.Protocol;

/// <summary>
/// Предложение сопряжения: код, который годен один раз и недолго.
/// </summary>
/// <remarks>
/// Код — разовое разрешение, а не пароль. Из этого следуют оба ограничения, и оба
/// не косметические.
/// <para>
/// <b>Одноразовость.</b> Код диктуют вслух по телефону, пишут в переписке и произносят
/// в помещении, где есть посторонние. После того как им воспользовались по назначению,
/// он обязан перестать работать: иначе услышавший его сопрягается вторым, и оператор
/// об этом не узнает — у него всё прошло успешно.
/// </para>
/// <para>
/// <b>Срок.</b> Агент на площадке живёт неделями. Код, выданный при запуске и годный
/// до перезапуска, — это постоянный пароль, написанный на экране, и назвать его разовым
/// значило бы соврать. Отсчёт идёт по <see cref="Stopwatch"/>, а не по системным часам:
/// перевод часов не должен ни продлевать срок, ни обрывать его.
/// </para>
/// </remarks>
public sealed class PairingOffer
{
    private readonly long _issuedAt;
    private readonly long _lifetimeTicks;

    private int _used;

    private PairingOffer(string code, TimeSpan lifetime)
    {
        Code = code;
        Lifetime = lifetime;
        _issuedAt = Stopwatch.GetTimestamp();
        _lifetimeTicks = (long)(Stopwatch.Frequency * lifetime.TotalSeconds);
    }

    /// <summary>Сам код. Годен он или нет, говорит <see cref="CodeIfValid"/>.</summary>
    public string Code { get; }

    public TimeSpan Lifetime { get; }

    /// <summary>Код уже использован по назначению.</summary>
    public bool IsUsed => Volatile.Read(ref _used) != 0;

    public bool IsExpired => Stopwatch.GetTimestamp() - _issuedAt > _lifetimeTicks;

    /// <summary>Сколько осталось. Ноль — срок вышел.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = _lifetimeTicks - (Stopwatch.GetTimestamp() - _issuedAt);

            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency);
        }
    }

    /// <summary>Код, если им ещё можно воспользоваться. Иначе <c>null</c>.</summary>
    public string? CodeIfValid => IsUsed || IsExpired ? null : Code;

    /// <summary>Код для показа человеку: группами, как его читают вслух.</summary>
    public string ForHumans => PairingCode.ForHumans(Code);

    /// <summary>Помечает код использованным. Второй раз возвращает <c>false</c>.</summary>
    public bool Consume() => Interlocked.Exchange(ref _used, 1) == 0;

    /// <summary>Почему код больше не годится — словами, которые можно показать.</summary>
    public string? ExplainIfSpent()
    {
        if (IsUsed)
        {
            return "Код уже использован: сопряжение по нему состоялось. "
                   + "Для следующего нужен новый код.";
        }

        return IsExpired
            ? $"Срок кода истёк: он был годен {Lifetime.TotalMinutes:0} мин. Нужен новый код."
            : null;
    }

    public static PairingOffer Issue() => Issue(PairingCode.Lifetime);

    public static PairingOffer Issue(TimeSpan lifetime) => new(PairingCode.Generate(), lifetime);

    /// <summary>Предложение по коду, названному снаружи. Для проверок и для повторной выдачи.</summary>
    public static PairingOffer For(string code, TimeSpan lifetime) =>
        new(PairingCode.Normalize(code), lifetime);
}
