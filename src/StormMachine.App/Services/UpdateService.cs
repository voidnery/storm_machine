using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Application;
using Velopack;
using Velopack.Sources;

namespace StormMachine.App.Services;

/// <summary>Что сейчас известно про обновление.</summary>
public enum UpdateState
{
    /// <summary>Ещё не спрашивали.</summary>
    Unknown,

    Checking,

    UpToDate,

    Available,

    Downloading,

    /// <summary>Скачано и готово к установке. Установка — по команде оператора.</summary>
    Ready,

    /// <summary>Продукт запущен не из установленной копии: обновлять нечего.</summary>
    NotInstalled,

    Failed,
}

/// <summary>
/// Обновление продукта.
/// </summary>
/// <remarks>
/// Проверка идёт сама, установка — только по команде человека. Причина не в осторожности
/// вообще, а в том, чем продукт занимается: подменить версию посреди суточного мониторинга
/// значит разорвать ряд измерений надвое и не сказать об этом. Условия измерения хранятся
/// вместе с каждым прогоном именно затем, чтобы такие разрывы были видны, — устраивать их
/// самому было бы прямым противоречием.
/// <para>
/// То же правило, что у профилей окружения: узнать можно молча, поменять — нет.
/// </para>
/// <para>
/// Работоспособность под обрезкой проверена спайком-07: лента, поиск, сверка контрольной
/// суммы и разностный пакет работают на опубликованном бинарнике. Проверять это было
/// обязательно — Quartz на том же вопросе провалился (спайк-06).
/// </para>
/// </remarks>
public sealed partial class UpdateService : ObservableObject
{
    /// <summary>Откуда берутся выпуски. Тот же репозиторий, что в README.</summary>
    public const string ReleasesUrl = "https://github.com/voidnery/storm_machine";

    private readonly RunnerService _runner;
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService(RunnerService runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

        // Оговорка про идущие измерения зависит от списка операций, а не от состояния
        // обновления: без этой подписки она застывала — измерение закончилось,
        // а «дождитесь окончания» продолжало висеть, и наоборот.
        _runner.ActiveChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Hold));
            OnPropertyChanged(nameof(CanApply));
        };

        try
        {
            _manager = new UpdateManager(new GithubSource(ReleasesUrl, accessToken: null, prerelease: false));

            if (!_manager.IsInstalled)
            {
                // Запуск из каталога сборки или из распакованного архива. Это не ошибка,
                // и делать вид, что обновление возможно, нельзя: кнопка, которая ничего
                // не сделает, хуже её отсутствия.
                _manager = null;
                State = UpdateState.NotInstalled;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _manager = null;
            State = UpdateState.NotInstalled;
            Detail = ex.Message;
        }
    }

    [ObservableProperty]
    private UpdateState _state;

    [ObservableProperty]
    private string? _availableVersion;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private int _percent;

    public static string CurrentVersion => ProductInfo.Version;

    public bool CanCheck => _manager is not null && State is not (UpdateState.Checking or UpdateState.Downloading);

    public bool CanDownload => State == UpdateState.Available;

    /// <summary>
    /// Установка предлагается, только когда она возможна.
    /// </summary>
    /// <remarks>
    /// Раньше кнопка оставалась включённой во время измерений, а сама установка
    /// молча выходила по <see cref="Hold"/>: нажатие не давало ни результата,
    /// ни объяснения. Кнопка обязана быть выключенной там, где действие не сработает.
    /// </remarks>
    public bool CanApply => State == UpdateState.Ready && Hold is null;

    public string StateText => State switch
    {
        UpdateState.Unknown => "Наличие обновлений ещё не проверялось.",
        UpdateState.Checking => "Проверяю…",
        UpdateState.UpToDate => "Установлена последняя версия.",
        UpdateState.Available => $"Доступна версия {AvailableVersion}.",
        UpdateState.Downloading => $"Скачиваю: {Percent}%",
        UpdateState.Ready => $"Версия {AvailableVersion} скачана и готова к установке.",
        UpdateState.NotInstalled =>
            "Продукт запущен не из установленной копии — обновлять нечего. "
            + "Так бывает при работе из каталога сборки или из распакованного архива.",
        _ => Detail ?? "Проверить не удалось.",
    };

    /// <summary>
    /// Почему установка сейчас не предлагается, даже если обновление скачано.
    /// </summary>
    /// <remarks>
    /// Перезапуск посреди прогона обрывает измерение. Прогон, оборванный так,
    /// сохранит измеренное — это обеспечено записью по ходу, — но ряд всё равно
    /// разорвётся, а причина разрыва в журнале не будет видна.
    /// </remarks>
    public string? Hold => _runner.Active.Count > 0
        ? "Идут измерения. Установка перезапустит продукт и оборвёт их — дождитесь окончания."
        : null;

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is not { } manager)
        {
            return;
        }

        Update(UpdateState.Checking);

        try
        {
            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(true);

            if (_pending is null)
            {
                Update(UpdateState.UpToDate);

                return;
            }

            AvailableVersion = _pending.TargetFullRelease.Version.ToString();
            Update(UpdateState.Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException)
        {
            Detail = ex.Message;
            Update(UpdateState.Failed);
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is not { } manager || _pending is not { } update)
        {
            return;
        }

        Update(UpdateState.Downloading);

        try
        {
            await manager.DownloadUpdatesAsync(
                update,
                percent =>
                {
                    Percent = percent;
                    OnPropertyChanged(nameof(StateText));
                },
                cancelToken: cancellationToken).ConfigureAwait(true);

            Update(UpdateState.Ready);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException)
        {
            Detail = ex.Message;
            Update(UpdateState.Failed);
        }
    }

    /// <summary>Ставит скачанное и перезапускает продукт. Только по команде человека.</summary>
    public void Apply()
    {
        if (_manager is not { } manager || _pending is not { } update || Hold is not null)
        {
            return;
        }

        manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    private void Update(UpdateState state)
    {
        State = state;

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(Hold));
    }
}
