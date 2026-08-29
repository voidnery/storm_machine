using StormMachine.Domain.Monitors;
using StormMachine.Domain.Reports;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Domain.Profiles;

/// <summary>
/// Настройки для переноса между машинами.
/// </summary>
/// <remarks>
/// Один механизм на три однотипных долга — И-14 (расписание не переносится), И-15
/// (эталоны не переносятся) и И-16 (профили не переносятся). Три отдельных выгрузки
/// разошлись бы по формату и по поведению при повторной загрузке, а вопрос у них один:
/// «я настроил у себя, разворачиваю у заказчика».
/// <para>
/// <b>Учётные данные SNMP и пароли каналов сюда не входят и входить не будут.</b>
/// Они зашифрованы ключом учётной записи, и на другой машине не расшифруются
/// в принципе; выгружать их в открытом виде значило бы превратить файл обмена
/// в способ вынести пароли. Это ограничение, а не недоделка, и продукт называет
/// его при выгрузке, а не оставляет выясняться при загрузке.
/// </para>
/// <para>
/// Опознание идёт по идентификатору, а не по имени: повторная загрузка того же файла
/// обновляет настройки на месте, а не заводит их вторыми копиями. Имя для этого
/// не годится — его меняют.
/// </para>
/// </remarks>
public sealed record SettingsBundle
{
    /// <summary>Текущая версия формата обмена.</summary>
    /// <remarks>
    /// Указана явно и по той же причине, что у пресетов: файл, сохранённый сегодня,
    /// должен читаться будущими версиями продукта либо быть отвергнут с внятным
    /// объяснением — но не разобран неверно.
    /// </remarks>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string Product { get; init; } = "Storm Machine";

    /// <summary>Версия продукта, которым выгружено, — для разбора несовместимостей.</summary>
    public string? ProductVersion { get; init; }

    public DateTimeOffset ExportedUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<NetworkProfile> Profiles { get; init; } = [];

    public List<Monitor> Monitors { get; init; } = [];

    /// <summary>
    /// Эталоны.
    /// </summary>
    /// <remarks>
    /// Едут вместе с условиями, в которых сняты, — иначе на другой машине их не с чем
    /// сравнивать: эталон, снятый через физический адаптер, и замер через виртуальный
    /// коммутатор — разные измерения, и сравнение их без оговорки было бы выдумкой.
    /// </remarks>
    public List<Baseline> Baselines { get; init; } = [];

    public bool IsEmpty => Profiles.Count == 0 && Monitors.Count == 0 && Baselines.Count == 0;

    public int Total => Profiles.Count + Monitors.Count + Baselines.Count;

    /// <summary>Что лежит в файле, одной строкой.</summary>
    public string Describe()
    {
        if (IsEmpty)
        {
            return "пусто";
        }

        var parts = new List<string>();

        if (Profiles.Count > 0)
        {
            parts.Add(Text.Plural.With(Profiles.Count, "профиль", "профиля", "профилей"));
        }

        if (Monitors.Count > 0)
        {
            parts.Add(Text.Plural.With(Monitors.Count, "монитор", "монитора", "мониторов"));
        }

        if (Baselines.Count > 0)
        {
            parts.Add(Text.Plural.With(Baselines.Count, "эталон", "эталона", "эталонов"));
        }

        return string.Join(", ", parts);
    }
}

/// <summary>Что произошло при загрузке настроек.</summary>
public sealed record SettingsImportReport
{
    public required int Added { get; init; }

    public required int Updated { get; init; }

    public required int Skipped { get; init; }

    /// <summary>
    /// Что не поехало и почему.
    /// </summary>
    /// <remarks>
    /// Загрузка не отказывается целиком из-за одной непригодной записи: перенос
    /// девяти настроек из десяти полезнее, чем отказ от всех. Но и молчать о десятой
    /// нельзя — оператор обязан узнать, чего у него не появилось.
    /// </remarks>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public int Total => Added + Updated + Skipped;
}
