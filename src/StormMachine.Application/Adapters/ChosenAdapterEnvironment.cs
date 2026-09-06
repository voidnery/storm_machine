using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.Adapters;

/// <summary>
/// Окружение, знающее о выборе оператора.
/// </summary>
/// <remarks>
/// Обёртка, а не правка платформы: платформа отвечает на вопрос «что у машины есть
/// и куда уходит маршрут по умолчанию» — это факт системы, и подменять его выбором
/// человека нельзя. Выбор — слой выше, и он один на все два десятка мест, которые
/// спрашивают «откуда меряем»: панель, обнаружение, заголовок сценария, отчёт,
/// консоль. Иначе выбор пришлось бы протаскивать в каждое из них по отдельности,
/// и любое забытое место молча меряло бы не оттуда.
///
/// <para><b>Чего этот выбор не делает.</b> Он не перекладывает пакеты на другой
/// адаптер. Куда уйдёт пакет, решает таблица маршрутов операционной системы;
/// ICMP-пробы вдобавок идут через системный <c>Ping</c>, у которого источник
/// не задаётся вовсе. Поэтому выбор меняет то, <i>о чём</i> продукт говорит
/// и <i>что</i> он сканирует, а расхождение с маршрутом продукт обязан назвать
/// вслух — <see cref="Notice" />. Молчаливое расхождение было бы хуже отсутствия
/// выбора: человек считал бы, что меряет от выбранной карты.</para>
/// </remarks>
public sealed class ChosenAdapterEnvironment(INetworkEnvironment inner, AdapterChoice choice) : INetworkEnvironment
{
    private readonly INetworkEnvironment _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly AdapterChoice _choice = choice ?? throw new ArgumentNullException(nameof(choice));

    public IReadOnlyList<NetworkAdapter> GetAdapters() => _inner.GetAdapters();

    public bool IsElevated => _inner.IsElevated;

    /// <summary>Адаптер, от которого продукт меряет: выбранный оператором либо маршрутный.</summary>
    public NetworkAdapter? GetPrimaryAdapter() => Chosen() ?? _inner.GetPrimaryAdapter();

    /// <summary>Адаптер с маршрутом по умолчанию — тот, которым система пользуется сама.</summary>
    public NetworkAdapter? Routed => _inner.GetPrimaryAdapter();

    /// <summary>Выбор сделан оператором и действует.</summary>
    public bool IsChosenByOperator => Chosen() is not null;

    /// <summary>
    /// Что оператор обязан знать о своём выборе прямо сейчас; <c>null</c> — всё сходится.
    /// </summary>
    /// <remarks>
    /// Два случая, и оба — про доверие к числам. Выбранной карты не стало (вынули
    /// кабель, отключили адаптер) — продукт вернулся к маршрутной и обязан сказать
    /// об этом, иначе оператор решит, что меряет с прежней. Карта на месте,
    /// но маршрут по умолчанию идёт через другую — значит, до внешних целей пакеты
    /// пойдут не через выбранную, и «меряем отсюда» относится к своей сети,
    /// а не к интернету.
    /// </remarks>
    public string? Notice
    {
        get
        {
            if (_choice.IsAutomatic)
            {
                return null;
            }

            if (Chosen() is not { } chosen)
            {
                return "Выбранный адаптер сейчас недоступен — измерения идут от адаптера "
                       + $"с маршрутом по умолчанию ({_inner.GetPrimaryAdapter()?.Name ?? "не найден"}).";
            }

            var routed = _inner.GetPrimaryAdapter();

            return routed is null || string.Equals(routed.Id, chosen.Id, StringComparison.Ordinal)
                ? null
                : $"Маршрут по умолчанию идёт через «{routed.Name}»: до целей за пределами "
                  + $"своей сети пакеты пойдут через него, а не через «{chosen.Name}».";
        }
    }

    /// <summary>
    /// Выбранный адаптер, если он существует и поднят.
    /// </summary>
    /// <remarks>
    /// Опущенный адаптер не годится: измерять «от него» нельзя, и делать вид,
    /// что выбор в силе, значит соврать в строке состояния.
    /// </remarks>
    private NetworkAdapter? Chosen()
    {
        if (_choice.ChosenId is not { Length: > 0 } id)
        {
            return null;
        }

        foreach (var adapter in _inner.GetAdapters())
        {
            if (string.Equals(adapter.Id, id, StringComparison.Ordinal) && adapter.IsUp)
            {
                return adapter;
            }
        }

        return null;
    }
}
