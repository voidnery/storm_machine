using StormMachine.Application.Abstractions;
using StormMachine.Application.Capture;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Прослушивание как видит его продукт.
/// </summary>
/// <remarks>
/// Проверки написаны после боевого прогона на машине с Hyper-V: продукт упал
/// там неперехваченным исключением ещё до первого кадра. Падение пришло не из
/// захвата, а из сопоставления имён адаптеров — и держалось на предположении,
/// которое на такой машине просто неверно.
/// </remarks>
public sealed class CaptureServiceTests
{
    [Fact(DisplayName = "Два адаптера с одним MAC не роняют захват")]
    public void SharedMac_DoesNotCrash()
    {
        // На хосте с Hyper-V виртуальный коммутатор носит адрес физической карты,
        // за которой стоит. Раньше здесь строился словарь по MAC — и второй
        // адаптер с тем же ключом ронял весь разбор. Проверено живьём.
        var service = new CaptureService(
            new StubCapture([Heard("345A60CE68F7", "34-5A-60-CE-68-F7")]),
            new TwoAdapters());

        var adapters = service.Adapters();

        Assert.Single(adapters);
    }

    [Fact(DisplayName = "Имя берётся у физической карты, а не у виртуального коммутатора")]
    public void PhysicalName_Wins()
    {
        // Оператор ищет глазами ту карту, в которую воткнут провод. Имя
        // виртуального коммутатора на её месте — честный, но бесполезный ответ.
        var service = new CaptureService(
            new StubCapture([Heard("345A60CE68F7", "34-5A-60-CE-68-F7")]),
            new TwoAdapters());

        Assert.Equal("Ethernet", service.Adapters()[0].SystemName);
    }

    [Fact(DisplayName = "Адаптер без MAC не теряет имени и не ищет его")]
    public void WithoutMac_KeepsDescription()
    {
        var service = new CaptureService(
            new StubCapture([new CaptureAdapter { Id = "x", Description = "безымянный" }]),
            new TwoAdapters());

        var adapter = service.Adapters()[0];

        Assert.Null(adapter.SystemName);
        Assert.Equal("безымянный", adapter.DisplayName);
    }

    private static CaptureAdapter Heard(string id, string mac) => new()
    {
        Id = id,
        Description = "как это называет захват",
        MacAddress = mac,
    };

    /// <summary>Источник, который знает ровно то, что ему передали.</summary>
    private sealed class StubCapture(IReadOnlyList<CaptureAdapter> adapters) : ICaptureProvider
    {
        public CaptureEngine Engine => CaptureEngine.WindowsBuiltIn;

        public CaptureRefusal Availability => CaptureRefusal.None;

        public string? DriverDescription => "подставной источник";

        public IReadOnlyList<CaptureAdapter> Adapters() => adapters;

        public Task<CaptureResult> ListenAsync(
            CaptureAdapter adapter,
            CaptureOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Машина с Hyper-V: один MAC у двух адаптеров.</summary>
    private sealed class TwoAdapters : INetworkEnvironment
    {
        public IReadOnlyList<NetworkAdapter> GetAdapters() =>
        [
            new()
            {
                Id = "{virt}",
                Name = "vEthernet (DefaultVirtLan)",
                Description = "Hyper-V Virtual Ethernet Adapter",
                Kind = AdapterKind.Virtual,
                MacAddress = "34-5A-60-CE-68-F7",
                IsUp = true,
            },
            new()
            {
                Id = "{phys}",
                Name = "Ethernet",
                Description = "Realtek Gaming 2.5GbE Family Controller #3",
                Kind = AdapterKind.Physical,
                MacAddress = "34:5A:60:CE:68:F7",
                IsUp = true,
            },
        ];

        public NetworkAdapter? GetPrimaryAdapter() => GetAdapters()[1];

        public bool IsElevated => false;
    }
}
