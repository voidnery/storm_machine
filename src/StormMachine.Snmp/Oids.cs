namespace StormMachine.Snmp;

/// <summary>
/// Ветки дерева, которые продукт читает.
/// </summary>
/// <remarks>
/// Числами, а не именами: разбор MIB-файлов требует их наличия на машине, а нужные
/// нам ветки стандартны и за тридцать лет не двигались. Имена оставлены в комментариях —
/// без них через полгода никто не вспомнит, что такое <c>1.3.6.1.2.1.17.4.3.1.2</c>.
/// <para>
/// Живут в инфраструктуре, а не в домене: это подробности протокола, того же сорта,
/// что коды типов ICMP, и домену о них знать незачем.
/// </para>
/// </remarks>
internal static class Oids
{
    // -------------------------------------------------------------- system, RFC 1213

    public const string SysDescr = "1.3.6.1.2.1.1.1.0";
    public const string SysObjectId = "1.3.6.1.2.1.1.2.0";
    public const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    public const string SysContact = "1.3.6.1.2.1.1.4.0";
    public const string SysName = "1.3.6.1.2.1.1.5.0";
    public const string SysLocation = "1.3.6.1.2.1.1.6.0";
    public const string SysServices = "1.3.6.1.2.1.1.7.0";

    // ---------------------------------------------------------- ifTable, RFC 2863

    public const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    public const string IfType = "1.3.6.1.2.1.2.2.1.3";
    public const string IfMtu = "1.3.6.1.2.1.2.2.1.4";

    /// <summary>32-разрядная скорость: упирается в 4.29 Гбит/с.</summary>
    public const string IfSpeed = "1.3.6.1.2.1.2.2.1.5";

    public const string IfPhysAddress = "1.3.6.1.2.1.2.2.1.6";
    public const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";
    public const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";

    /// <summary>32-разрядные счётчики. На гигабите переполняются за 34 секунды.</summary>
    public const string IfInOctets = "1.3.6.1.2.1.2.2.1.10";

    public const string IfInUcastPkts = "1.3.6.1.2.1.2.2.1.11";
    public const string IfInDiscards = "1.3.6.1.2.1.2.2.1.13";
    public const string IfInErrors = "1.3.6.1.2.1.2.2.1.14";
    public const string IfOutOctets = "1.3.6.1.2.1.2.2.1.16";
    public const string IfOutUcastPkts = "1.3.6.1.2.1.2.2.1.17";
    public const string IfOutDiscards = "1.3.6.1.2.1.2.2.1.19";
    public const string IfOutErrors = "1.3.6.1.2.1.2.2.1.20";

    // ------------------------------------------------ ifXTable, RFC 2863 — то же, но шире

    /// <summary>Короткое имя порта: то, что видно в консоли устройства.</summary>
    public const string IfName = "1.3.6.1.2.1.31.1.1.1.1";

    /// <summary>64-разрядные счётчики. Ради них и нужна вторая версия протокола.</summary>
    public const string IfHCInOctets = "1.3.6.1.2.1.31.1.1.1.6";

    public const string IfHCInUcastPkts = "1.3.6.1.2.1.31.1.1.1.7";
    public const string IfHCOutOctets = "1.3.6.1.2.1.31.1.1.1.10";
    public const string IfHCOutUcastPkts = "1.3.6.1.2.1.31.1.1.1.11";

    /// <summary>Скорость в Мбит/с — единственный способ узнать её выше 4.29 Гбит/с.</summary>
    public const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";

    /// <summary>Подпись администратора: куда порт идёт.</summary>
    public const string IfAlias = "1.3.6.1.2.1.31.1.1.1.18";

    // ------------------------------------------------------ BRIDGE-MIB, RFC 4188

    /// <summary>Номер порта моста → <c>ifIndex</c>. Без него номера портов бессмысленны.</summary>
    public const string Dot1dBasePortIfIndex = "1.3.6.1.2.1.17.1.4.1.2";

    /// <summary>Таблица пересылки: MAC → номер порта моста.</summary>
    public const string Dot1dTpFdbPort = "1.3.6.1.2.1.17.4.3.1.2";

    /// <summary>Как запись попала в таблицу: 3 — выучена, 4 — свой адрес, 5 — задана.</summary>
    public const string Dot1dTpFdbStatus = "1.3.6.1.2.1.17.4.3.1.3";

    /// <summary>Q-BRIDGE-MIB, RFC 4363: та же таблица с разбивкой по VLAN.</summary>
    public const string Dot1qTpFdbPort = "1.3.6.1.2.1.17.7.1.2.2.1.2";

    /// <summary>Статус записи Q-BRIDGE — те же коды, что у BRIDGE-MIB.</summary>
    public const string Dot1qTpFdbStatus = "1.3.6.1.2.1.17.7.1.2.2.1.3";

    // --------------------------------------------------------- LLDP-MIB, IEEE 802.1AB

    /// <summary>Идентификатор порта соседа.</summary>
    public const string LldpRemPortId = "1.0.8802.1.1.2.1.4.1.1.7";

    public const string LldpRemPortDesc = "1.0.8802.1.1.2.1.4.1.1.8";
    public const string LldpRemSysName = "1.0.8802.1.1.2.1.4.1.1.9";
    public const string LldpRemSysDesc = "1.0.8802.1.1.2.1.4.1.1.10";
    public const string LldpRemChassisId = "1.0.8802.1.1.2.1.4.1.1.5";

    /// <summary>Локальный порт → его идентификатор. Обычно, но не всегда, <c>ifIndex</c>.</summary>
    public const string LldpLocPortId = "1.0.8802.1.1.2.1.3.7.1.3";

    // ------------------------------------------------------------- CDP-MIB, Cisco

    /// <summary>Имя соседа по CDP. Ветка производителя: у прочих её нет.</summary>
    public const string CdpCacheDeviceId = "1.3.6.1.4.1.9.9.23.1.2.1.1.6";

    public const string CdpCacheDevicePort = "1.3.6.1.4.1.9.9.23.1.2.1.1.7";
    public const string CdpCachePlatform = "1.3.6.1.4.1.9.9.23.1.2.1.1.8";
    public const string CdpCacheAddress = "1.3.6.1.4.1.9.9.23.1.2.1.1.4";
}
