using System.Text;
using StormMachine.Discovery;

namespace StormMachine.Discovery.UnitTests;

/// <summary>
/// Проверки разбора ответа NetBIOS.
/// </summary>
/// <remarks>
/// Ошибка здесь не падает, а называет устройства неверно: подставив групповое имя
/// вместо собственного, инвентарь назвал бы все машины офиса одинаково — именем
/// рабочей группы. Поэтому проверяется именно выбор имени, а не факт разбора.
/// </remarks>
public sealed class NetbiosNameQueryTests
{
    private const byte IdHigh = 0xAB;
    private const byte IdLow = 0xCD;

    /// <summary>Смещение счётчика имён в ответе.</summary>
    private const int NamesCountOffset = 56;

    /// <summary>Собирает ответ NBSTAT из перечня имён.</summary>
    private static byte[] Response(params (string Name, bool IsGroup)[] names)
    {
        var packet = new List<byte>(new byte[NamesCountOffset + 1]);

        packet[0] = IdHigh;
        packet[1] = IdLow;
        packet[NamesCountOffset] = (byte)names.Length;

        foreach (var (name, isGroup) in names)
        {
            // Запись: 15 знаков имени, дополненных пробелами, байт типа, два байта флагов.
            var padded = name.PadRight(15).AsSpan(0, 15);
            packet.AddRange(Encoding.ASCII.GetBytes(padded.ToString()));
            packet.Add(0x00);
            packet.Add((byte)(isGroup ? 0x80 : 0x04));
            packet.Add(0x00);
        }

        return [.. packet];
    }

    [Fact]
    public void OwnName_IsPreferredOverGroupName()
    {
        // В ответе перечислены все имена узла: собственное, имя рабочей группы,
        // служебные. Групповое описывает не узел, а домен.
        var response = Response(("WORKGROUP", true), ("ONLINEPC2", false));

        Assert.Equal("ONLINEPC2", NetbiosNameQuery.Parse(response, IdHigh, IdLow));
    }

    [Fact]
    public void FirstUniqueName_Wins()
    {
        var response = Response(("NAS2", false), ("NAS2-BACKUP", false));

        Assert.Equal("NAS2", NetbiosNameQuery.Parse(response, IdHigh, IdLow));
    }

    [Fact]
    public void TrailingSpaces_AreTrimmed()
    {
        var response = Response(("PROXY", false));

        Assert.Equal("PROXY", NetbiosNameQuery.Parse(response, IdHigh, IdLow));
    }

    [Fact]
    public void ServiceNames_AreSkipped()
    {
        // Имена вида __MSBROWSE__ принадлежат службе, а не машине.
        var response = Response(("__MSBROWSE__", false), ("DIRECTORPC2", false));

        Assert.Equal("DIRECTORPC2", NetbiosNameQuery.Parse(response, IdHigh, IdLow));
    }

    [Fact]
    public void ForeignIdentifier_IsRejected()
    {
        // Ответ с чужим идентификатором — это ответ не на наш вопрос. Принять его
        // значило бы приписать имя одного узла другому.
        var response = Response(("ONLINEPC2", false));

        Assert.Null(NetbiosNameQuery.Parse(response, 0x11, 0x22));
    }

    [Fact]
    public void OnlyGroupNames_GiveNothing() =>
        Assert.Null(NetbiosNameQuery.Parse(Response(("WORKGROUP", true)), IdHigh, IdLow));

    [Fact]
    public void TruncatedResponse_IsRejected()
    {
        // Обрезанный пакет — повод отказаться, а не читать за пределами буфера.
        var response = Response(("ONLINEPC2", false));

        Assert.Null(NetbiosNameQuery.Parse(response.AsSpan(0, NamesCountOffset + 5), IdHigh, IdLow));
    }

    [Fact]
    public void EmptyResponse_IsRejected() =>
        Assert.Null(NetbiosNameQuery.Parse([], IdHigh, IdLow));

    [Fact]
    public void CountLargerThanPayload_DoesNotOverrun()
    {
        // Заявленное число имён больше, чем в пакете: испорченный или враждебный ответ
        // не должен уводить чтение за границу.
        var response = Response(("ONLINEPC2", false));
        response[NamesCountOffset] = 40;

        Assert.Equal("ONLINEPC2", NetbiosNameQuery.Parse(response, IdHigh, IdLow));
    }
}
