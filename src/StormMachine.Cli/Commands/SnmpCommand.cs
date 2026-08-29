using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Snmp;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Snmp;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Опрос оборудования: <c>storm snmp</c>.
/// </summary>
/// <remarks>
/// Уровень 1. Всё, что здесь есть, — только чтение: <c>SET</c> в продукте нет
/// и не планируется. Инструмент диагностики, умеющий менять конфигурацию
/// оборудования, — это другой инструмент, с другой ценой ошибки.
/// <para>
/// Перебор наборов учётных данных — не подбор: пробуются только заведённые
/// оператором и только против названного им узла. Ни словарей, ни обхода подсети.
/// </para>
/// </remarks>
internal static class SnmpCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("snmp", "Опрос оборудования: порты, соседи, таблица пересылки.");

        command.Subcommands.Add(BuildCredentials(services));
        command.Subcommands.Add(BuildProbe(services));
        command.Subcommands.Add(BuildInterfaces(services));
        command.Subcommands.Add(BuildNeighbors(services));
        command.Subcommands.Add(BuildForwarding(services));
        command.Subcommands.Add(BuildWalk(services));
        command.Subcommands.Add(HistoryCommands.CreatePortHistory(services));

        command.SetAction(async (_, cancellationToken) =>
        {
            var store = services.GetRequiredService<ISnmpCredentialStore>();

            SnmpRenderer.WriteCredentials(await store.ListAsync(cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ учётные данные

    private static Command BuildCredentials(IServiceProvider services)
    {
        var command = new Command("creds", "Наборы учётных данных для опроса.");

        command.Subcommands.Add(BuildCredentialsAdd(services));
        command.Subcommands.Add(BuildCredentialsRemove(services));

        command.SetAction(async (_, cancellationToken) =>
        {
            var store = services.GetRequiredService<ISnmpCredentialStore>();

            SnmpRenderer.WriteCredentials(await store.ListAsync(cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    private static Command BuildCredentialsAdd(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "«свитчи», «ядро», «объект заказчика»." };

        var version = new Option<string>("--версия", "--version")
        {
            Description = "v1, v2c или v3. По умолчанию v2c.",
            DefaultValueFactory = _ => "v2c",
        };

        var community = new Option<string?>("--community", "--сообщество")
        {
            Description = "Строка сообщества для v1 и v2c. Без ключа — спросим отдельно.",
        };

        var user = new Option<string?>("--пользователь", "--user") { Description = "Имя пользователя для v3." };

        var auth = new Option<string>("--проверка", "--auth")
        {
            Description = "Проверка подлинности v3: нет, md5, sha1, sha256, sha384, sha512.",
            DefaultValueFactory = _ => "sha256",
        };

        var privacy = new Option<string>("--шифр", "--privacy")
        {
            Description = "Шифрование v3: нет, des, aes128, aes192, aes256.",
            DefaultValueFactory = _ => "aes128",
        };

        var port = new Option<int>("--порт", "--port")
        {
            Description = "Порт устройства.",
            DefaultValueFactory = _ => SnmpCredential.DefaultPort,
        };

        var timeout = new Option<int>("--ожидание", "--timeout")
        {
            Description = "Сколько ждать ответа, мс.",
            DefaultValueFactory = _ => (int)SnmpCredential.DefaultTimeout.TotalMilliseconds,
        };

        var retries = new Option<int>("--повторы", "--retries")
        {
            Description = "Сколько раз повторить запрос без ответа.",
            DefaultValueFactory = _ => 1,
        };

        var order = new Option<int>("--порядок", "--order")
        {
            Description = "Чем меньше, тем раньше набор пробуется.",
        };

        var command = new Command("add", "Завести или изменить набор.")
        {
            name, version, community, user, auth, privacy, port, timeout, retries, order,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<ISnmpCredentialStore>();

            if (!TryVersion(parse.GetValue(version)!, out var chosen))
            {
                Console.Error.WriteLine("Версия должна быть v1, v2c или v3.");

                return 2;
            }

            var existing = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            string? communityValue = null;
            string? authPassword = null;
            string? privacyPassword = null;
            var authProtocol = SnmpAuthProtocol.None;
            var privacyProtocol = SnmpPrivacyProtocol.None;

            if (chosen == SnmpVersion.V3)
            {
                if (!TryAuth(parse.GetValue(auth)!, out authProtocol))
                {
                    Console.Error.WriteLine("Проверка подлинности: нет, md5, sha1, sha256, sha384 или sha512.");

                    return 2;
                }

                if (!TryPrivacy(parse.GetValue(privacy)!, out privacyProtocol))
                {
                    Console.Error.WriteLine("Шифрование: нет, des, aes128, aes192 или aes256.");

                    return 2;
                }

                if (authProtocol != SnmpAuthProtocol.None)
                {
                    authPassword = Secrets.Read("Пароль проверки подлинности", existing?.AuthPassword);

                    if (authPassword is null)
                    {
                        return 2;
                    }
                }

                if (privacyProtocol != SnmpPrivacyProtocol.None)
                {
                    privacyPassword = Secrets.Read("Пароль шифрования", existing?.PrivacyPassword);

                    if (privacyPassword is null)
                    {
                        return 2;
                    }
                }
            }
            else
            {
                communityValue = parse.GetValue(community) ?? Secrets.Read("Строка сообщества", existing?.Community);

                if (communityValue is null)
                {
                    return 2;
                }
            }

            var credential = new SnmpCredential
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = parse.GetValue(name)!,
                Version = chosen,
                Community = communityValue,
                UserName = parse.GetValue(user) ?? existing?.UserName,
                AuthProtocol = authProtocol,
                AuthPassword = authPassword,
                PrivacyProtocol = privacyProtocol,
                PrivacyPassword = privacyPassword,
                Port = parse.GetValue(port),
                Timeout = TimeSpan.FromMilliseconds(parse.GetValue(timeout)),
                Retries = parse.GetValue(retries),
                Order = parse.GetValue(order),
                CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
            };

            var errors = credential.Validate();

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine(error);
                }

                return 2;
            }

            await store.SaveAsync(credential, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(existing is null
                ? $"Набор «{credential.Name}» заведён: {credential.Describe()}"
                : $"Набор «{credential.Name}» изменён: {credential.Describe()}");

            foreach (var warning in credential.Warnings())
            {
                Console.WriteLine($"  ! {warning}");
            }

            Console.WriteLine();
            Console.WriteLine($"Проверить: storm snmp probe <адрес устройства>");

            return 0;
        });

        return command;
    }

    private static Command BuildCredentialsRemove(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя набора или его начало." };
        var command = new Command("rm", "Удалить набор.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<ISnmpCredentialStore>();
            var credential = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (credential is null)
            {
                Console.Error.WriteLine($"Набор «{parse.GetValue(name)}» не найден.");

                return 1;
            }

            await store.DeleteAsync(credential.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Набор «{credential.Name}» удалён.");

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ опрос

    private static Command BuildProbe(IServiceProvider services)
    {
        var host = new Argument<string>("узел") { Description = "Адрес или имя устройства." };
        var command = new Command("probe", "Найти подходящий набор и показать, что за устройство.") { host };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var service = services.GetRequiredService<SnmpService>();
            var target = parse.GetValue(host)!;

            if (!await service.HasCredentialsAsync(cancellationToken).ConfigureAwait(false))
            {
                Console.Error.WriteLine("Учётных данных SNMP нет. Завести: storm snmp creds add \"свитчи\".");

                return 2;
            }

            var reach = await service.ProbeAsync(target, cancellationToken).ConfigureAwait(false);

            if (reach is null)
            {
                Console.WriteLine($"Устройство {target} не ответило ни одним из заведённых наборов.");
                Console.WriteLine();
                Console.WriteLine("Различить «SNMP выключен» и «учётные данные не те» снаружи нельзя:");
                Console.WriteLine("устройство, отвергающее запрос, по RFC 3414 просто молчит.");

                return 1;
            }

            SnmpRenderer.WriteSystem(target, reach);

            return 0;
        });

        return command;
    }

    private static Command BuildInterfaces(IServiceProvider services)
    {
        var host = new Argument<string>("узел") { Description = "Адрес или имя устройства." };
        var set = CredentialOption();

        var load = new Option<int>("--нагрузка", "--load")
        {
            Description = "Померить загрузку: пауза между снимками счётчиков, секунды.",
        };

        var command = new Command("interfaces", "Порты: состояние, скорость, ошибки, загрузка.")
        {
            host, set, load,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var (service, credential, code) = await Resolve(services, parse, set, host, cancellationToken)
                .ConfigureAwait(false);

            if (credential is null)
            {
                return code;
            }

            var target = parse.GetValue(host)!;
            var device = await service.InspectAsync(target, credential, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<PortLoad> loads = [];

            if (parse.GetValue(load) is > 0 and var seconds)
            {
                var interval = TimeSpan.FromSeconds(seconds);

                if (SnmpService.IntervalWarning(device.Interfaces, interval, credential.HasHighCapacityCounters)
                    is { } warning)
                {
                    Console.WriteLine($"! {warning}");
                }

                Console.WriteLine($"Меряю загрузку: два снимка счётчиков с паузой "
                                  + $"{seconds.ToString(CultureInfo.InvariantCulture)} с…");

                loads = await service.MeasureAsync(target, credential, interval, cancellationToken)
                    .ConfigureAwait(false);
            }

            SnmpRenderer.WriteInterfaces(device, loads);

            return 0;
        });

        return command;
    }

    private static Command BuildNeighbors(IServiceProvider services)
    {
        var host = new Argument<string>("узел") { Description = "Адрес или имя устройства." };
        var set = CredentialOption();
        var command = new Command("neighbors", "Соседи по LLDP и CDP: кто с каким портом соединён.")
        {
            host, set,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var (service, credential, code) = await Resolve(services, parse, set, host, cancellationToken)
                .ConfigureAwait(false);

            if (credential is null)
            {
                return code;
            }

            var device = await service
                .InspectAsync(parse.GetValue(host)!, credential, cancellationToken)
                .ConfigureAwait(false);

            SnmpRenderer.WriteNeighbors(device);

            return 0;
        });

        return command;
    }

    private static Command BuildForwarding(IServiceProvider services)
    {
        var host = new Argument<string>("узел") { Description = "Адрес или имя устройства." };
        var set = CredentialOption();
        var port = new Option<string?>("--порт", "--port") { Description = "Показать только этот порт." };
        var mac = new Option<string?>("--mac") { Description = "Найти, в каком порту этот адрес." };

        var command = new Command("fdb", "Таблица пересылки: какой MAC в каком порту.")
        {
            host, set, port, mac,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var (service, credential, code) = await Resolve(services, parse, set, host, cancellationToken)
                .ConfigureAwait(false);

            if (credential is null)
            {
                return code;
            }

            var device = await service
                .InspectAsync(parse.GetValue(host)!, credential, cancellationToken)
                .ConfigureAwait(false);

            SnmpRenderer.WriteForwarding(device, parse.GetValue(port), parse.GetValue(mac));

            return 0;
        });

        return command;
    }

    private static Command BuildWalk(IServiceProvider services)
    {
        var host = new Argument<string>("узел") { Description = "Адрес или имя устройства." };
        var oid = new Argument<string>("ветка") { Description = "Числовой идентификатор: 1.3.6.1.2.1.1." };
        var set = CredentialOption();

        var limit = new Option<int>("--предел", "--limit")
        {
            Description = "Сколько узлов показать.",
            DefaultValueFactory = _ => 128,
        };

        var command = new Command("walk", "Обойти произвольную ветку — для случаев, которых продукт не знает.")
        {
            host, oid, set, limit,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<ISnmpCredentialStore>();
            var client = services.GetRequiredService<ISnmpClient>();

            var credential = await Choose(services, store, parse.GetValue(set), parse.GetValue(host)!,
                cancellationToken).ConfigureAwait(false);

            if (credential is null)
            {
                Console.Error.WriteLine("Не нашлось подходящего набора учётных данных.");

                return 1;
            }

            var count = parse.GetValue(limit);

            var found = await client
                .WalkAsync(parse.GetValue(host)!, credential, parse.GetValue(oid)!, count, cancellationToken)
                .ConfigureAwait(false);

            SnmpRenderer.WriteWalk(found, count);

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ вспомогательное

    private static Option<string?> CredentialOption() => new("--набор", "--credential")
    {
        Description = "Каким набором опрашивать. Без ключа — подберём сами.",
    };

    private static async Task<(SnmpService Service, SnmpCredential? Credential, int Code)> Resolve(
        IServiceProvider services,
        System.CommandLine.ParseResult parse,
        Option<string?> set,
        Argument<string> host,
        CancellationToken cancellationToken)
    {
        var service = services.GetRequiredService<SnmpService>();
        var store = services.GetRequiredService<ISnmpCredentialStore>();

        var credential = await Choose(
            services,
            store,
            parse.GetValue(set),
            parse.GetValue(host) ?? string.Empty,
            cancellationToken).ConfigureAwait(false);

        if (credential is null)
        {
            Console.Error.WriteLine(
                "Не нашлось подходящего набора учётных данных. Проверить связь: storm snmp probe <узел>.");

            return (service, null, 1);
        }

        return (service, credential, 0);
    }

    /// <summary>Явно названный набор или тот, которым узел действительно отвечает.</summary>
    private static async Task<SnmpCredential?> Choose(
        IServiceProvider services,
        ISnmpCredentialStore store,
        string? named,
        string host,
        CancellationToken cancellationToken)
    {
        if (named is not null)
        {
            return await Find(store, named, cancellationToken).ConfigureAwait(false);
        }

        var reach = await services.GetRequiredService<SnmpService>()
            .ProbeAsync(host, cancellationToken)
            .ConfigureAwait(false);

        return reach?.Credential;
    }

    private static async Task<SnmpCredential?> Find(
        ISnmpCredentialStore store,
        string needle,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.FindAsync(needle, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return null;
        }
    }

    private static bool TryVersion(string text, out SnmpVersion version)
    {
        version = SnmpVersion.V2c;

        return text.ToLowerInvariant() switch
        {
            "v1" or "1" => Set(SnmpVersion.V1, out version),
            "v2c" or "v2" or "2" => Set(SnmpVersion.V2c, out version),
            "v3" or "3" => Set(SnmpVersion.V3, out version),
            _ => false,
        };
    }

    private static bool TryAuth(string text, out SnmpAuthProtocol protocol)
    {
        protocol = SnmpAuthProtocol.None;

        return text.ToLowerInvariant() switch
        {
            "нет" or "no" or "none" => Set(SnmpAuthProtocol.None, out protocol),
            "md5" => Set(SnmpAuthProtocol.Md5, out protocol),
            "sha1" or "sha" => Set(SnmpAuthProtocol.Sha1, out protocol),
            "sha256" => Set(SnmpAuthProtocol.Sha256, out protocol),
            "sha384" => Set(SnmpAuthProtocol.Sha384, out protocol),
            "sha512" => Set(SnmpAuthProtocol.Sha512, out protocol),
            _ => false,
        };
    }

    private static bool TryPrivacy(string text, out SnmpPrivacyProtocol protocol)
    {
        protocol = SnmpPrivacyProtocol.None;

        return text.ToLowerInvariant() switch
        {
            "нет" or "no" or "none" => Set(SnmpPrivacyProtocol.None, out protocol),
            "des" => Set(SnmpPrivacyProtocol.Des, out protocol),
            "aes" or "aes128" => Set(SnmpPrivacyProtocol.Aes128, out protocol),
            "aes192" => Set(SnmpPrivacyProtocol.Aes192, out protocol),
            "aes256" => Set(SnmpPrivacyProtocol.Aes256, out protocol),
            _ => false,
        };
    }

    private static bool Set<T>(T value, out T target)
    {
        target = value;

        return true;
    }
}
