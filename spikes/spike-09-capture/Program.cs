using System.Globalization;
using System.Text;
using SharpPcap;

// Спайк-09. Захват пакетов: что происходит БЕЗ драйвера и переживает ли библиотека обрезку.
//
// Два вопроса, и второй важнее первого.
//
// 1. Обрезка. Тот же вопрос, что в спайках 06–08. У SharpPcap он острее: библиотека
//    целиком построена на P/Invoke к wpcap.dll, а обрезчик про нативные вызовы
//    рассуждать не умеет.
//
// 2. ОТСУТСТВИЕ ДРАЙВЕРА — главное. Npcap продукт не распространяет ни при каких
//    условиях: лицензия NPSL это запрещает. Значит, у большинства пользователей
//    драйвера не будет, и продукт обязан вести себя достойно: не падать при старте,
//    не падать при загрузке типа, а внятно сказать, чего не хватает и откуда это взять.
//    Где именно библиотека спотыкается — при загрузке сборки, при первом обращении
//    к типу или при перечислении устройств, — определяет, можно ли вообще держать
//    её в основном бинаре или нужен отдельно загружаемый плагин.
//
// Стенд для второго вопроса идеальный: на этой машине Npcap не установлен.
//
// Запуск:
//   spike09          — обе проверки
//   spike09 devices  — только перечисление устройств

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("Спайк-09: захват пакетов без драйвера и под обрезкой");
        Console.WriteLine();

        var failures = 0;

        failures += Loads();
        failures += Devices();

        failures += Version();

        GC.KeepAlive(args);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "ИТОГ: библиотека ведёт себя предсказуемо."
            : $"ИТОГ: непредсказуемого поведения — {failures.ToString(CultureInfo.InvariantCulture)}.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Грузится ли сборка вообще.
    /// </summary>
    /// <remarks>
    /// Если тип не поднимается без драйвера, держать библиотеку в основном бинаре
    /// нельзя: любое обращение к экрану возможностей роняло бы продукт у большинства
    /// пользователей. Тогда нужен отдельно загружаемый плагин.
    /// </remarks>
    private static int Loads()
    {
        Console.WriteLine("1. Загрузка типов без установленного драйвера");

        try
        {
            var type = typeof(CaptureDeviceList);

            Console.WriteLine($"   + тип {type.Name} загружен");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! загрузка типа: {ex.GetType().Name}: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Перечисление устройств — первое настоящее обращение к драйверу.
    /// </summary>
    /// <remarks>
    /// Тут и выясняется, чем именно оборачивается его отсутствие. Продукту нужен
    /// не факт отказа, а <b>тип и текст</b>: по ним он отличит «драйвера нет»
    /// от «драйвер есть, но нет прав» — а это два разных совета оператору.
    /// </remarks>
    private static int Devices()
    {
        Console.WriteLine();
        Console.WriteLine("2. Перечисление устройств");

        try
        {
            var devices = CaptureDeviceList.Instance;

            Console.WriteLine($"   + устройств: {devices.Count.ToString(CultureInfo.InvariantCulture)}");

            foreach (var device in devices)
            {
                Console.WriteLine($"     {device.Name} — {device.Description}");
            }

            if (devices.Count == 0)
            {
                Console.WriteLine("   . список пуст — драйвер не установлен либо не даёт доступа");
            }

            return 0;
        }
        catch (DllNotFoundException ex)
        {
            // Ожидаемый и, по сути, ХОРОШИЙ исход: отказ конкретный и опознаваемый.
            Console.WriteLine($"   . DllNotFoundException — драйвера нет. Текст: {ex.Message}");

            return 0;
        }
        catch (TypeInitializationException ex)
        {
            Console.WriteLine($"   . TypeInitializationException, внутри: "
                              + $"{ex.InnerException?.GetType().Name ?? "неизвестно"} — {ex.InnerException?.Message}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! неожиданный отказ: {ex.GetType().FullName}: {ex.Message}");

            return 1;
        }
    }

    /// <summary>Версия libpcap — второй способ спросить, есть ли драйвер.</summary>
    private static int Version()
    {
        Console.WriteLine();
        Console.WriteLine("3. Версия libpcap");

        try
        {
            Console.WriteLine($"   + {Pcap.SharpPcapVersion} поверх {Pcap.Version}");

            return 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            Console.WriteLine($"   . {ex.GetType().Name} — драйвера нет, и это видно отсюда");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! неожиданный отказ: {ex.GetType().FullName}: {ex.Message}");

            return 1;
        }
    }
}
