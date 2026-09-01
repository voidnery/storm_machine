using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Локальные тесты: скорость между точками, задержка под нагрузкой, сравнение резолверов.
/// </summary>
/// <remarks>
/// Раздел стоял заглушкой с И-4 и был доделан в И-24 по прямому требованию оператора.
/// Никакой своей логики: форма строится из паспортов проб общим прогонщиком.
/// </remarks>
public sealed class LocalTestsPageViewModel : PageViewModel
{
    public LocalTestsPageViewModel(
        NavigationSection section,
        RunnerService runner,
        IProbeRegistry registry,
        IRunStore store,
        IAgentDirectory agents,
        IDeviceStore devices)
        : base(section)
    {
        Runner = new ProbeRunnerViewModel(runner, registry, store, agents, devices,
            ["throughput", "channel", "bufferbloat", "dns"]);
    }

    public ProbeRunnerViewModel Runner { get; }

    public static string Note =>
        "Скорость, качество канала и задержка под нагрузкой требуют вторую точку — "
        + "сопряжённого агента.";

    public static string NoteWhy =>
        "Агент сопрягается в настройках, раздел «Агенты». Сравнение резолверов DNS "
        + "работает без него.";

    public override Task ActivateAsync(CancellationToken cancellationToken = default) =>
        Runner.LoadAgentsAsync(cancellationToken);
}

/// <summary>Скорость и качество: speedtest, iperf3, задержка под нагрузкой.</summary>
public sealed class SpeedPageViewModel : PageViewModel
{
    public SpeedPageViewModel(
        NavigationSection section,
        RunnerService runner,
        IProbeRegistry registry,
        IRunStore store,
        IAgentDirectory agents,
        IDeviceStore devices)
        : base(section)
    {
        Runner = new ProbeRunnerViewModel(runner, registry, store, agents, devices,
            ["speedtest", "iperf3", "bufferbloat"]);
    }

    public ProbeRunnerViewModel Runner { get; }

    public static string Note => "Скорость наружу меряется до публичного сервера M-Lab.";

    public static string NoteWhy =>
        "Сервер выбирает их служба: у неё есть данные о загрузке узлов, которых нет "
        + "у нас. iperf3 — мост к существующему «iperf3 -s» там, где своего агента "
        + "поставить нельзя.";

    public override Task ActivateAsync(CancellationToken cancellationToken = default) =>
        Runner.LoadAgentsAsync(cancellationToken);
}
