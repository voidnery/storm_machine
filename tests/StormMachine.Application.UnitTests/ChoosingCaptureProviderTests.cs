using StormMachine.Application.Abstractions;
using StormMachine.Application.Capture;
using StormMachine.Domain.Capture;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Выбор источника захвата.
/// </summary>
/// <remarks>
/// Источников два, и выбор между ними — решение продукта, а не оператора. Цена
/// ошибки здесь не в скорости, а в совете: человеку, у которого всё решается
/// перезапуском с правами, нельзя показать «поставьте драйвер, который мы
/// не распространяем».
/// </remarks>
public sealed class ChoosingCaptureProviderTests
{
    [Fact(DisplayName = "Работающий источник выигрывает у неработающего")]
    public void Working_Wins()
    {
        var choosing = new ChoosingCaptureProvider(
        [
            new FakeSource(CaptureEngine.Npcap, CaptureRefusal.NoDriver),
            new FakeSource(CaptureEngine.WindowsBuiltIn, CaptureRefusal.None),
        ]);

        Assert.Equal(CaptureEngine.WindowsBuiltIn, choosing.Engine);
        Assert.Equal(CaptureRefusal.None, choosing.Availability);
    }

    [Fact(DisplayName = "При двух работающих берётся записанный первым")]
    public void Order_IsPreference()
    {
        // Порядок в композиции — это и есть порядок предпочтения: Npcap отдаёт
        // кадры потоком и не гоняет их через файл.
        var choosing = new ChoosingCaptureProvider(
        [
            new FakeSource(CaptureEngine.Npcap, CaptureRefusal.None),
            new FakeSource(CaptureEngine.WindowsBuiltIn, CaptureRefusal.None),
        ]);

        Assert.Equal(CaptureEngine.Npcap, choosing.Engine);
    }

    [Fact(DisplayName = "Из двух отказов показывается выполнимый совет")]
    public void Refusals_PickTheActionableAdvice()
    {
        // «Перезапустите от администратора» человек выполнит сам. «Поставьте
        // драйвер, который мы не распространяем» — почти никогда. Показать
        // второе, умолчав о первом, значит оставить его ни с чем.
        var choosing = new ChoosingCaptureProvider(
        [
            new FakeSource(CaptureEngine.Npcap, CaptureRefusal.NoDriver),
            new FakeSource(CaptureEngine.WindowsBuiltIn, CaptureRefusal.NeedsElevation),
        ]);

        Assert.Equal(CaptureRefusal.NeedsElevation, choosing.Availability);
        Assert.Equal(CaptureEngine.WindowsBuiltIn, choosing.Engine);
    }

    [Fact(DisplayName = "Выбор пересчитывается, а не запоминается")]
    public void Choice_IsRecomputed()
    {
        // Драйвер могли поставить, а продукт — перезапустить с правами, не закрывая
        // окна. Запомненный ответ пережил бы оба события и соврал бы ровно тогда,
        // когда человек сделал то, о чём его просили.
        var builtIn = new FakeSource(CaptureEngine.WindowsBuiltIn, CaptureRefusal.NeedsElevation);
        var choosing = new ChoosingCaptureProvider([builtIn]);

        Assert.Equal(CaptureRefusal.NeedsElevation, choosing.Availability);

        builtIn.Refusal = CaptureRefusal.None;

        Assert.Equal(CaptureRefusal.None, choosing.Availability);
    }

    [Fact(DisplayName = "Без источников вовсе ничего не падает")]
    public void NoSources_StayCalm()
    {
        var choosing = new ChoosingCaptureProvider([]);

        Assert.Equal(CaptureEngine.None, choosing.Engine);
        Assert.Equal(CaptureRefusal.NoDriver, choosing.Availability);
        Assert.Null(choosing.DriverDescription);
        Assert.Empty(choosing.Adapters());
    }

    [Fact(DisplayName = "Прослушивание уходит выбранному источнику")]
    public async Task Listening_GoesToTheChosenOne()
    {
        var builtIn = new FakeSource(CaptureEngine.WindowsBuiltIn, CaptureRefusal.None);
        var choosing = new ChoosingCaptureProvider(
        [
            new FakeSource(CaptureEngine.Npcap, CaptureRefusal.NoDriver),
            builtIn,
        ]);

        await choosing.ListenAsync(
            new CaptureAdapter { Id = "x", Description = "карта" },
            new CaptureOptions());

        Assert.True(builtIn.Listened);
    }

    private sealed class FakeSource(CaptureEngine engine, CaptureRefusal refusal) : ICaptureProvider
    {
        internal CaptureRefusal Refusal { get; set; } = refusal;

        internal bool Listened { get; private set; }

        public CaptureEngine Engine { get; } = engine;

        public CaptureRefusal Availability => Refusal;

        public string? DriverDescription => Refusal == CaptureRefusal.NoDriver ? null : "источник";

        public IReadOnlyList<CaptureAdapter> Adapters() => [];

        public Task<CaptureResult> ListenAsync(
            CaptureAdapter adapter,
            CaptureOptions options,
            CancellationToken cancellationToken = default)
        {
            Listened = true;

            return Task.FromResult(new CaptureResult
            {
                Adapter = adapter,
                StartedUtc = DateTimeOffset.UtcNow,
                Duration = TimeSpan.Zero,
                FramesSeen = 0,
                Unparsed = 0,
                Neighbors = [],
                Dhcp = new DhcpFinding { Sightings = [] },
            });
        }
    }
}
