using System.Diagnostics;
using System.Globalization;
using System.Text;

// Спайк-10. Замена Npcap встроенным в Windows pktmon.
//
// Вопросы:
//   1) Пускают ли фильтры pktmon по ethertype (LLDP 0x88CC) и по адресу
//      назначения (CDP 01-00-0C-CC-CC-CC)?
//   2) Что выходит из etl2pcap: полные кадры Ethernet или обрезки?
//   3) Сколько стоит запуск: драйвер грузится при первом start.
//   4) Дублируются ли кадры: pktmon видит пакет на нескольких компонентах.
//
// Запускать с правами администратора: без них pktmon отвечает кодом 5.

internal static class Spike10
{
    private static readonly string Work = Path.Combine(
        Path.GetTempPath(), "storm-spike10-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Разбор готового файла прав не требует: это вторая половина вопроса —
        // понимает ли наш разборщик кадры, пришедшие не от Npcap.
        if (args.Length > 1 && string.Equals(args[0], "разбор", StringComparison.Ordinal))
        {
            return Understand(args[1]);
        }

        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 25;

        Directory.CreateDirectory(Work);
        var etl = Path.Combine(Work, "захват.etl");
        var pcap = Path.Combine(Work, "захват.pcapng");

        Console.WriteLine($"Рабочая папка: {Work}");
        Console.WriteLine();

        var hello = Run("pktmon", "list");

        if (hello.Code != 0)
        {
            Console.WriteLine("pktmon недоступен:");
            Console.WriteLine(hello.Output.Trim());
            Console.WriteLine();
            Console.WriteLine(hello.Code == 5
                ? "Код 5 — нет прав. Запустите спайк от администратора."
                : $"Код {hello.Code}.");

            return 1;
        }

        Console.WriteLine("--- что показывает pktmon list ---");
        Console.WriteLine(hello.Output.TrimEnd());
        Console.WriteLine();

        // Какой компонент слушать. Прошлый прогон брал все сетевые карты сразу
        // и получил 21 полный дубль из 25 кадров: один и тот же пакет pktmon
        // видит на нескольких точках стека. Берём компонент своей карты по MAC.
        var mine = PrimaryMac();
        var component = Component(hello.Output, mine);

        Console.WriteLine($"Своя карта: {mine ?? "не определена"}; компонент pktmon: "
            + (component ?? "не нашёлся, слушаем все карты"));
        Console.WriteLine();

        // ——— 1. Фильтры ———
        var clock = Stopwatch.StartNew();

        Say(Run("pktmon", "filter remove"), "сброс фильтров");
        Say(Run("pktmon", "filter add lldp -d 0x88CC"), "фильтр LLDP по ethertype");
        Say(Run("pktmon", "filter add cdp -m 01-00-0C-CC-CC-CC"), "фильтр CDP по адресу");
        Say(Run("pktmon", "filter add dhcp -t UDP -p 67"), "фильтр DHCP по порту");

        var listed = Run("pktmon", "filter list");
        Console.WriteLine("--- что приняли ---");
        Console.WriteLine(listed.Output.Trim());
        Console.WriteLine();

        // ——— 2. Захват ———
        var which = component is null ? "nics" : component;
        var started = Run("pktmon", $"start -c --comp {which} --pkt-size 0 --file-name \"{etl}\"");
        Say(started, "запуск захвата");
        Console.WriteLine($"Драйвер поднялся за {clock.ElapsedMilliseconds} мс");

        if (started.Code != 0)
        {
            Run("pktmon", "filter remove");

            return 1;
        }

        Console.WriteLine($"Слушаем {seconds} с. LLDP объявляется раз в 30 с — ждём хотя бы один круг.");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        Say(Run("pktmon", "stop"), "остановка");
        Say(Run("pktmon", "filter remove"), "уборка фильтров");

        // ——— 3. Преобразование ———
        var real = Directory.GetFiles(Work, "*.etl").FirstOrDefault() ?? etl;
        Console.WriteLine($"Журнал: {Path.GetFileName(real)}, {new FileInfo(real).Length} Б");

        var converted = Run("pktmon", $"etl2pcap \"{real}\" -o \"{pcap}\"");
        Say(converted, "etl2pcap");

        if (!File.Exists(pcap))
        {
            Console.WriteLine(converted.Output.Trim());

            return 1;
        }

        // ——— 4. Разбор ———
        var frames = PcapNg.Read(pcap).ToList();
        Console.WriteLine();
        Console.WriteLine($"Кадров в pcapng: {frames.Count}");

        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;

        foreach (var frame in frames)
        {
            kinds[Kind(frame.Data)] = kinds.GetValueOrDefault(Kind(frame.Data)) + 1;

            if (!seen.Add(Convert.ToHexString(frame.Data)))
            {
                duplicates++;
            }
        }

        foreach (var pair in kinds.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"  {pair.Key,-28} {pair.Value}");
        }

        Console.WriteLine($"Полных дублей по байтам: {duplicates}");
        Console.WriteLine($"Интерфейсов в файле: {PcapNg.Interfaces(pcap)}");

        foreach (var frame in frames.Take(3))
        {
            Console.WriteLine();
            Console.WriteLine($"— кадр {frame.Data.Length} Б, интерфейс {frame.Interface}, {frame.Utc:HH:mm:ss.fff}");
            Console.WriteLine("  " + Convert.ToHexString(frame.Data.AsSpan(0, Math.Min(48, frame.Data.Length))));
        }

        Console.WriteLine();
        Understand(pcap);

        Console.WriteLine();
        Console.WriteLine("Файлы оставлены: " + Work);

        return 0;
    }

    /// <summary>
    /// Поймёт ли наш разборщик кадры, пришедшие не от Npcap.
    /// </summary>
    /// <remarks>
    /// Разборщик продукта принимает голый массив байтов и про источник не знает
    /// ничего — ради этого он таким и написан. Спайк проверяет, что это правда.
    /// </remarks>
    private static int Understand(string pcap)
    {
        if (!File.Exists(pcap))
        {
            Console.WriteLine($"Файла нет: {pcap}");

            return 1;
        }

        var frames = PcapNg.Read(pcap).ToList();
        var neighbors = new List<string>();
        var servers = new List<string>();
        var unparsed = 0;

        foreach (var frame in frames)
        {
            var finding = StormMachine.Capture.Npcap.FrameParser.Parse(frame.Data, frame.Utc, "спайк");

            if (finding is null)
            {
                unparsed++;

                continue;
            }

            if (finding.Neighbor is { } neighbor)
            {
                var line = $"{neighbor.Protocol}: {neighbor.RemoteName} порт {neighbor.RemotePort}";

                if (!neighbors.Contains(line, StringComparer.Ordinal))
                {
                    neighbors.Add(line);
                }
            }

            if (finding.Dhcp is { } sighting)
            {
                var line = $"{sighting.ServerAddress} предлагает {sighting.OfferedAddress}"
                           + $", шлюз {sighting.OfferedGateway ?? "—"}";

                if (!servers.Contains(line, StringComparer.Ordinal))
                {
                    servers.Add(line);
                }
            }
        }

        Console.WriteLine($"--- разбор нашим FrameParser: {frames.Count} кадров, не опознано {unparsed} ---");

        Console.WriteLine($"Соседей: {neighbors.Count}");

        foreach (var line in neighbors)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine($"Серверов DHCP: {servers.Count}");

        foreach (var line in servers)
        {
            Console.WriteLine("  " + line);
        }

        return 0;
    }

    /// <summary>MAC карты, через которую идёт маршрут по умолчанию.</summary>
    private static string? PrimaryMac()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }

            var properties = nic.GetIPProperties();

            if (properties.GatewayAddresses.Count == 0)
            {
                continue;
            }

            var bytes = nic.GetPhysicalAddress().GetAddressBytes();

            if (bytes.Length == 6)
            {
                return string.Join('-', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            }
        }

        return null;
    }

    /// <summary>
    /// Идентификатор компонента pktmon по MAC.
    /// </summary>
    /// <remarks>
    /// Разбор терпимый к виду таблицы: она переводится на язык системы, и опираться
    /// на заголовки колонок нельзя. Ищем строку, где стоит нужный MAC, и берём
    /// из неё первое число — это идентификатор.
    /// </remarks>
    private static string? Component(string list, string? mac)
    {
        if (mac is null)
        {
            return null;
        }

        foreach (var line in list.Split('\n'))
        {
            if (line.Contains(mac, StringComparison.OrdinalIgnoreCase))
            {
                var first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(t => int.TryParse(t, CultureInfo.InvariantCulture, out _));

                if (first is not null)
                {
                    return first;
                }
            }
        }

        return null;
    }

    /// <summary>Грубое опознание кадра — разбор настоящий живёт в продукте.</summary>
    private static string Kind(byte[] frame)
    {
        if (frame.Length < 14)
        {
            return "обрезок";
        }

        var type = (ushort)((frame[12] << 8) | frame[13]);
        var destination = Convert.ToHexString(frame.AsSpan(0, 6));

        if (type == 0x88CC)
        {
            return "LLDP";
        }

        if (string.Equals(destination, "01000CCCCCCC", StringComparison.Ordinal))
        {
            return "CDP";
        }

        if (type == 0x0800 && frame.Length > 36 && frame[23] == 17)
        {
            var header = (frame[14] & 0x0F) * 4;
            var source = (frame[14 + header] << 8) | frame[15 + header];

            return source == 67 ? "DHCP (ответ сервера)" : $"UDP с порта {source}";
        }

        return $"ethertype 0x{type:X4}";
    }

    private static void Say((int Code, string Output) result, string what) =>
        Console.WriteLine(result.Code == 0
            ? $"[ок]    {what}"
            : $"[код {result.Code}] {what}: {result.Output.Trim().Split('\n')[0]}");

    private static (int Code, string Output) Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        })!;

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }
}

/// <summary>Чтение pcapng: нужны блоки кадров, остальное пропускается.</summary>
internal static class PcapNg
{
    private const uint SectionHeader = 0x0A0D0D0A;
    private const uint InterfaceDescription = 0x00000001;
    private const uint EnhancedPacket = 0x00000006;
    private const uint SimplePacket = 0x00000003;

    internal sealed record Frame(byte[] Data, int Interface, DateTimeOffset Utc);

    internal static int Interfaces(string path) => Blocks(path).Count(b => b.Type == InterfaceDescription);

    internal static IEnumerable<Frame> Read(string path)
    {
        foreach (var block in Blocks(path))
        {
            if (block.Type == EnhancedPacket && block.Body.Length >= 20)
            {
                var ifaceId = BitConverter.ToInt32(block.Body, 0);
                var high = BitConverter.ToUInt32(block.Body, 4);
                var low = BitConverter.ToUInt32(block.Body, 8);
                var captured = BitConverter.ToInt32(block.Body, 12);

                if (captured < 0 || captured > block.Body.Length - 20)
                {
                    continue;
                }

                var data = new byte[captured];
                Array.Copy(block.Body, 20, data, 0, captured);

                // Разрешение по умолчанию — микросекунды; точного знания из IDB
                // спайку не нужно, время здесь только для порядка кадров.
                var micros = ((ulong)high << 32) | low;

                yield return new Frame(
                    data,
                    ifaceId,
                    DateTimeOffset.UnixEpoch.AddTicks((long)micros * 10));
            }
            else if (block.Type == SimplePacket && block.Body.Length >= 4)
            {
                var original = BitConverter.ToInt32(block.Body, 0);
                var length = Math.Min(original, block.Body.Length - 4);

                if (length <= 0)
                {
                    continue;
                }

                var data = new byte[length];
                Array.Copy(block.Body, 4, data, 0, length);

                yield return new Frame(data, 0, DateTimeOffset.UnixEpoch);
            }
        }
    }

    private static IEnumerable<(uint Type, byte[] Body)> Blocks(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        while (stream.Position + 12 <= stream.Length)
        {
            var type = reader.ReadUInt32();
            var total = reader.ReadUInt32();

            if (total < 12 || stream.Position + total - 8 > stream.Length)
            {
                yield break;
            }

            var body = reader.ReadBytes((int)total - 12);
            reader.ReadUInt32();

            if (type != SectionHeader)
            {
                yield return (type, body);
            }
        }
    }
}
