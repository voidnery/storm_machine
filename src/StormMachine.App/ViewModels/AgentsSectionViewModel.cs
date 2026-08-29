using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Agents;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка сопряжённых агентов.</summary>
public sealed record AgentRow(RemoteAgent Agent)
{
    public string Name => Agent.DisplayName;

    public string Where => Agent.Direction == AgentDirection.ClientDials
        ? $"{Agent.Address}:{Agent.Port.ToString(CultureInfo.InvariantCulture)}"
        : $"звонит сам на порт {Agent.Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Отпечаток показывается кусками: его сверяют глазами, а не читают целиком.</summary>
    public string Thumbprint => Agent.Thumbprint.Length >= 8
        ? $"{Agent.Thumbprint[..4]} {Agent.Thumbprint[4..8]}…"
        : Agent.Thumbprint;

    public string Product => Agent.Product;
}

/// <summary>Найденный в сети агент, ещё не сопряжённый.</summary>
public sealed record DiscoveredAgentRow(DiscoveredAgent Agent)
{
    public string Name => Agent.MachineName;

    public string Where => $"{Agent.Address}:{Agent.Port.ToString(CultureInfo.InvariantCulture)}";

    public string State => Agent.IsAlreadyPaired ? "уже сопряжён" : "новый";

    public bool CanPair => !Agent.IsAlreadyPaired;
}

/// <summary>
/// Агенты в графическом клиенте.
/// </summary>
/// <remarks>
/// Закрывает долг, который тянулся с И-12 и был назван блокирующим при разборе И-19:
/// три пробы уровня 0 — <c>throughput</c>, <c>channel</c> и <c>bufferbloat</c> — требуют
/// сопряжённого агента, а сопрягать можно было только из консоли. Экран возможностей
/// показывал их с причиной «нужна вторая точка измерения» и инструкцией
/// <c>storm agents pair</c> — консольной командой. Кто поставил графический клиент,
/// попадал в тупик: возможность видна, объяснена и недоступна.
/// <para>
/// Оба способа сопряжения здесь по решению оператора, принятому перед И-12: соединение
/// устанавливает любая сторона. Звонить самим — когда входящие на площадке разрешены;
/// ждать звонка — когда прав там нет. Выбор делается один раз и запоминается вместе
/// с агентом.
/// </para>
/// <para>
/// <b>Отпечаток показывается всегда и сверяется человеком.</b> Объявление о себе в сети
/// подделать может кто угодно, и обнаружение избавляет только от набора адреса —
/// доверие даёт сверка отпечатка, а не то, что агент нашёлся.
/// </para>
/// </remarks>
public sealed partial class AgentsSectionViewModel(IAgentDirectory directory) : ObservableObject, IDisposable
{
    private readonly IAgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    private CancellationTokenSource? _pairing;

    public ObservableCollection<AgentRow> Agents { get; } = [];

    public ObservableCollection<DiscoveredAgentRow> Found { get; } = [];

    [ObservableProperty]
    private AgentRow? _selected;

    [ObservableProperty]
    private DiscoveredAgentRow? _selectedFound;

    /// <summary>Отпечаток этого клиента — его называют тому, кто стоит у агента.</summary>
    [ObservableProperty]
    private string? _ownThumbprint;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    /// <summary>Идёт ли сейчас долгая операция: сопряжение или поиск.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Что происходит прямо сейчас — то же, что консоль пишет строкой.</summary>
    [ObservableProperty]
    private string? _progress;

    public bool HasAgents => Agents.Count > 0;

    public int DefaultPort => _directory.DefaultPort;

    // ------------------------------------------------------------------- список

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var agents = await _directory.ListAsync(cancellationToken).ConfigureAwait(true);

            Agents.Clear();

            foreach (var agent in agents)
            {
                Agents.Add(new AgentRow(agent));
            }

            OwnThumbprint = await _directory.GetOwnThumbprintAsync(cancellationToken).ConfigureAwait(true);
            Error = null;
            OnPropertyChanged(nameof(HasAgents));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = $"Список агентов недоступен: {ex.Message}";
        }
    }

    // --------------------------------------------------------------- сопряжение

    /// <summary>Позвонить агенту самим. Требует разрешённых входящих на его машине.</summary>
    [RelayCommand]
    private async Task DialAsync()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            Error = "Не задан адрес агента.";

            return;
        }

        if (string.IsNullOrWhiteSpace(Code))
        {
            Error = "Нужен код сопряжения — его выдаёт агент командой «storm-agent listen --сопряжение».";

            return;
        }

        await RunAsync(async token =>
        {
            var (host, port) = SplitHost(Host, _directory.DefaultPort);

            var agent = await _directory
                .PairByDialingAsync(host, port, Code.Trim(), token)
                .ConfigureAwait(true);

            Code = string.Empty;
            Message = $"Сопряжён «{agent.DisplayName}». Отпечаток {agent.Thumbprint[..8]} — сверьте его с тем, "
                      + "что показал агент.";
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Подождать, пока агент позвонит сам.
    /// </summary>
    /// <remarks>
    /// Код придумывает реализация и сообщает его до начала ожидания: его надо
    /// продиктовать тому, кто стоит у агента. Ход ожидания идёт в <see cref="Progress"/>,
    /// а не в консоль — у графического клиента её нет, и это ровно тот дефект,
    /// который нашёлся в И-19 у проб агента.
    /// </remarks>
    [RelayCommand]
    private async Task WaitForCallAsync() =>
        await RunAsync(async token =>
        {
            var progress = new Progress<PairingProgress>(p =>
                Dispatcher.UIThread.Post(() => Progress = p.Message));

            var agent = await _directory
                .PairByWaitingAsync(_directory.DefaultPort, progress, token)
                .ConfigureAwait(true);

            Message = $"Сопряжён «{agent.DisplayName}». Отпечаток {agent.Thumbprint[..8]} — сверьте его с тем, "
                      + "что показал агент.";
        }).ConfigureAwait(true);

    /// <summary>Отменяет идущее сопряжение или поиск.</summary>
    [RelayCommand]
    private void Stop() => _pairing?.Cancel();

    // ----------------------------------------------------------------- поиск

    /// <summary>
    /// Слушает, кто объявляет о себе в сети.
    /// </summary>
    /// <remarks>
    /// Избавляет от набора адреса и только от этого: сопряжение всё равно требует кода
    /// и сверки отпечатка. Объявлению доверять нельзя — подделать его может кто угодно.
    /// Работает в пределах одной подсети: агент на удалённой площадке так не найдётся.
    /// </remarks>
    [RelayCommand]
    private async Task BrowseAsync() =>
        await RunAsync(async token =>
        {
            Progress = "Слушаю объявления агентов…";

            var found = await _directory
                .BrowseAsync(TimeSpan.FromSeconds(5), token)
                .ConfigureAwait(true);

            Found.Clear();

            foreach (var agent in found)
            {
                Found.Add(new DiscoveredAgentRow(agent));
            }

            Message = found.Count == 0
                ? "Никто о себе не объявил. Агент на другой подсети так не найдётся — введите адрес руками."
                : $"Найдено: {found.Count}. Сопряжение всё равно требует кода и сверки отпечатка.";
        }).ConfigureAwait(true);

    /// <summary>Подставляет найденный адрес в поле — код всё равно вводит человек.</summary>
    [RelayCommand]
    private void UseFound()
    {
        if (SelectedFound is not { } row)
        {
            return;
        }

        Host = row.Where;
        Message = "Адрес подставлен. Код сопряжения возьмите у того, кто стоит у агента.";
    }

    // ---------------------------------------------------------------- изменения

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        await RunAsync(async token =>
        {
            var agent = await _directory.CheckAsync(row.Agent.Thumbprint, token).ConfigureAwait(true);

            Message = $"«{agent.DisplayName}» отвечает. Версия: {agent.Product}.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ForgetAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        await RunAsync(async token =>
        {
            await _directory.ForgetAsync(row.Agent.Thumbprint, token).ConfigureAwait(true);

            // Забывание — действие клиента, а не агента: у того мы остаёмся
            // в списке собеседников, и сказать об этом надо, иначе оператор
            // решит, что связь разорвана с обеих сторон.
            Message = $"«{row.Name}» забыт. На самом агенте запись о нас осталась — "
                      + "уберите её там, если он больше не наш.";
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Общая обвязка долгих операций.
    /// </summary>
    /// <remarks>
    /// Одна на все: каждая из них может идти минутами, каждую надо уметь отменить,
    /// и после каждой список обязан перечитаться. Три почти одинаковых блока
    /// разошлись бы — в одном забыли бы снять признак занятости, в другом
    /// не обновили бы список.
    /// </remarks>
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        _pairing?.Dispose();
        _pairing = new CancellationTokenSource();

        IsBusy = true;
        Message = null;
        Error = null;
        Progress = null;

        try
        {
            await work(_pairing.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Message = "Отменено.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = null;

            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Освобождает отмену идущей операции.
    /// </summary>
    /// <remarks>
    /// Сопряжение ожиданием держит слушающий сокет и живёт минутами. Уйти с экрана,
    /// не отменив его, значило бы оставить порт занятым — и следующая попытка
    /// сопряжения упёрлась бы в «порт занять не удалось» без внятной причины.
    /// </remarks>
    public void Dispose()
    {
        _pairing?.Cancel();
        _pairing?.Dispose();
        _pairing = null;
    }

    /// <summary>Разбирает «адрес» или «адрес:порт».</summary>
    private static (string Host, int Port) SplitHost(string text, int fallback)
    {
        var trimmed = text.Trim();
        var colon = trimmed.LastIndexOf(':');

        if (colon > 0
            && int.TryParse(trimmed[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is > 0 and <= 65535)
        {
            return (trimmed[..colon], port);
        }

        return (trimmed, fallback);
    }
}
