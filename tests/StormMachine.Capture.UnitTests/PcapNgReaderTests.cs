using StormMachine.Capture.Frames;

namespace StormMachine.Capture.UnitTests;

/// <summary>
/// Чтение pcapng.
/// </summary>
/// <remarks>
/// Встроенный в Windows захват отдаёт услышанное файлом, а не потоком, и между
/// сетью и разбором кадра встаёт ещё один формат. Ошибиться в нём можно тихо:
/// кадр уедет на байт, время — в 1970-й, а продукт покажет это как факт о сети.
/// Поэтому формат проверяется на собранных вручную байтах, как и сами кадры.
/// </remarks>
public sealed class PcapNgReaderTests
{
    /// <summary>Кадр CDP с живой сети — первые байты реального объявления.</summary>
    private static readonly byte[] Cdp = Convert.FromHexString(
        "01000CCCCCCCD401C3420FAC0078AAAA0300000C200001792E5F0001000D53572D322E35472D31");

    private static readonly byte[] Lldp = Convert.FromHexString(
        "0180C200000ED401C3420FAC88CC0207040001C3420FAC");

    [Fact(DisplayName = "Кадры возвращаются целиком и в порядке записи")]
    public void Frames_ComeBackWhole()
    {
        var file = PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_000_000), (Lldp, 2_500_000));

        var frames = PcapNgReader.Read(new MemoryStream(file)).ToList();

        Assert.Equal(2, frames.Count);
        Assert.Equal(Cdp, frames[0].Bytes);
        Assert.Equal(Lldp, frames[1].Bytes);
    }

    [Fact(DisplayName = "Время берётся в том разрешении, которое объявил интерфейс")]
    public void Time_FollowsDeclaredResolution()
    {
        // Одно и то же число долей при разных долях — это разное время. Разрешение
        // по умолчанию микросекундное, и прочитать наносекундный файл как
        // микросекундный значит ошибиться в тысячу раз, ничего не заметив.
        var micro = PcapNgReader
            .Read(new MemoryStream(PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_500_000))))
            .Single();

        var nano = PcapNgReader
            .Read(new MemoryStream(PcapNgFile.With(PcapNgFile.Nanoseconds, (Cdp, 1_500_000))))
            .Single();

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1.5), micro.ObservedUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(1.5), nano.ObservedUtc);
    }

    [Fact(DisplayName = "Оборванный файл отдаёт то, что успело записаться")]
    public void Truncated_KeepsWhatWasRead()
    {
        // Журнал обрывается на полуслове буднично: остановили захват, кончилось
        // место, выключили машину. Услышанное до обрыва — всё равно услышанное.
        var whole = PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_000_000), (Lldp, 2_000_000));
        var cut = whole[..^12];

        var frames = PcapNgReader.Read(new MemoryStream(cut)).ToList();

        Assert.Single(frames);
        Assert.Equal(Cdp, frames[0].Bytes);
    }

    [Fact(DisplayName = "Кадр с невозможной длиной пропускается, а не рушит разбор")]
    public void AbsurdLength_IsSkipped()
    {
        // Длина приходит с диска, а не из нашего кода, и доверять ей нельзя:
        // объявленный гигабайт в кадре — это испорченный файл, а не кадр.
        var file = new List<byte>();
        file.AddRange(PcapNgFile.SectionHeader());
        file.AddRange(PcapNgFile.Interface(PcapNgFile.Microseconds));
        file.AddRange(PcapNgFile.Packet(Cdp, 1_000_000, declared: 1_000_000_000));
        file.AddRange(PcapNgFile.Packet(Lldp, 2_000_000));

        var frames = PcapNgReader.Read(new MemoryStream([.. file])).ToList();

        Assert.Single(frames);
        Assert.Equal(Lldp, frames[0].Bytes);
    }

    /// <summary>
    /// Настоящий файл pktmon — первые триста байт прогона спайка-10.
    /// </summary>
    /// <remarks>
    /// Собранный нами файл проверяет наше понимание формата против него же самого.
    /// Этот — против того, что pktmon пишет на самом деле: заголовок секции с его
    /// опциями, описание интерфейса <b>без</b> опции разрешения (значит, доли
    /// по умолчанию) и один кадр CDP от живого коммутатора.
    /// </remarks>
    private const string FromPktMon =
        "0A0D0D0A780000004D3C2B1A01000000FFFFFFFFFFFFFFFF03000E0057696E646F77732031302050726F0000"
        + "02002A004D6963726F2D5374617220496E7465726E6174696F6E616C20436F2E2C204C74642E204D532D3745"
        + "3236000004000E005061636B6574204D6F6E69746F7200000000000078000000010000001400000001000000"
        + "000000001400000006000000A800000000000000B65D0600F85A5E0F860000008600000001000CCCCCCCD401"
        + "C3420FAC0078AAAA0300000C200001792E5F0001000D53572D322E35472D3100020011000000010101CC0004"
        + "0A10020400030013627269646765312F65746865723133000400080000000100050027372E32342E34202873"
        + "7461626C652920323032362D30392D31362031313A33323A32310006000C4D696B726F54696B0000A8000000";

    [Fact(DisplayName = "Настоящий файл pktmon читается и опознаётся")]
    public void RealFile_IsRead()
    {
        var file = Convert.FromHexString(FromPktMon.Replace(" ", string.Empty, StringComparison.Ordinal));

        var frame = PcapNgReader.Read(new MemoryStream(file)).Single();
        var finding = FrameParser.Parse(frame.Bytes, frame.ObservedUtc, "тестовая карта");

        Assert.Equal(134, frame.Bytes.Length);

        // Время — микросекунды от эпохи, как объявлено по умолчанию. Прогон спайка
        // показал ровно эту отметку: 13 октября 2026 года, 10:13:03.536.
        Assert.Equal(
            new DateTimeOffset(2026, 10, 13, 10, 13, 3, 536, TimeSpan.Zero).AddTicks(8880),
            frame.ObservedUtc);

        Assert.Equal("SW-2.5G-1", finding?.Neighbor?.RemoteName);
        Assert.Equal("bridge1/ether13", finding?.Neighbor?.RemotePort);
    }

    [Fact(DisplayName = "Прочитанные кадры понимает разборщик продукта")]
    public void Frames_AreUnderstoodByTheParser()
    {
        // Главный вопрос спайка-10 в виде проверки: кадр, прошедший через файл,
        // остаётся тем же кадром. Разборщик писался под Npcap и про pcapng
        // не знает ничего — и не должен.
        var file = PcapNgFile.With(PcapNgFile.Microseconds, (Cdp, 1_000_000));

        var frame = PcapNgReader.Read(new MemoryStream(file)).Single();
        var finding = FrameParser.Parse(frame.Bytes, frame.ObservedUtc, "тестовая карта");

        Assert.NotNull(finding);
        Assert.Equal("SW-2.5G-1", finding!.Neighbor?.RemoteName);
    }
}
