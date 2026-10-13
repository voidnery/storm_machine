using System.Net;
using StormMachine.Protocol;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Объявление себя в локальной сети.
/// </summary>
/// <remarks>
/// Проверяется на байтах, а не на живой сети. Причина не в удобстве: сетевая
/// проверка падает на машине, где входящие на 5353 не разрешены, — то есть
/// по чужому брандмауэру, а не по вине продукта. Проверено на этой же машине:
/// объявление ушло, а обратно не вернулось ничего. Проверка, падающая от чужой
/// настройки, — это проверка, которую перестают запускать.
/// <para>
/// Байты же отвечают на настоящий вопрос: собранное нами мы сами и прочтём,
/// и клиента от агента отличим. Сама рассылка — общий код, работающий в продукте
/// с И-12.
/// </para>
/// </remarks>
public sealed class LocalDiscoveryTests
{
    [Fact(DisplayName = "Клиент, который ждёт звонка, читается как клиент")]
    public void WaitingClient_IsReadBack()
    {
        // Ровно то, чего не было до И-26: объявлялся только агент, и обратное
        // направление требовало от человека на чужой площадке набрать адрес.
        var packet = LocalDiscovery.BuildAnnouncement(
            "станция-оператора",
            47820,
            $"{LocalDiscovery.ClientProduct}/0.2.1",
            "ABCDEF0123456789",
            IPAddress.Parse("192.168.200.104"));

        Assert.True(
            LocalDiscovery.TryReadAnnouncement(packet, new IPEndPoint(IPAddress.Parse("192.168.200.104"), 5353), out var peer),
            "Собственное объявление не прочиталось.");

        Assert.Equal("станция-оператора", peer.MachineName);
        Assert.Equal(47820, peer.Port);
        Assert.True(peer.IsClient, "Клиент опознан не как клиент.");
        Assert.False(peer.IsAgent);
    }

    [Fact(DisplayName = "Агент читается как агент")]
    public void Agent_IsReadBack()
    {
        var packet = LocalDiscovery.BuildAnnouncement(
            "точка-на-площадке",
            47820,
            $"{LocalDiscovery.AgentProduct}/0.2.1",
            "0123456789ABCDEF",
            IPAddress.Parse("10.0.0.7"));

        Assert.True(
            LocalDiscovery.TryReadAnnouncement(packet, new IPEndPoint(IPAddress.Parse("10.0.0.7"), 5353), out var peer));

        Assert.True(peer.IsAgent);
        Assert.False(peer.IsClient);
    }

    [Fact(DisplayName = "Обрывок пакета не выдаётся за объявление")]
    public void Garbage_IsNotAnAnnouncement()
    {
        // В эфире 5353 живёт не только наш продукт: там Bonjour, принтеры, телевизоры.
        // Чужой пакет обязан быть отвергнут, а не разобран наполовину.
        Assert.False(LocalDiscovery.TryReadAnnouncement([1, 2, 3], new IPEndPoint(IPAddress.Loopback, 5353), out _));
        Assert.False(LocalDiscovery.TryReadAnnouncement([], new IPEndPoint(IPAddress.Loopback, 5353), out _));
    }

    [Theory(DisplayName = "Агента от клиента отличает то, чем он назвался")]
    [InlineData("storm-agent/1.2.3", true, false)]
    [InlineData("storm/1.2.3", false, true)]
    [InlineData("storm-agent", true, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void Product_TellsAgentFromClient(string? product, bool agent, bool client)
    {
        // Признак строчный и доверять ему нельзя: объявление подделает кто угодно.
        // Кто перед нами на самом деле, подтверждает отпечаток при сопряжении —
        // объявление лишь избавляет от набора адреса.
        var peer = new AnnouncedPeer
        {
            Address = "192.168.1.2",
            Port = 47820,
            MachineName = "кто-то",
            Product = product,
        };

        Assert.Equal(agent, peer.IsAgent);
        Assert.Equal(client, peer.IsClient);
    }
}
