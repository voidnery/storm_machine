using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Application;

namespace StormMachine.App.Services;

/// <summary>Что сейчас известно про новую версию.</summary>
public enum UpdateState
{
    /// <summary>Ещё не спрашивали.</summary>
    Unknown,

    Checking,

    UpToDate,

    Available,

    Failed,
}

/// <summary>
/// Новая версия продукта: узнать — по кнопке, поставить — руками.
/// </summary>
/// <remarks>
/// Продукт поставляется только переносимым архивом (решение штаба D-0005), и ставить
/// себя он не умеет и не должен. Переносимую копию везут с собой в папке, и подменять
/// её на ходу нельзя; к тому же замена версии посреди суточного мониторинга разорвала бы
/// ряд измерений надвое, не сказав об этом. Поэтому служба делает одно: спрашивает
/// страницу выпусков и говорит, есть ли новее. Скачивают и распаковывают поверх руками.
/// <para>
/// Спрашивает только по нажатию. Станцию приносят на чужие площадки, часто без выхода
/// наружу, и обращение к GitHub, которого никто не просил, легло бы в тот самый трафик,
/// который здесь измеряют.
/// </para>
/// <para>
/// До этого здесь жил Velopack с лентой обновлений и установкой по команде. Лента
/// работает только для копии, поставленной установщиком, а установщика больше нет:
/// у переносимой копии та же кнопка навсегда осталась бы серой.
/// </para>
/// </remarks>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>Откуда берутся выпуски. Тот же репозиторий, что в README.</summary>
    public const string ReleasesUrl = "https://github.com/voidnery/storm_machine";

    /// <summary>
    /// Список выпусков, а не «последний»: последним GitHub считает только стабильный,
    /// а пока продукт выходит альфами и бетами, такой запрос не находил бы ничего.
    /// </summary>
    private const string ReleasesApi = "https://api.github.com/repos/voidnery/storm_machine/releases?per_page=30";

    private static readonly HttpClient Http = CreateClient();

    [ObservableProperty]
    private UpdateState _state;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _detail;

    public static string CurrentVersion => ProductInfo.Version;

    public bool CanCheck => State != UpdateState.Checking;

    /// <summary>
    /// Куда идти за архивом: к найденному выпуску, а пока его нет — к списку выпусков.
    /// </summary>
    /// <remarks>
    /// Адрес собирается из разобранного номера, а не берётся из ответа сервера:
    /// открывать в браузере то, что пришло по сети, продукт не станет.
    /// </remarks>
    public string ReleasePage => AvailableVersion is { } version
        ? $"{ReleasesUrl}/releases/tag/v{version}"
        : $"{ReleasesUrl}/releases";

    public string StateText => State switch
    {
        UpdateState.Unknown => "Новая версия ещё не искалась. Проверка спросит страницу выпусков на GitHub.",
        UpdateState.Checking => "Проверяю…",
        UpdateState.UpToDate => $"Новее {CurrentVersion} на GitHub нет.",
        UpdateState.Available => $"Вышла версия {AvailableVersion}. Архив — на странице выпуска; "
                                 + "ставится распаковкой поверх этой папки.",
        _ => Detail is { Length: > 0 } detail ? $"Проверить не удалось: {detail}" : "Проверить не удалось.",
    };

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCheck)
        {
            return;
        }

        Update(UpdateState.Checking);

        try
        {
            using var response = await Http.GetAsync(new Uri(ReleasesApi), cancellationToken).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            AvailableVersion = Newer(Published(document.RootElement), CurrentVersion);
            Update(AvailableVersion is null ? UpdateState.UpToDate : UpdateState.Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                       or TaskCanceledException or InvalidOperationException)
        {
            Detail = ex.Message;
            Update(UpdateState.Failed);
        }
    }

    /// <summary>Открывает страницу выпуска в браузере.</summary>
    public void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasePage) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            // Браузера может не быть — на служебной машине это обычное дело. Адрес
            // на экране остаётся: его копируют и открывают там, где выход наружу есть.
            Detail = $"браузер не открылся ({ex.Message}). Адрес можно скопировать ниже.";
            Update(UpdateState.Failed);
        }
    }

    /// <summary>
    /// Самый новый выпуск старше текущей версии, либо <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Стабильной версии предлагаются только стабильные выпуски, предварительной — любые
    /// новее неё. Кто сидит на альфе, ждёт следующую альфу; кто на стабильной — не ждёт.
    /// </remarks>
    internal static string? Newer(IEnumerable<(string Tag, bool Prerelease)> releases, string current)
    {
        if (!ReleaseVersion.TryParse(current, out var installed))
        {
            return null;
        }

        ReleaseVersion? newest = null;

        foreach (var (tag, prerelease) in releases)
        {
            if (!ReleaseVersion.TryParse(tag, out var candidate)
                || ((prerelease || candidate.IsPrerelease) && !installed.IsPrerelease)
                || candidate <= installed
                || (newest is { } best && candidate <= best))
            {
                continue;
            }

            newest = candidate;
        }

        return newest?.ToString();
    }

    /// <summary>Опубликованные выпуски из ответа GitHub: черновики пропускаются.</summary>
    private static List<(string Tag, bool Prerelease)> Published(JsonElement root)
    {
        var found = new List<(string Tag, bool Prerelease)>();

        if (root.ValueKind != JsonValueKind.Array)
        {
            return found;
        }

        foreach (var release in root.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object
                || (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                || !release.TryGetProperty("tag_name", out var tag)
                || tag.ValueKind != JsonValueKind.String
                || tag.GetString() is not { Length: > 0 } name)
            {
                continue;
            }

            var prerelease = release.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True;
            found.Add((name, prerelease));
        }

        return found;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // Без User-Agent GitHub отвечает 403: таково правило его API.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StormMachine", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    private void Update(UpdateState state)
    {
        State = state;

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(ReleasePage));
    }
}
