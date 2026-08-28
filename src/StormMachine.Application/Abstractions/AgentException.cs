namespace StormMachine.Application.Abstractions;

/// <summary>
/// Не вышло поговорить с агентом.
/// </summary>
/// <remarks>
/// Существует, чтобы показ не зависел от провода. Отказы приходят из слоя протокола
/// со своим типом исключения, а представлению о протоколе знать не положено: оно
/// не должно ни ссылаться на него, ни ловить его типы.
/// <para>
/// Появился после того, как клиент упал с необработанным исключением на втором
/// сопряжении по погашенному коду: сообщение было готово и понятно, но некому было
/// его поймать — тип исключения остался за границей слоя.
/// </para>
/// </remarks>
public sealed class AgentException : Exception
{
    public AgentException()
    {
    }

    public AgentException(string message)
        : base(message)
    {
    }

    public AgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
