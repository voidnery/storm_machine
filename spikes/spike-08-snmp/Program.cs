using System.Globalization;
using System.Net;
using System.Text;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Spike08;

// Спайк-08. Переживает ли SharpSnmpLib обрезку — и что она делает с криптографией v3.
//
// Тот же вопрос, что в спайках 06 и 07, но с двумя новыми поводами для беспокойства.
// Первый: разбор BER — рекурсивная фабрика типов, а такие в .NET часто устроены
// на рефлексии. Второй, более острый: v3 считает HMAC и шифрует AES, а поиск алгоритма
// по имени через CryptoConfig — классическая жертва обрезчика.
//
// Проверять надо на опубликованном бинарнике: отладочный не обрезан и всегда скажет
// «всё хорошо». И обязательно прогонять то же самое без обрезки — иначе «сломала
// обрезка» и «сломал я» неразличимы.
//
// Запуск:
//   spike08                      — проверка: криптография и круг v2c против дублёра
//   spike08 serve [порт]         — поднять коммутатор-дублёр и оставить работать
//   spike08 <узел> <community>   — проверка против настоящего оборудования

internal static class Program
{
    private const int DefaultPort = 16100;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "serve")
        {
            return await Serve(args).ConfigureAwait(false);
        }

        var failures = 0;

        Console.WriteLine("Спайк-08: SharpSnmpLib под обрезкой");
        Console.WriteLine();

        failures += Cryptography();

        if (args.Length >= 2)
        {
            failures += await Against(args[0], args[1]).ConfigureAwait(false);
        }
        else
        {
            failures += await AgainstDouble().ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ИТОГ: проходит." : $"ИТОГ: провалов {failures}.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Поднимает дублёр и оставляет работать — стенд для проверки продукта.</summary>
    private static async Task<int> Serve(string[] args)
    {
        var port = args.Length > 1 && int.TryParse(args[1], out var chosen) ? chosen : DefaultPort;
        var community = args.Length > 2 ? args[2] : "public";

        using var stop = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        await new Device(port, community).RunAsync(stop.Token).ConfigureAwait(false);

        Console.WriteLine("Остановлен.");

        return 0;
    }

    // ------------------------------------------------------------------ v3 и криптография

    /// <summary>
    /// Криптография SNMPv3 — самое вероятное место отказа после обрезки.
    /// </summary>
    /// <remarks>
    /// Вывод ключа из пароля прогоняет пароль через хеш килобайтами, а шифрование
    /// поднимает AES. Если обрезчик выкинул алгоритм, до которого добираются по имени,
    /// падение будет здесь — и только на опубликованной сборке.
    /// </remarks>
    private static int Cryptography()
    {
        Console.WriteLine("1. Криптография SNMPv3");

        var failures = 0;
        var engineId = new OctetString(new byte[] { 0x80, 0x00, 0x1f, 0x88, 0x80, 0x01, 0x02, 0x03, 0x04 });
        var password = Encoding.ASCII.GetBytes("storm-machine-auth");

        foreach (var (name, provider) in Providers())
        {
            try
            {
                var key = provider.PasswordToKey(password, engineId.GetRaw());

                Console.WriteLine(key.Length > 0 && Array.Exists(key, b => b != 0)
                    ? $"   + {name}: ключ выведен, {key.Length.ToString(CultureInfo.InvariantCulture)} байт"
                    : $"   ! {name}: ключ пустой");

                if (key.Length == 0)
                {
                    failures++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ! {name}: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        try
        {
            var auth = new SHA256AuthenticationProvider(new OctetString("storm-machine-auth"));
            var privacy = new AESPrivacyProvider(new OctetString("storm-machine-priv"), auth);

            var parameters = new SecurityParameters(
                engineId,
                new Integer32(1),
                new Integer32(0),
                new OctetString("storm"),
                auth.CleanDigest,
                privacy.Salt);

            var scope = new Scope(new GetRequestPdu(1, [new Variable(new ObjectIdentifier("1.3.6.1.2.1.1.5.0"))]));
            var plain = scope.GetData(VersionCode.V3);
            var encrypted = privacy.Encrypt(plain, parameters);
            var decrypted = privacy.Decrypt(encrypted, parameters);

            var same = decrypted.ToBytes().SequenceEqual(plain.ToBytes());

            Console.WriteLine(same
                ? "   + AES: шифрование и расшифровка сошлись"
                : "   ! AES: расшифрованное не совпало с исходным");

            if (!same)
            {
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! AES: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        return failures;
    }

    private static IEnumerable<(string Name, IAuthenticationProvider Provider)> Providers()
    {
#pragma warning disable CS0618 // Устарели, но встречаются на оборудовании, которое иного не умеет.
        yield return ("MD5", new MD5AuthenticationProvider(new OctetString("storm-machine-auth")));
        yield return ("SHA-1", new SHA1AuthenticationProvider(new OctetString("storm-machine-auth")));
#pragma warning restore CS0618
        yield return ("SHA-256", new SHA256AuthenticationProvider(new OctetString("storm-machine-auth")));
        yield return ("SHA-512", new SHA512AuthenticationProvider(new OctetString("storm-machine-auth")));
    }

    // ------------------------------------------------------------------ круг v2c

    private static async Task<int> AgainstDouble()
    {
        Console.WriteLine();
        Console.WriteLine("2. Полный круг v2c против устройства-дублёра");

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var device = Task.Run(() => new Device(DefaultPort, "public").RunAsync(stop.Token), stop.Token);

        var failures = await Query(new IPEndPoint(IPAddress.Loopback, DefaultPort), "public").ConfigureAwait(false);

        await stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await device.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Дублёр остановлен намеренно.
        }

        return failures;
    }

    private static async Task<int> Against(string host, string community)
    {
        Console.WriteLine();
        Console.WriteLine($"2. Круг v2c против настоящего оборудования: {host}");

        if (!IPAddress.TryParse(host, out var address))
        {
            address = (await Dns.GetHostAddressesAsync(host).ConfigureAwait(false))[0];
        }

        return await Query(new IPEndPoint(address, 161), community).ConfigureAwait(false);
    }

    private static async Task<int> Query(IPEndPoint endpoint, string community)
    {
        var failures = 0;

        try
        {
            var answer = await Messenger.GetAsync(
                VersionCode.V2,
                endpoint,
                new OctetString(community),
                [
                    new Variable(new ObjectIdentifier("1.3.6.1.2.1.1.1.0")),
                    new Variable(new ObjectIdentifier("1.3.6.1.2.1.1.5.0")),
                ]).ConfigureAwait(false);

            foreach (var variable in answer)
            {
                Console.WriteLine($"   + GET {variable.Id} = {variable.Data}");
            }

            if (answer.Count != 2)
            {
                failures++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! GET: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        failures += await Walk(endpoint, community, "1.3.6.1.2.1.2.2.1.2", "порты").ConfigureAwait(false);
        failures += await Walk(endpoint, community, "1.3.6.1.2.1.17.4.3.1.2", "таблица пересылки")
            .ConfigureAwait(false);
        failures += await Walk(endpoint, community, "1.0.8802.1.1.2.1.4.1.1.9", "соседи LLDP").ConfigureAwait(false);

        return failures;
    }

    private static async Task<int> Walk(IPEndPoint endpoint, string community, string root, string what)
    {
        try
        {
            var table = new List<Variable>();

            await Messenger.WalkAsync(
                VersionCode.V2,
                endpoint,
                new OctetString(community),
                new ObjectIdentifier(root),
                table,
                WalkMode.WithinSubtree).ConfigureAwait(false);

            foreach (var variable in table.Take(4))
            {
                Console.WriteLine($"   + {what}: {variable.Id} = {variable.Data}");
            }

            if (table.Count == 0)
            {
                Console.WriteLine($"   ! {what}: пусто");

                return 1;
            }

            if (table.Count > 4)
            {
                Console.WriteLine($"     …всего {table.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   ! {what}: {ex.GetType().Name}: {ex.Message}");

            return 1;
        }
    }
}
