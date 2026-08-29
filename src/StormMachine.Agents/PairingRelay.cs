using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;

namespace StormMachine.Agents;

/// <summary>
/// Передаёт ход сопряжения наблюдателю пробы.
/// </summary>
/// <remarks>
/// Существует вместо <see cref="Progress{T}"/> ради времени доставки. <c>Progress</c>
/// откладывает вызов в пул потоков, а сообщение здесь одно и срочное: «жду звонка
/// агента, на его машине набрать вот это». Прийти оно должно до того, как ожидание
/// закончится, иначе оно бесполезно — набирать уже поздно.
/// <para>
/// До И-19 оба вызывающих писали это сообщение прямо в <c>Console</c>. Графический
/// клиент собран как <c>WinExe</c>, консоли у него нет, и указание пропадало —
/// прогон стоял до истечения срока, ничего не прося и не объясняя.
/// </para>
/// </remarks>
internal sealed class PairingRelay(IProbeObserver observer) : IProgress<PairingProgress>
{
    private readonly IProbeObserver _observer = observer ?? throw new ArgumentNullException(nameof(observer));

    public void Report(PairingProgress value)
    {
        if (value is { Message.Length: > 0 })
        {
            _observer.OnProgress(value.Message);
        }
    }
}
