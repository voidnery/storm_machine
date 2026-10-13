using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using StormMachine.Application.Agents;

namespace StormMachine.Agents.Kits;

/// <summary>
/// Комплект агента: один файл, который увозят на другой конец.
/// </summary>
/// <remarks>
/// Задача комплекта — чтобы человеку на том конце <b>не пришлось ничего понимать</b>:
/// распаковал, выполнил одну команду, назвал код. Поэтому внутри не только бинарь,
/// но и написанные команды с подставленными адресом, портом и отпечатком, а для Linux —
/// готовый юнит systemd.
/// <para>
/// Кода сопряжения в комплекте нет, и это не упущение. Код живёт десять минут
/// (<c>PairingCode.Lifetime</c>), а комплект едет на площадку часами: вшитый код
/// приехал бы мёртвым, и человек на том конце решил бы, что сломан продукт. Код
/// называют голосом в момент сопряжения — так же, как и раньше.
/// </para>
/// </remarks>
public sealed class AgentKitWriter(AgentBinaryStore binaries) : IAgentKitBuilder
{
    private readonly AgentBinaryStore _binaries = binaries ?? throw new ArgumentNullException(nameof(binaries));

    private static string ReadmeName => IAgentKitBuilder.ReadmeName;

    public async Task<AgentKit> BuildAsync(AgentKitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var agent = _binaries.Open(request.Platform)
            ?? throw new InvalidOperationException(
                $"В поставке нет агента для платформы «{AgentPlatforms.Title(request.Platform)}». "
                + "Он кладётся в подкаталог «agents» при сборке выпуска.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var contents = new List<string> { AgentPlatforms.ExecutableName(request.Platform), ReadmeName };

        if (AgentPlatforms.IsLinux(request.Platform))
        {
            contents.Add(SystemdUnitName);
        }

        if (AgentPlatforms.IsWindows(request.Platform))
        {
            await WriteZipAsync(request, agent, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteTarAsync(request, agent, cancellationToken).ConfigureAwait(false);
        }

        return new AgentKit(request.OutputPath, new FileInfo(request.OutputPath).Length, contents);
    }

    private const string SystemdUnitName = "storm-agent.service";

    /// <summary>Windows: zip, права файлов там не хранятся и не нужны.</summary>
    private static async Task WriteZipAsync(AgentKitRequest request, Stream agent, CancellationToken cancellationToken)
    {
        await using var file = File.Create(request.OutputPath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        var binary = archive.CreateEntry(AgentPlatforms.ExecutableName(request.Platform), CompressionLevel.Optimal);

        await using (var entry = binary.Open())
        {
            await agent.CopyToAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        var readme = archive.CreateEntry(ReadmeName, CompressionLevel.Optimal);

        await using var text = new StreamWriter(readme.Open(), Encoding.UTF8);
        await text.WriteAsync(Readme(request)).ConfigureAwait(false);
    }

    /// <summary>
    /// Linux и macOS: tar.gz с правами.
    /// </summary>
    /// <remarks>
    /// Признак исполняемости кладётся прямо в архив (0755). Zip его не хранит,
    /// и человеку на том конце пришлось бы догадаться про <c>chmod +x</c> — ровно
    /// то «надо понимать», ради отсутствия которого комплект и собирается.
    /// </remarks>
    private static async Task WriteTarAsync(AgentKitRequest request, Stream agent, CancellationToken cancellationToken)
    {
        await using var file = File.Create(request.OutputPath);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);

        var name = AgentPlatforms.ExecutableName(request.Platform);

        // Поток агента приходит из архива и длины не знает: tar требует размер заранее,
        // поэтому бинарь прокачивается через память. Пятнадцать мегабайт — цена,
        // которую платит один раз тот, кто выдаёт комплект.
        using var buffer = new MemoryStream();
        await agent.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        var binary = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = buffer,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                   | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                   | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        };

        await tar.WriteEntryAsync(binary, cancellationToken).ConfigureAwait(false);

        await WriteTextEntryAsync(tar, ReadmeName, Readme(request), cancellationToken).ConfigureAwait(false);

        if (AgentPlatforms.IsLinux(request.Platform))
        {
            await WriteTextEntryAsync(tar, SystemdUnitName, Unit(request), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteTextEntryAsync(
        TarWriter tar,
        string name,
        string text,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = stream,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        };

        await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Что прочтёт человек на том конце.
    /// </summary>
    /// <remarks>
    /// Две команды и ничего лишнего. Первая — для площадки, где ничего ставить
    /// не дадут и входящие закрыты: агент дозванивается сам. Вторая — для постоянной
    /// точки. Отпечаток клиента напечатан затем, чтобы сверка была возможна:
    /// без неё сопряжение подтверждает, что соединение установлено, а не что оно
    /// установлено с тем, с кем нужно.
    /// </remarks>
    private static string Readme(AgentKitRequest request)
    {
        var binary = AgentPlatforms.ExecutableName(request.Platform);
        var run = AgentPlatforms.IsWindows(request.Platform) ? binary : "./" + binary;
        var address = request.ClientAddresses.Count > 0 ? request.ClientAddresses[0] : "адрес-клиента";

        var text = new StringBuilder();

        text.AppendLine("Storm Machine — агент измерений");
        text.AppendLine("================================");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Платформа: {AgentPlatforms.Title(request.Platform)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Выдан машиной: {request.ClientName}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Собран: {DateTimeOffset.Now:dd.MM.yyyy HH:mm}");
        text.AppendLine();
        text.AppendLine("Агент ничего не устанавливает и не требует прав администратора.");
        text.AppendLine("Он хранит свою личность рядом с собой — в той папке, куда вы его распакуете.");
        text.AppendLine();
        text.AppendLine("ЧТО СДЕЛАТЬ");
        text.AppendLine("-----------");
        text.AppendLine("1. На машине со Storm Machine откройте «Настройки → Агенты»");
        text.AppendLine("   и нажмите «Ждать звонка». Клиент покажет код из шести знаков.");
        text.AppendLine("   Код живёт 10 минут — берите его в момент сопряжения, а не заранее.");
        text.AppendLine();
        text.AppendLine("2. Здесь выполните:");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"   {run} connect {address} --порт {request.Port} --код КОД");
        text.AppendLine();

        if (request.ClientAddresses.Count > 1)
        {
            text.AppendLine("   Если этот адрес недоступен, попробуйте другие адреса клиента:");

            foreach (var other in request.ClientAddresses.Skip(1))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"     {other}");
            }

            text.AppendLine();
        }

        text.AppendLine("3. Сверьте отпечаток, который покажет агент, с отпечатком клиента:");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"   {request.ClientThumbprint}");
        text.AppendLine();
        text.AppendLine("   Не совпал — не продолжайте: соединение установлено не с той машиной.");
        text.AppendLine();
        text.AppendLine("ЕСЛИ НУЖНА ПОСТОЯННАЯ ТОЧКА");
        text.AppendLine("----------------------------");

        if (AgentPlatforms.IsLinux(request.Platform))
        {
            text.AppendLine("Сопрягитесь один раз (шаги выше), затем:");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"   sudo {run} service install");
            text.AppendLine();
            text.AppendLine("Это запишет юнит systemd и запустит его. Рядом лежит готовый");
            text.AppendLine("storm-agent.service — если ставить будете руками.");
            text.AppendLine("Без sudo юнит ставится пользовательским и живёт, пока открыт сеанс.");
        }
        else if (AgentPlatforms.IsWindows(request.Platform))
        {
            text.AppendLine("Сопрягитесь один раз (шаги выше), затем от администратора:");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"   {run} service install");
            text.AppendLine();
            text.AppendLine("Это единственное место, где агенту нужны права администратора.");
        }
        else
        {
            text.AppendLine("На macOS постоянная точка не заводится: держите агента запущенным");
            text.AppendLine(CultureInfo.InvariantCulture, $"   {run} connect {address} --порт {request.Port}");
            text.AppendLine("или запускайте его на время измерений.");
        }

        text.AppendLine();
        text.AppendLine("ЧТО АГЕНТ ДЕЛАЕТ");
        text.AppendLine("----------------");
        text.AppendLine("Отвечает на измерения той машины, с которой сопряжён: пропускная");
        text.AppendLine("способность, качество канала, задержка под нагрузкой. Сам ничего");
        text.AppendLine("не измеряет и никуда не отправляет. Соединение шифруется, собеседник");
        text.AppendLine("проверяется по отпечатку — чужой не подключится.");

        return text.ToString();
    }

    private static string Unit(AgentKitRequest request)
    {
        var binary = AgentPlatforms.ExecutableName(request.Platform);

        return $"""
            [Unit]
            Description=Storm Machine — агент измерений
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=simple
            # Путь поправьте на тот, куда распакуете агента.
            ExecStart=/opt/storm-agent/{binary} listen
            WorkingDirectory=/opt/storm-agent
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=multi-user.target

            """;
    }
}
