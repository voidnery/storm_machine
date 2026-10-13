using StormMachine.Capture.PktMon;

namespace StormMachine.Capture.UnitTests;

/// <summary>
/// Разбор таблицы компонентов pktmon.
/// </summary>
/// <remarks>
/// Таблица переводится на язык системы, и проверка на одном языке ничего не стоит.
/// Образец с русской машины снят живьём в спайке-10; английский собран по той же
/// раскладке колонок. Разбор не должен держаться ни за тот, ни за другой.
/// </remarks>
public sealed class PktMonComponentsTests
{
    /// <summary>Что показал pktmon на машине с Hyper-V — дословно.</summary>
    private const string Russian = """

        Сетевые адаптеры:   ИД MAC-адрес         Имя
           -- ---------         ---
            1                   Meta Tunnel
           55 00-15-5D-00-03-09 panel_munarie
           54 00-15-5D-00-03-07 servermonitor_v1
           53 00-15-5D-00-02-C8 vEthernet (Default Switch)
           50 34-5A-60-CE-68-F7 vEthernet (DefaultVirtLan)
           56 34-5A-60-CE-68-F7 Realtek Gaming 2.5GbE Family Controller #3
        """;

    private const string English = """

        Network Adapters:   Id MAC Address       Name
           -- ---------         ---
           50 34-5A-60-CE-68-F7 vEthernet (DefaultVirtLan)
           56 34-5A-60-CE-68-F7 Realtek Gaming 2.5GbE Family Controller #3
        """;

    [Fact(DisplayName = "Строки таблицы читаются: идентификатор, MAC, имя")]
    public void Table_IsRead()
    {
        var components = PktMonComponents.Parse(Russian);

        Assert.Equal(6, components.Count);

        var realtek = components.Single(c => c.Id == 56);

        Assert.Equal("345A60CE68F7", realtek.MacAddress);
        Assert.Equal("Realtek Gaming 2.5GbE Family Controller #3", realtek.Name);
    }

    [Fact(DisplayName = "Компонент без MAC не теряется и не выдумывает его")]
    public void WithoutMac_IsKept()
    {
        var tunnel = PktMonComponents.Parse(Russian).Single(c => c.Id == 1);

        Assert.Null(tunnel.MacAddress);
        Assert.Equal("Meta Tunnel", tunnel.Name);
    }

    [Fact(DisplayName = "Один MAC — несколько компонентов: берутся все")]
    public void SharedMac_GivesEveryComponent()
    {
        // Главная находка спайка-10. На хосте с Hyper-V за одним MAC стоят
        // и физическая карта, и виртуальный коммутатор поверх неё. Слушать надо
        // оба: LLDP идёт на адрес, который виртуальный коммутатор не передаёт
        // дальше, и на нём одном соседи не слышны вовсе.
        var chosen = PktMonComponents.WithMac(PktMonComponents.Parse(Russian), "34:5A:60:CE:68:F7");

        Assert.Equal([50, 56], chosen.Select(c => c.Id).Order());
    }

    [Fact(DisplayName = "Разбор не держится за язык системы")]
    public void Language_DoesNotMatter()
    {
        var russian = PktMonComponents.WithMac(PktMonComponents.Parse(Russian), "34-5A-60-CE-68-F7");
        var english = PktMonComponents.WithMac(PktMonComponents.Parse(English), "34-5A-60-CE-68-F7");

        Assert.Equal(russian.Select(c => c.Id).Order(), english.Select(c => c.Id).Order());
    }

    [Theory(DisplayName = "Пустой и бессмысленный ответ даёт пустой список")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("Не удалось обменяться данными с драйвером PktMon. Отказано в доступе.")]
    public void Nonsense_GivesNothing(string? listing) =>
        Assert.Empty(PktMonComponents.Parse(listing));

    [Fact(DisplayName = "Без MAC искать нечего")]
    public void NoMac_FindsNothing() =>
        Assert.Empty(PktMonComponents.WithMac(PktMonComponents.Parse(Russian), null));
}
