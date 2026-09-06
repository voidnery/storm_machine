using StormMachine.Application.Abstractions;
using StormMachine.Application.Adapters;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Выбор адаптера: он действует, и продукт не молчит, когда он расходится с маршрутом.
/// </summary>
/// <remarks>
/// Замечание оператора (06.09.2026): на машине с Hyper-V маршрут по умолчанию идёт
/// через виртуальный коммутатор, и продукт умел только предупреждать об этом.
/// Здесь закрепляется, что выбор влияет на ответ «откуда меряем» для всех,
/// кто его спрашивает, и что <b>честность выбора</b> проверяется отдельно:
/// исчезнувший адаптер и расхождение с маршрутом обязаны быть названы.
/// </remarks>
public sealed class AdapterChoiceTests
{
    private static readonly NetworkAdapter Physical = new()
    {
        Id = "eth",
        Name = "Ethernet",
        Description = "физическая карта",
        Kind = AdapterKind.Physical,
        IPv4Address = "192.168.1.10",
        PrefixLength = 24,
        IsUp = true,
    };

    private static readonly NetworkAdapter Virtual = new()
    {
        Id = "vswitch",
        Name = "vEthernet",
        Description = "виртуальный коммутатор",
        Kind = AdapterKind.Virtual,
        IPv4Address = "192.168.200.110",
        PrefixLength = 24,
        IsUp = true,
    };

    [Fact(DisplayName = "Без выбора меряем от маршрутного адаптера — как раньше")]
    public async Task WithoutChoice_TheRoutedAdapterIsUsed()
    {
        var environment = await BuildAsync(chosen: null);

        Assert.Equal(Virtual.Id, environment.GetPrimaryAdapter()?.Id);
        Assert.False(environment.IsChosenByOperator);
        Assert.Null(environment.Notice);
    }

    [Fact(DisplayName = "Выбранный адаптер перекрывает маршрутный")]
    public async Task Choice_OverridesTheRoutedAdapter()
    {
        var environment = await BuildAsync(chosen: Physical.Id);

        Assert.Equal(Physical.Id, environment.GetPrimaryAdapter()?.Id);
        Assert.True(environment.IsChosenByOperator);

        // Маршрут по умолчанию остаётся фактом системы: подменять его выбором нельзя.
        Assert.Equal(Virtual.Id, environment.Routed?.Id);
    }

    [Fact(DisplayName = "Расхождение выбора с маршрутом названо вслух")]
    public async Task ChoiceAwayFromTheRoute_IsSaidOutLoud()
    {
        var environment = await BuildAsync(chosen: Physical.Id);

        Assert.NotNull(environment.Notice);
        Assert.Contains("vEthernet", environment.Notice, StringComparison.Ordinal);
        Assert.Contains("Ethernet", environment.Notice, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Исчезнувший адаптер не выдаётся за действующий выбор")]
    public async Task ChoiceThatIsGone_FallsBackAndSaysSo()
    {
        // Карту вынули: в списке её больше нет.
        var environment = await BuildAsync(chosen: "нет-такого");

        Assert.Equal(Virtual.Id, environment.GetPrimaryAdapter()?.Id);
        Assert.False(environment.IsChosenByOperator);
        Assert.NotNull(environment.Notice);
        Assert.Contains("недоступен", environment.Notice, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Опущенный адаптер выбором не считается")]
    public async Task ChoiceOfADownAdapter_IsNotHonoured()
    {
        var down = Physical with { IsUp = false };
        var settings = new MemorySettings();
        await settings.SetAsync(AdapterChoice.Key, down.Id);

        var choice = new AdapterChoice(settings);
        await choice.LoadAsync();

        var environment = new ChosenAdapterEnvironment(new FixedEnvironment([down, Virtual], Virtual), choice);

        Assert.Equal(Virtual.Id, environment.GetPrimaryAdapter()?.Id);
        Assert.NotNull(environment.Notice);
    }

    [Fact(DisplayName = "Возврат к автоматическому стирает выбор")]
    public async Task ChoosingNothing_ReturnsToAutomatic()
    {
        var settings = new MemorySettings();
        var choice = new AdapterChoice(settings);

        await choice.ChooseAsync(Physical.Id);
        Assert.False(choice.IsAutomatic);

        await choice.ChooseAsync(null);

        Assert.True(choice.IsAutomatic);
        Assert.Null(await settings.GetAsync(AdapterChoice.Key));
    }

    private static async Task<ChosenAdapterEnvironment> BuildAsync(string? chosen)
    {
        var settings = new MemorySettings();

        if (chosen is not null)
        {
            await settings.SetAsync(AdapterChoice.Key, chosen);
        }

        var choice = new AdapterChoice(settings);
        await choice.LoadAsync();

        return new ChosenAdapterEnvironment(new FixedEnvironment([Physical, Virtual], Virtual), choice);
    }

    /// <summary>Окружение с заданным составом: маршрутный адаптер известен заранее.</summary>
    private sealed class FixedEnvironment(IReadOnlyList<NetworkAdapter> adapters, NetworkAdapter? routed)
        : INetworkEnvironment
    {
        public bool IsElevated => false;

        public IReadOnlyList<NetworkAdapter> GetAdapters() => adapters;

        public NetworkAdapter? GetPrimaryAdapter() => routed;
    }

    /// <summary>Настройки в памяти: проверке не нужна база, ей нужна пара «ключ — значение».</summary>
    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(
            string key,
            string? value,
            bool secret = false,
            CancellationToken cancellationToken = default)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.Remove(key));

        public Task<IReadOnlyList<SettingEntry>> ListAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SettingEntry>>([]);
    }
}
