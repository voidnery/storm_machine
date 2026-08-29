using StormMachine.Agents;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;

namespace StormMachine.Agents.UnitTests;

/// <summary>
/// Ход сопряжения доходит до наблюдателя пробы.
/// </summary>
/// <remarks>
/// Регрессия на дефект И-19. Пробы агента отдавали сообщение «жду звонка агента,
/// на его машине набрать storm-agent connect …» прямо в <c>Console.WriteLine</c>.
/// В консоли это работало. Графический клиент собран как <c>WinExe</c>, консоли
/// у него нет — указание пропадало, и прогон стоял до истечения срока ожидания,
/// не объясняя оператору ни что от него требуется, ни почему ничего не происходит.
/// <para>
/// Отсюда предмет проверки: сообщение обязано идти <b>через наблюдателя</b>. Проверять
/// «не пишет в Console» бессмысленно — вернуть туда вывод можно и не заметить этого,
/// а вот отсутствие вызова наблюдателя видно сразу.
/// </para>
/// </remarks>
public sealed class PairingRelayTests
{
    [Fact]
    public void Relay_PassesTheMessageToTheObserver()
    {
        var observer = new RecordingObserver();
        var relay = new PairingRelay(observer);

        relay.Report(new PairingProgress("Жду звонка агента «стенд» на порт 7431.", null, IsDone: false));

        Assert.Equal(["Жду звонка агента «стенд» на порт 7431."], observer.Progress);
    }

    /// <summary>
    /// Доставка синхронна.
    /// </summary>
    /// <remarks>
    /// Здесь и причина отказа от <see cref="Progress{T}"/>: он откладывает вызов
    /// в пул потоков, а сообщение одно и срочное. Прийти оно должно до того, как
    /// ожидание закончится, — иначе набирать команду уже поздно.
    /// </remarks>
    [Fact]
    public void Relay_DeliversBeforeReturning()
    {
        var observer = new RecordingObserver();
        var relay = new PairingRelay(observer);

        relay.Report(new PairingProgress("сообщение", null, IsDone: false));

        // Ни ожидания, ни опроса: если бы доставка была отложенной, здесь было бы пусто.
        Assert.Single(observer.Progress);
    }

    /// <summary>Ход сопряжения не превращается в факт: в журнале ему делать нечего.</summary>
    [Fact]
    public void Relay_DoesNotRecordFacts()
    {
        var observer = new RecordingObserver();

        new PairingRelay(observer).Report(new PairingProgress("сообщение", null, IsDone: false));

        Assert.Empty(observer.Facts);
        Assert.Null(observer.Resolved);
    }

    [Fact]
    public void Relay_IgnoresEmptyMessages()
    {
        var observer = new RecordingObserver();
        var relay = new PairingRelay(observer);

        relay.Report(new PairingProgress(string.Empty, null, IsDone: true));

        Assert.Empty(observer.Progress);
    }

    [Fact]
    public void Relay_RefusesToWorkWithoutAnObserver() =>
        Assert.Throws<ArgumentNullException>(() => new PairingRelay(null!));

    /// <summary>
    /// Код сопряжения доходит вместе с сообщением.
    /// </summary>
    /// <remarks>
    /// Код придумывает сама реализация и сообщает его до начала ожидания: оператору
    /// надо продиктовать его тому, кто стоит у агента. Он входит в текст сообщения,
    /// и потерять его нельзя — заставлять человека ждать неизвестно чего бессмысленно.
    /// </remarks>
    [Fact]
    public void Relay_CarriesTheCodeInsideTheMessage()
    {
        var observer = new RecordingObserver();

        new PairingRelay(observer).Report(
            new PairingProgress("Код сопряжения: 418-236. Продиктуйте его тому, кто у агента.", "418236", IsDone: false));

        Assert.Contains("418-236", observer.Progress[0], StringComparison.Ordinal);
    }

    /// <summary>Наблюдатель, который запоминает всё, что ему сказали.</summary>
    private sealed class RecordingObserver : IProbeObserver
    {
        public List<string> Progress { get; } = [];

        public List<ProbeFact> Facts { get; } = [];

        public string? Resolved { get; private set; }

        public void OnResolved(string address) => Resolved = address;

        public void OnFact(ProbeFact fact) => Facts.Add(fact);

        public void OnProgress(string message) => Progress.Add(message);
    }
}
