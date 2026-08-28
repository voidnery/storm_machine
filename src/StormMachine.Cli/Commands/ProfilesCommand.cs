using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Profiles;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Профили сетевого окружения: <c>storm profiles</c>.
/// </summary>
/// <remarks>
/// Смысл профиля не в удобстве переключения списков, а в том, что измерения из разных
/// мест несопоставимы. Порог 50 мс, разумный в офисе, бессмыслен для канала до филиала
/// через VPN. Активный профиль записывается в условия каждого измерения — иначе через
/// полгода отличить замер у заказчика от замера в офисе будет нечем.
/// </remarks>
internal static class ProfilesCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("profiles", "Профили окружения: офис, дом, объект заказчика.");

        command.Subcommands.Add(BuildAdd(services));
        command.Subcommands.Add(BuildUse(services));
        command.Subcommands.Add(BuildDetect(services));
        command.Subcommands.Add(BuildShow(services));
        command.Subcommands.Add(BuildRemove(services));

        command.SetAction(async (_, cancellationToken) =>
        {
            var profiles = services.GetRequiredService<ProfileService>();

            ProfileRenderer.WriteList(
                await profiles.ListAsync(cancellationToken).ConfigureAwait(false),
                profiles.CurrentSignature());

            return 0;
        });

        return command;
    }

    private static Command BuildAdd(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "«офис», «дом», «объект заказчика»." };

        var description = new Option<string?>("--описание", "--description")
        {
            Description = "Пояснение: что это за место.",
        };

        var targets = new Option<string[]>("--цель", "--target")
        {
            Description = "Цель, важная в этом окружении. Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var thresholds = new Option<string[]>("--порог", "--threshold")
        {
            Description = "Порог, уместный здесь: «p95 < 50». Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var monitors = new Option<string[]>("--монитор", "--monitor")
        {
            Description = "Монитор, работающий в этом профиле. Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var here = new Option<bool>("--отсюда", "--here")
        {
            Description = "Запомнить приметы текущей сети: MAC шлюза, его адрес и подсеть.",
        };

        var command = new Command("add", "Завести профиль.")
        {
            name, description, targets, thresholds, monitors, here,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var service = services.GetRequiredService<ProfileService>();
            var monitorStore = services.GetRequiredService<IMonitorStore>();

            List<Threshold> limits;

            try
            {
                limits = [.. (parse.GetValue(thresholds) ?? []).Select(t => Threshold.Parse(t))];
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 2;
            }

            var chosen = new List<Guid>();

            foreach (var needle in parse.GetValue(monitors) ?? [])
            {
                var monitor = await monitorStore.FindAsync(needle, cancellationToken).ConfigureAwait(false);

                if (monitor is null)
                {
                    Console.Error.WriteLine($"Монитор «{needle}» не найден. Список: storm monitors.");

                    return 1;
                }

                chosen.Add(monitor.Id);
            }

            var existing = await service.FindAsync(parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            var profile = new NetworkProfile
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = parse.GetValue(name)!,
                Description = parse.GetValue(description),
                Targets = parse.GetValue(targets) ?? [],
                Thresholds = limits,
                Monitors = chosen,
                Signature = parse.GetValue(here) ? service.CurrentSignature() : existing?.Signature ?? new(),
                IsActive = existing?.IsActive ?? false,
                CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
            };

            var errors = profile.Validate();

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine(error);
                }

                return 2;
            }

            await service.SaveAsync(profile, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(existing is null
                ? $"Профиль «{profile.Name}» заведён."
                : $"Профиль «{profile.Name}» изменён.");

            ProfileRenderer.WriteDetails(profile);

            if (!profile.IsActive)
            {
                Console.WriteLine($"Переключиться: storm profiles use \"{profile.Name}\"");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildUse(IServiceProvider services)
    {
        var name = new Argument<string?>("имя")
        {
            Description = "Имя профиля. Без имени — снять выбор и работать без профиля.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("use", "Переключиться на профиль.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var service = services.GetRequiredService<ProfileService>();
            var needle = parse.GetValue(name);

            if (string.IsNullOrWhiteSpace(needle))
            {
                var changed = await service.ActivateAsync(null, cancellationToken).ConfigureAwait(false);

                Console.WriteLine("Профиль снят: измерения пойдут без пометки о месте.");
                Console.WriteLine(Changed(changed));

                return 0;
            }

            var profile = await Find(service, needle, cancellationToken).ConfigureAwait(false);

            if (profile is null)
            {
                return 1;
            }

            var moved = await service.ActivateAsync(profile.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Активен профиль «{profile.Name}».");
            Console.WriteLine(Changed(moved));

            if (profile.Thresholds.Count > 0)
            {
                Console.WriteLine(
                    "Пороги профиля: "
                    + string.Join(", ", profile.Thresholds.Select(t => t.Describe())));
            }

            Console.WriteLine();
            Console.WriteLine("Имя профиля попадёт в условия каждого следующего измерения.");

            return 0;
        });

        return command;
    }

    private static Command BuildDetect(IServiceProvider services)
    {
        var command = new Command("detect", "Узнать, на какой профиль похожа текущая сеть.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var service = services.GetRequiredService<ProfileService>();
            var signature = service.CurrentSignature();

            Console.WriteLine($"Приметы текущей сети: {signature.Describe()}");

            if (signature.IsEmpty)
            {
                Console.WriteLine("Узнавать не по чему: ни шлюза, ни подсети определить не удалось.");

                return 0;
            }

            var guess = await service.DetectAsync(cancellationToken).ConfigureAwait(false);
            var active = await service.GetActiveAsync(cancellationToken).ConfigureAwait(false);

            if (guess is null)
            {
                Console.WriteLine("Похожего профиля нет.");
                Console.WriteLine("Запомнить это место: storm profiles add \"<имя>\" --отсюда");

                return 0;
            }

            Console.WriteLine($"Похоже на профиль «{guess.Profile.Name}» — {guess.Because}.");

            // Продукт не переключает профиль сам: смена профиля меняет пороги
            // и состав работающих мониторов, а делать это молча значит поменять
            // смысл измерений за спиной оператора.
            if (active?.Id == guess.Profile.Id)
            {
                Console.WriteLine("Он и активен — переключать нечего.");
            }
            else
            {
                Console.WriteLine($"Сейчас активен: {active?.Name ?? "профиль не выбран"}.");
                Console.WriteLine($"Переключиться: storm profiles use \"{guess.Profile.Name}\"");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildShow(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя профиля или его начало." };
        var command = new Command("show", "Показать профиль целиком.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var service = services.GetRequiredService<ProfileService>();
            var profile = await Find(service, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (profile is null)
            {
                return 1;
            }

            ProfileRenderer.WriteDetails(profile);

            return 0;
        });

        return command;
    }

    private static Command BuildRemove(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя профиля или его начало." };
        var command = new Command("rm", "Удалить профиль.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var service = services.GetRequiredService<ProfileService>();
            var profile = await Find(service, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (profile is null)
            {
                return 1;
            }

            await service.DeleteAsync(profile.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Профиль «{profile.Name}» удалён.");
            Console.WriteLine("Мониторы и измерения остались: профиль их не содержал, а только называл.");

            return 0;
        });

        return command;
    }

    private static async Task<NetworkProfile?> Find(
        ProfileService service,
        string needle,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await service.FindAsync(needle, cancellationToken).ConfigureAwait(false);

            if (profile is null)
            {
                Console.Error.WriteLine($"Профиль «{needle}» не найден. Список: storm profiles.");
            }

            return profile;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return null;
        }
    }

    private static string Changed(int count) => count == 0
        ? "Состав работающих мониторов не изменился."
        : $"Мониторов переключено: {count.ToString(CultureInfo.InvariantCulture)}.";
}
