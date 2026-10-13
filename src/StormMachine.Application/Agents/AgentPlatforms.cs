namespace StormMachine.Application.Agents;

/// <summary>Куда поедет агент.</summary>
public enum AgentPlatform
{
    WindowsX64,
    LinuxX64,
    LinuxArm64,
    MacArm64,
    MacX64,
}

/// <summary>
/// Как продукт называет платформы агента и что для них собирает.
/// </summary>
/// <remarks>
/// Словарь один на окно, консоль и упаковку — по той же причине, что и остальные
/// словари продукта: расхождение здесь стоит человеку скачанного не того файла.
/// <para>
/// Для Windows комплект — zip, для остальных — tar.gz. Это не вкусовщина: zip
/// не хранит признак исполняемости, и после распаковки на Linux человеку пришлось бы
/// догадаться про <c>chmod +x</c>. Tar хранит права, и агент запускается сразу —
/// а комплект затем и собирается, чтобы на том конце ничего не пришлось понимать.
/// </para>
/// </remarks>
public static class AgentPlatforms
{
    /// <summary>Все платформы в порядке, в котором их показывают человеку.</summary>
    public static IReadOnlyList<AgentPlatform> All { get; } =
    [
        AgentPlatform.WindowsX64,
        AgentPlatform.LinuxX64,
        AgentPlatform.LinuxArm64,
        AgentPlatform.MacArm64,
        AgentPlatform.MacX64,
    ];

    /// <summary>Опознание платформы в .NET — им собирают бинарь.</summary>
    public static string Rid(AgentPlatform platform) => platform switch
    {
        AgentPlatform.WindowsX64 => "win-x64",
        AgentPlatform.LinuxX64 => "linux-x64",
        AgentPlatform.LinuxArm64 => "linux-arm64",
        AgentPlatform.MacArm64 => "osx-arm64",
        _ => "osx-x64",
    };

    /// <summary>Как платформа называется для человека.</summary>
    public static string Title(AgentPlatform platform) => platform switch
    {
        AgentPlatform.WindowsX64 => "Windows (64 бита)",
        AgentPlatform.LinuxX64 => "Linux (64 бита)",
        AgentPlatform.LinuxArm64 => "Linux ARM64",
        AgentPlatform.MacArm64 => "macOS (Apple Silicon)",
        _ => "macOS (Intel)",
    };

    /// <summary>Чем эта платформа бывает на деле — чтобы выбрать, не гадая.</summary>
    public static string About(AgentPlatform platform) => platform switch
    {
        AgentPlatform.WindowsX64 => "обычный ПК или сервер с Windows",
        AgentPlatform.LinuxX64 => "Ubuntu Server и любой обычный Linux на x86-64",
        AgentPlatform.LinuxArm64 => "одноплатник или ARM-сервер: Raspberry Pi, облачный ARM",
        AgentPlatform.MacArm64 => "Mac с процессором Apple, с 2020 года",
        _ => "Mac с процессором Intel, до 2020 года",
    };

    public static bool IsWindows(AgentPlatform platform) => platform == AgentPlatform.WindowsX64;

    public static bool IsLinux(AgentPlatform platform) =>
        platform is AgentPlatform.LinuxX64 or AgentPlatform.LinuxArm64;

    /// <summary>Имя исполняемого файла на той стороне.</summary>
    public static string ExecutableName(AgentPlatform platform) =>
        IsWindows(platform) ? "storm-agent.exe" : "storm-agent";

    /// <summary>
    /// Имя комплекта: по нему видно, что внутри и куда это везти.
    /// </summary>
    public static string KitName(AgentPlatform platform) =>
        $"storm-agent-{Rid(platform)}" + (IsWindows(platform) ? ".zip" : ".tar.gz");

    /// <summary>Имя сжатого бинаря в поставке клиента.</summary>
    public static string PackedName(AgentPlatform platform) => $"storm-agent-{Rid(platform)}.zip";
}
