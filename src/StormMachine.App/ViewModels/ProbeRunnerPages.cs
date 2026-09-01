using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Локальные тесты: проверки машин своей сети и измерения между точками.
/// </summary>
/// <remarks>
/// Раздел стоял заглушкой с И-4 и был доделан в И-24 по прямому требованию оператора.
/// Никакой своей логики: форма строится из паспортов проб общим прогонщиком.
/// <para>
/// Порядок проб не случайный. До И-24+ в списке стояли только те четыре, что требуют
/// сопряжённого агента, — и раздел «Локальные тесты» не умел проверить машину
/// в локальной сети (замечание оператора). Первыми идут пробы, работающие в одиночку:
/// ping, TCP-connect, UDP. Ими проверяют соседа по сети, и именно за этим сюда заходят
/// чаще всего.
/// </para>
/// <para>
/// Непрерывный ping с живым графиком остаётся отдельной страницей «Задержка»: там
/// другой род работы — смотреть, как ведёт себя задержка минутами, а не получить
/// ответ на «жив ли узел».
/// </para>
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
            ["ping", "tcp", "udp", "dns", "throughput", "channel", "bufferbloat"]);
    }

    public ProbeRunnerViewModel Runner { get; }

    public static string Note =>
        "Ping, TCP-connect, UDP и сравнение резолверов работают в одиночку — "
        + "ими проверяют машину в своей сети.";

    public static string NoteWhy =>
        "Пропускная способность, качество канала и задержка под нагрузкой требуют "
        + "вторую точку — сопряжённого агента. Он заводится в настройках, раздел «Агенты». "
        + "Непрерывный ping с графиком живёт на отдельной странице «Задержка».";

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
