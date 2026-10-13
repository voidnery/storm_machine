using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using StormMachine.Protocol;

namespace StormMachine.Agent;

/// <summary>
/// Оболочка службы Windows.
/// </summary>
/// <remarks>
/// Диспетчер служб ждёт отклика в считаные секунды, поэтому запуск здесь только
/// заводит фоновую работу и сразу возвращается. Всё, что дольше, — уже в самом агенте.
/// <para>
/// Вынесено из <c>Program</c> отдельным файлом, когда агент стал собираться под Linux
/// и macOS: тип <c>ServiceBase</c> существует только на Windows, и соседство
/// с общим кодом запуска заставляло бы читателя каждый раз проверять, где он находится.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class AgentService : ServiceBase
{
    private readonly CancellationTokenSource _stopping = new();

    private Task? _work;

    public AgentService() => ServiceName = WindowsServiceControl.ServiceName;

    protected override void OnStart(string[] args)
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        var host = new AgentHost(new AgentSettings
        {
            IdentityPath = Path.Combine(directory, "storm-agent.identity.pfx"),
            PeerBookPath = Path.Combine(directory, "storm-agent.peers.json"),
            Port = SecureChannel.DefaultPort,

            // Служба работает без человека рядом, и новых собеседников не принимает:
            // код сопряжения некому прочитать. Сопрягать надо до установки, запустив
            // агента в консоли.
            Pairing = null,
            Log = message => EventLog.WriteEntry(message),
        });

        _work = host.ListenAsync(_stopping.Token);
    }

    protected override void OnStop()
    {
        _stopping.Cancel();

        try
        {
            _work?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Прерывание — штатный способ остановки, и жаловаться на него незачем.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Установка и удаление службы Windows — через её же диспетчер.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceControl
{
    public const string ServiceName = "StormAgent";

    public static int Install(string path)
    {
        var code = Sc($"create {ServiceName} binPath= \"\\\"{path}\\\" --служба\" start= auto "
                      + "DisplayName= \"Storm Machine — агент измерений\"");

        if (code != 0)
        {
            return code;
        }

        Sc($"description {ServiceName} \"Удалённая точка измерения Storm Machine.\"");

        return Sc($"start {ServiceName}");
    }

    public static int Uninstall()
    {
        Sc($"stop {ServiceName}");

        return Sc($"delete {ServiceName}");
    }

    private static int Sc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (process is null)
        {
            Console.Error.WriteLine("Не удалось запустить sc.exe.");

            return 1;
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        if (output.Trim().Length > 0)
        {
            Console.WriteLine(output.Trim());
        }

        return process.ExitCode;
    }
}
