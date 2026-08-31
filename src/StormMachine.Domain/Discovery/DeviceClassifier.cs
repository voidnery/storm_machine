namespace StormMachine.Domain.Discovery;

/// <summary>
/// Догадка о роли устройства по косвенным признакам.
/// </summary>
/// <remarks>
/// Появился в И-24 по требованию оператора: найденные устройства должны раскладываться
/// по категориям — маршрутизаторы, коммутаторы, серверы, принтеры — а не лежать одним
/// списком. Три свойства, без которых классификатор был бы вреден:
/// <list type="number">
/// <item><b>Догадка не выдаётся за наблюдение.</b> Результат попадает в устройство
/// только когда нет ни правки оператора, ни настоящего свидетельства (SSDP, скан,
/// SNMP), и помечается как догадка — показ добавляет к тегу знак вопроса.</item>
/// <item><b>Правила консервативны.</b> Вендор TP-Link делает и маршрутизаторы,
/// и камеры, и лампочки — по такому вендору роль не угадывается. Лучше пустая
/// категория, чем уверенно неверная: карта, где догадка выглядит фактом,
/// хуже отсутствия карты.</item>
/// <item><b>Порядок правил фиксирован.</b> Одни и те же признаки всегда дают один
/// и тот же тег — иначе пересканирование меняло бы категории без изменений в сети.</item>
/// </list>
/// </remarks>
public static class DeviceClassifier
{
    /// <summary>Роли, предлагаемые в выпадающих списках правки. Своя строка тоже годится.</summary>
    public static IReadOnlyList<string> KnownRoles { get; } =
    [
        "маршрутизатор",
        "коммутатор",
        "точка доступа",
        "сервер",
        "хранилище",
        "принтер",
        "камера",
        "компьютер",
        "телефон",
        "медиа",
        "шлюз",
    ];

    /// <summary>Тег по косвенным признакам; <c>null</c> — уверенной догадки нет.</summary>
    public static string? Guess(
        string? macAddress,
        string? vendor,
        string? hostName,
        IReadOnlyList<int> openPorts)
    {
        ArgumentNullException.ThrowIfNull(openPorts);

        // Виртуальный адрес протокола резервирования — самый надёжный признак:
        // такой MAC не бывает ни у чего, кроме резервируемого маршрутизатора.
        if (MacAddresses.DescribeVirtual(macAddress) is not null)
        {
            return "маршрутизатор";
        }

        // Порты печати выдают принтер вернее вендора: HP делает и серверы.
        if (openPorts.Contains(9100) || openPorts.Contains(631))
        {
            return "принтер";
        }

        // RTSP — почти всегда камера или видеорегистратор.
        if (openPorts.Contains(554))
        {
            return "камера";
        }

        if (HasAny(vendor, "Synology", "QNAP", "TrueNAS", "Drobo")
            || HasAny(hostName, "nas"))
        {
            return "хранилище";
        }

        if (HasAny(vendor, "Hikvision", "Dahua", "Axis Communications")
            || HasAny(hostName, "cam", "ipc"))
        {
            return "камера";
        }

        // Вендоры, делающие только сетевое железо. TP-Link и D-Link сюда
        // не входят намеренно: они делают всё подряд.
        if (HasAny(vendor, "MikroTik", "Ubiquiti", "Juniper", "Aruba", "Ruckus", "Keenetic"))
        {
            return "маршрутизатор";
        }

        if (HasAny(hostName, "printer", "print"))
        {
            return "принтер";
        }

        if (HasAny(hostName, "srv", "server"))
        {
            return "сервер";
        }

        if (HasAny(hostName, "tv", "chromecast", "shield"))
        {
            return "медиа";
        }

        // Удалённый рабочий стол — рабочая станция Windows.
        if (openPorts.Contains(3389))
        {
            return "компьютер";
        }

        // SSH вместе с веб-сервером — что-то, что обслуживают, а не за чем сидят.
        if (openPorts.Contains(22) && (openPorts.Contains(80) || openPorts.Contains(443)))
        {
            return "сервер";
        }

        return null;
    }

    private static bool HasAny(string? text, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
