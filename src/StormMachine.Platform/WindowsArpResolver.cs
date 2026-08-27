using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StormMachine.Application.Abstractions;

namespace StormMachine.Platform;

/// <summary>
/// Разрешение адреса в MAC через IPHLPAPI.
/// </summary>
/// <remarks>
/// Ключевая находка этапа исследования (<c>R-03</c>): <c>SendARP</c> и <c>GetIpNetTable</c>
/// дают MAC-адреса <b>без прав администратора и без драйвера захвата</b>. Именно поэтому
/// инвентарь с распознаванием вендора попал в уровень 0 — те самые 80% ценности,
/// доступные сразу после запуска.
/// <para>
/// <c>SendARP</c> работает только внутри одной широковещательной области: для узла
/// за маршрутизатором он вернёт MAC маршрутизатора или ошибку. Это ограничение самого
/// протокола, а не реализации, и продукт обязан его понимать — иначе весь интернет
/// оказался бы «оборудованием одного вендора».
/// </para>
/// </remarks>
public sealed class WindowsArpResolver : IArpResolver
{
    private const int NoError = 0;

    /// <summary>Длина MAC-адреса Ethernet.</summary>
    private const int MacLength = 6;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable(IntPtr ipNetTable, ref int size, bool order);

    public string? Resolve(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var destination = ToUInt32(address);
        var mac = new byte[MacLength];
        var length = (uint)mac.Length;

        try
        {
            if (SendARP(destination, 0, mac, ref length) != NoError || length < MacLength)
            {
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            // Не Windows или урезанная система: инвентарь остаётся без MAC,
            // но сканирование по-прежнему находит живые узлы.
            return null;
        }

        return Format(mac, MacLength);
    }

    public IReadOnlyDictionary<string, string> ReadTable()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var size = 0;
        var buffer = IntPtr.Zero;

        try
        {
            // Первый вызов сообщает нужный размер через ошибку — обычный для Win32
            // двухшаговый разговор.
            _ = GetIpNetTable(IntPtr.Zero, ref size, false);

            if (size <= 0)
            {
                return table;
            }

            buffer = Marshal.AllocHGlobal(size);

            if (GetIpNetTable(buffer, ref size, false) != NoError)
            {
                return table;
            }

            Read(buffer, table);
        }
        catch (DllNotFoundException)
        {
            return table;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return table;
    }

    private static void Read(IntPtr buffer, Dictionary<string, string> table)
    {
        var count = Marshal.ReadInt32(buffer);
        var rowSize = Marshal.SizeOf<MibIpNetRow>();
        var cursor = buffer + Marshal.SizeOf<int>();

        for (var i = 0; i < count; i++)
        {
            var row = Marshal.PtrToStructure<MibIpNetRow>(cursor);
            cursor += rowSize;

            // Тип 2 — запись помечена недействительной; 4 — статическая, она годится.
            if (row.PhysicalAddressLength < MacLength || row.Type == 2)
            {
                continue;
            }

            var address = new IPAddress(row.Address).ToString();
            table[address] = Format(row.PhysicalAddress, MacLength);
        }
    }

    /// <summary>Каноническое написание MAC: заглавные шестнадцатеричные через дефис.</summary>
    internal static string Format(byte[] bytes, int length)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return string.Join('-', bytes.Take(length).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        // Порядок байтов сетевой: SendARP ждёт адрес в том же виде, в каком он идёт
        // по проводу, а не в порядке хоста.
        return ((uint)bytes[3] << 24) | ((uint)bytes[2] << 16) | ((uint)bytes[1] << 8) | bytes[0];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public uint Index;
        public uint PhysicalAddressLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PhysicalAddress;

        public uint Address;
        public uint Type;
    }
}
