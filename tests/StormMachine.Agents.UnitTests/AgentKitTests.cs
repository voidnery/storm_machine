using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using StormMachine.Agents.Kits;
using StormMachine.Application.Agents;

namespace StormMachine.Agents.UnitTests;

/// <summary>
/// Комплект агента: один файл, который увозят на другой конец.
/// </summary>
/// <remarks>
/// Проверяется не «собралось без исключения», а то, ради чего комплект существует:
/// внутри лежит рабочий бинарь, написано, что сделать, и для Linux — готовый юнит.
/// Комплект, который собрался и не содержит одного из трёх, бесполезен ровно там,
/// где его открывают, — на чужой площадке, где спросить некого.
/// </remarks>
public sealed class AgentKitTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "storm-tests",
        $"комплект-{Guid.NewGuid():N}");

    public AgentKitTests() => Directory.CreateDirectory(Path.Combine(_directory, AgentBinaryStore.FolderName));

    [Fact(DisplayName = "Комплект для Linux: бинарь исполняемый, юнит и написанное на месте")]
    public async Task LinuxKit_CarriesTheBinaryTheUnitAndTheWords()
    {
        Pack(AgentPlatform.LinuxX64, "двоичный агент");

        var kit = await BuildAsync(AgentPlatform.LinuxX64);

        Assert.True(File.Exists(kit.Path));

        var entries = await ReadTarAsync(kit.Path);

        Assert.Contains("storm-agent", entries.Keys);
        Assert.Contains(IAgentKitBuilder.ReadmeName, entries.Keys);
        Assert.Contains("storm-agent.service", entries.Keys);

        // Признак исполняемости — половина смысла tar вместо zip: без него человеку
        // на том конце пришлось бы догадаться про chmod.
        Assert.True(entries["storm-agent"].Mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(entries["storm-agent"].Mode.HasFlag(UnixFileMode.OtherExecute));

        var readme = entries[IAgentKitBuilder.ReadmeName].Text;

        // Адрес, порт и отпечаток подставлены: человек на том конце ничего
        // не выясняет и не набирает по памяти.
        Assert.Contains("192.168.1.5", readme, StringComparison.Ordinal);
        Assert.Contains("--порт 9700", readme, StringComparison.Ordinal);
        Assert.Contains("ABCD1234", readme, StringComparison.Ordinal);

        // Кода в комплекте нет и быть не должно: он живёт десять минут.
        Assert.Contains("Код живёт 10 минут", readme, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Комплект для Windows: zip с бинарём и написанным")]
    public async Task WindowsKit_IsAZip()
    {
        Pack(AgentPlatform.WindowsX64, "двоичный агент");

        var kit = await BuildAsync(AgentPlatform.WindowsX64);

        using var archive = ZipFile.OpenRead(kit.Path);

        Assert.Contains(archive.Entries, e => e.Name == "storm-agent.exe");
        Assert.Contains(archive.Entries, e => e.Name == IAgentKitBuilder.ReadmeName);

        // Юнита systemd здесь нет: на Windows постоянную точку заводит служба.
        Assert.DoesNotContain(archive.Entries, e => e.Name.EndsWith(".service", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Платформы без агента в поставке не предлагаются")]
    public void MissingPlatform_IsNotOffered()
    {
        Pack(AgentPlatform.LinuxX64, "двоичный агент");

        var binaries = new AgentBinaryStore(_directory);

        Assert.True(binaries.Has(AgentPlatform.LinuxX64));

        // Пункт, который нельзя выдать, — обещание, которого продукт не выполнит.
        Assert.False(binaries.Has(AgentPlatform.LinuxArm64));
        Assert.False(binaries.Has(AgentPlatform.MacX64));
    }

    [Fact(DisplayName = "Агента для платформы нет — сказано словами, а не отказом")]
    public async Task WithoutTheBinary_TheRefusalIsNamed()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildAsync(AgentPlatform.LinuxArm64));

        Assert.Contains("Linux ARM64", error.Message, StringComparison.Ordinal);
        Assert.Contains("agents", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Кладёт в поставку «агента» — для проверки важно не содержимое, а путь.</summary>
    private void Pack(AgentPlatform platform, string content)
    {
        var zip = Path.Combine(_directory, AgentBinaryStore.FolderName, AgentPlatforms.PackedName(platform));

        using var file = File.Create(zip);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        var entry = archive.CreateEntry(AgentPlatforms.ExecutableName(platform));

        using var stream = new StreamWriter(entry.Open(), Encoding.UTF8);
        stream.Write(content);
    }

    private Task<AgentKit> BuildAsync(AgentPlatform platform) =>
        new AgentKitWriter(new AgentBinaryStore(_directory)).BuildAsync(new AgentKitRequest
        {
            Platform = platform,
            OutputPath = Path.Combine(_directory, AgentPlatforms.KitName(platform)),
            ClientThumbprint = "ABCD1234 5678",
            ClientAddresses = ["192.168.1.5", "10.0.0.2"],
            Port = 9700,
            ClientName = "стенд",
        });

    private static async Task<Dictionary<string, (UnixFileMode Mode, string Text)>> ReadTarAsync(string path)
    {
        var found = new Dictionary<string, (UnixFileMode, string)>(StringComparer.Ordinal);

        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            using var text = new MemoryStream();

            if (entry.DataStream is { } data)
            {
                await data.CopyToAsync(text);
            }

            found[entry.Name] = (entry.Mode, Encoding.UTF8.GetString(text.ToArray()));
        }

        return found;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог уберёт система.
        }
    }
}
