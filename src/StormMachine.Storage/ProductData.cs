using StormMachine.Application.Abstractions;

namespace StormMachine.Storage;

/// <summary>
/// Где продукт держит своё на этой машине.
/// </summary>
/// <remarks>
/// Одно правило на всё: базу измерений, журнал клиента и базу принадлежности адресов.
/// Раньше каталог вычисляли двое — хранилище и база принадлежности, — каждый по-своему,
/// и портативный режим пришлось бы заводить дважды.
/// <para>
/// Порядок старшинства не случаен. <c>STORM_DB</c> сильнее всего: ею открывают копию,
/// присланную в поддержку, и ею же проверки уводят запись из рабочей истории — частный
/// случай обязан перекрывать общее правило. Дальше маркер портативности. Если нет
/// ни того ни другого — профиль пользователя, как было всегда.
/// </para>
/// </remarks>
public static class ProductData
{
    /// <summary>
    /// Файл-маркер рядом с программой: он включает портативный режим.
    /// </summary>
    /// <remarks>
    /// Маркер, а не догадка по пути. Догадка («каталог записываемый — значит переносим»)
    /// ошибается именно там, где ошибку не видно: продукт молча начинает писать не туда,
    /// и человек узнаёт об этом, когда ищет историю измерений и не находит. Маркер кладут
    /// в портативный архив при сборке — это решение, а не совпадение.
    /// </remarks>
    public const string PortableMarker = StorageEnvironment.PortableMarker;

    /// <summary>Каталог данных продукта.</summary>
    public static string Directory => Resolve().Path;

    /// <summary>Данные лежат рядом с программой, а не в профиле пользователя.</summary>
    public static bool IsPortable => Resolve().Portable;

    /// <summary>Путь к базе по умолчанию — файл в каталоге данных.</summary>
    public static string DefaultDatabasePath()
    {
        var chosen = Environment.GetEnvironmentVariable(StorageEnvironment.PathVariable);

        return string.IsNullOrWhiteSpace(chosen)
            ? Path.Combine(Directory, "storm.db")
            : Path.GetFullPath(chosen.Trim());
    }

    /// <summary>
    /// Маркер, который кладёт сам упаковщик портативного архива.
    /// </summary>
    /// <remarks>
    /// Velopack помечает им корень портативного архива — то есть архив объявляет
    /// себя переносимым сам, и продукту оставалось только научиться это слышать.
    /// Имя чужое, поэтому живёт здесь, в инфраструктуре, а не в договоре с оператором:
    /// человеку мы называем свой маркер, упаковщику — его.
    /// </remarks>
    private const string PackagerMarker = ".portable";

    /// <summary>
    /// Разбор правила целиком — за один проход, чтобы два ответа не разошлись.
    /// </summary>
    /// <remarks>
    /// Не кэшируется: маркер можно положить рядом и перезапустить продукт, а проверки
    /// меняют <c>STORM_DB</c> между прогонами в одном процессе. Обращений к правилу
    /// единицы за запуск, и платить за них кэшем, который врёт, незачем.
    /// </remarks>
    private static (string Path, bool Portable) Resolve()
    {
        var chosen = Environment.GetEnvironmentVariable(StorageEnvironment.PathVariable);

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            var file = Path.GetFullPath(chosen.Trim());

            return (Path.GetDirectoryName(file) ?? AppContext.BaseDirectory, Portable: false);
        }

        // Ищем рядом с программой и на уровень выше. Второе — не щедрость, а устройство
        // портативного архива: бинарь в нём лежит в папке «current», маркер — в корне,
        // и данные обязаны лечь к маркеру. Папку «current» заменяют целиком, распаковывая
        // новую версию поверх старой; история измерений, положенная туда, пережила бы
        // ровно одно обновление.
        foreach (var candidate in Candidates())
        {
            if (File.Exists(Path.Combine(candidate, PortableMarker))
                || File.Exists(Path.Combine(candidate, PackagerMarker)))
            {
                return (candidate, Portable: true);
            }
        }

        return (
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StormMachine"),
            Portable: false);
    }

    /// <summary>Где искать маркер: рядом с программой и на уровень выше.</summary>
    private static IEnumerable<string> Candidates()
    {
        if (BesideProgram() is not { Length: > 0 } beside)
        {
            yield break;
        }

        yield return beside;

        if (Path.GetDirectoryName(beside) is { Length: > 0 } above)
        {
            yield return above;
        }
    }

    /// <summary>
    /// Каталог рядом с запущенной программой.
    /// </summary>
    /// <remarks>
    /// Берётся путь процесса, а не <c>AppContext.BaseDirectory</c>: у самодостаточного
    /// одиночного файла базовый каталог — это временная папка распаковки, и маркер
    /// пришлось бы класть туда, куда человек не ходит.
    /// </remarks>
    private static string? BesideProgram() =>
        Environment.ProcessPath is { Length: > 0 } process
            ? Path.GetDirectoryName(process)
            : null;
}
