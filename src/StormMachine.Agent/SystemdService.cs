using System.Diagnostics;
using System.Runtime.Versioning;

namespace StormMachine.Agent;

/// <summary>
/// Постоянная точка на Linux — юнитом systemd.
/// </summary>
/// <remarks>
/// Команда у оператора одна и та же — <c>storm-agent service install</c>, — а способ
/// разный: на Windows это диспетчер служб, здесь systemd. Разводить это по двум
/// командам значило бы заставить человека знать, на какой он машине, — продукт
/// и так знает.
/// <para>
/// Два размещения, и выбор между ними не вкусовой. Юнит системы
/// (<c>/etc/systemd/system</c>) поднимается при загрузке машины и живёт без входа
/// пользователя — но требует root. Пользовательский юнит ставится без прав вовсе,
/// но по умолчанию умирает вместе с сеансом; об этом сказано прямо, а не умолчано.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class SystemdService
{
    public const string UnitName = "storm-agent.service";

    /// <summary>Текст юнита. Он же кладётся в комплект агента — чтобы ставить руками.</summary>
    public static string Unit(string executable, bool system)
    {
        var directory = Path.GetDirectoryName(executable) ?? "/opt/storm-agent";

        return $"""
            [Unit]
            Description=Storm Machine — агент измерений
            Documentation=https://github.com/voidnery/storm_machine
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=simple
            ExecStart={executable} listen
            WorkingDirectory={directory}
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy={(system ? "multi-user.target" : "default.target")}

            """;
    }

    public static int Install(string executable)
    {
        var system = Environment.IsPrivilegedProcess;
        var path = UnitPath(system);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Unit(executable, system));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Не удалось записать {path}: {ex.Message}");

            return 1;
        }

        Console.WriteLine($"Юнит записан: {path}");

        var scope = system ? string.Empty : "--user ";

        if (Systemctl($"{scope}daemon-reload") != 0)
        {
            // systemd на машине может не быть вовсе — контейнер, busybox, chroot.
            // Юнит уже записан, и это не ошибка: его поставят тем, что там есть.
            Console.WriteLine(
                "systemctl не отработал. Юнит оставлен на месте — поставьте его тем, "
                + "что принято на этой машине, или запускайте агента командой listen.");

            return 1;
        }

        var code = Systemctl($"{scope}enable --now storm-agent");

        if (code == 0 && !system)
        {
            Console.WriteLine(
                "Юнит пользовательский: он работает, пока открыт сеанс. Чтобы агент "
                + "поднимался при загрузке машины, поставьте службу от root "
                + "или включите задержку сеанса: loginctl enable-linger $USER");
        }

        return code;
    }

    public static int Uninstall()
    {
        var system = Environment.IsPrivilegedProcess;
        var scope = system ? string.Empty : "--user ";

        Systemctl($"{scope}disable --now storm-agent");

        var path = UnitPath(system);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Юнит удалён: {path}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Не удалось удалить {path}: {ex.Message}");

            return 1;
        }

        return Systemctl($"{scope}daemon-reload");
    }

    private static string UnitPath(bool system) => system
        ? $"/etc/systemd/system/{UnitName}"
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "systemd",
            "user",
            UnitName);

    private static int Systemctl(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("systemctl", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"systemctl не запустился: {ex.Message}");

            return 1;
        }
    }
}
