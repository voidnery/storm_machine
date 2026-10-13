using System.IO.Compression;
using StormMachine.Application.Agents;

namespace StormMachine.Agents.Kits;

/// <summary>
/// Откуда продукт берёт бинарь агента для чужой платформы.
/// </summary>
/// <remarks>
/// Агенты едут внутри поставки, а не скачиваются по требованию. Решение оператора
/// и причина простая: станцию носят на объекты, где интернета нет, а агент нужен
/// именно там. Цена — размер пакета; в сжатом виде пять платформ стоят около 33 МБ
/// вместо 80.
/// <para>
/// Windows-агент при этом лежит в поставке дважды не будет: рядом с клиентом он
/// и так есть распакованным — им пользуются прямо на этой машине. Сначала ищем
/// сжатый, потом распакованного соседа.
/// </para>
/// </remarks>
public sealed class AgentBinaryStore(string directory) : IAgentBinaries
{
    /// <summary>Подкаталог со сжатыми агентами рядом с клиентом.</summary>
    public const string FolderName = "agents";

    private readonly string _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>Есть ли у продукта агент для этой платформы.</summary>
    public bool Has(AgentPlatform platform) => Packed(platform) is not null || Loose(platform) is not null;

    /// <summary>
    /// Открывает бинарь агента на чтение. Пусто — агента для этой платформы в поставке нет.
    /// </summary>
    /// <remarks>
    /// Отдаётся поток, а не путь: сжатый агент лежит внутри архива, и распаковывать
    /// его во временный файл ради того, чтобы тут же упаковать в комплект, незачем.
    /// </remarks>
    public Stream? Open(AgentPlatform platform)
    {
        if (Packed(platform) is { } packed)
        {
            var archive = ZipFile.OpenRead(packed);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.StartsWith("storm-agent", StringComparison.Ordinal));

            if (entry is null)
            {
                archive.Dispose();

                return null;
            }

            return new EntryStream(archive, entry.Open());
        }

        return Loose(platform) is { } loose ? File.OpenRead(loose) : null;
    }

    private string? Packed(AgentPlatform platform)
    {
        var path = Path.Combine(_directory, FolderName, AgentPlatforms.PackedName(platform));

        return File.Exists(path) ? path : null;
    }

    private string? Loose(AgentPlatform platform)
    {
        // Распакованный сосед годится только для своей платформы: рядом с клиентом
        // лежит агент этой машины, и выдавать его как linux-агента нельзя.
        if (!Matches(platform))
        {
            return null;
        }

        var path = Path.Combine(_directory, AgentPlatforms.ExecutableName(platform));

        return File.Exists(path) ? path : null;
    }

    private static bool Matches(AgentPlatform platform) => platform switch
    {
        AgentPlatform.WindowsX64 => OperatingSystem.IsWindows(),
        AgentPlatform.LinuxX64 => OperatingSystem.IsLinux(),
        AgentPlatform.MacArm64 or AgentPlatform.MacX64 => OperatingSystem.IsMacOS(),
        _ => false,
    };

    /// <summary>Поток записи архива, который держит сам архив открытым, пока его читают.</summary>
    private sealed class EntryStream(ZipArchive archive, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                archive.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
