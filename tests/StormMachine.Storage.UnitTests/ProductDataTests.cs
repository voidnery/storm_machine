using StormMachine.Application.Abstractions;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Где продукт держит своё: одно правило и его старшинство.
/// </summary>
/// <remarks>
/// Правило отвечает на вопрос, который дороже всех остальных на этой странице:
/// куда легли измерения. Ошибка здесь не падает и не светится — продукт просто
/// пишет не туда, и человек узнаёт об этом, когда ищет историю и не находит.
/// <para>
/// Проверки идут по одной: правило читает переменную окружения процесса, и две
/// проверки, меняющие её одновременно, проверяли бы друг друга. Коллекция —
/// дешевле, чем переписывать правило ради тестируемости.
/// </para>
/// </remarks>
[Collection("Каталог данных")]
public sealed class ProductDataTests : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(StorageEnvironment.PathVariable);

    [Fact(DisplayName = "Без маркера и переменной данные лежат в профиле пользователя")]
    public void WithoutMarkerAndVariable_DataLivesInTheUserProfile()
    {
        Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StormMachine");

        // Маркера рядом с тестовым хостом нет — проверка идёт по умолчанию продукта.
        Assert.Equal(expected, ProductData.Directory);
        Assert.False(ProductData.IsPortable);
        Assert.Equal(Path.Combine(expected, "storm.db"), ProductData.DefaultDatabasePath());
    }

    [Fact(DisplayName = "Переменная окружения сильнее всего")]
    public void TheEnvironmentVariable_WinsOverEverything()
    {
        var file = Path.Combine(Path.GetTempPath(), "storm-tests", $"явная-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, file);

        // Частный случай обязан перекрывать общее правило: ею открывают копию
        // из поддержки и уводят запись проверок из рабочей истории.
        Assert.Equal(Path.GetFullPath(file), ProductData.DefaultDatabasePath());
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(file)), ProductData.Directory);

        // И это не портативность: данные лежат там, куда показали, а не рядом с программой.
        Assert.False(ProductData.IsPortable);
    }

    [Fact(DisplayName = "Маркер рядом с программой включает портативный режим")]
    public void TheMarker_TurnsOnPortableMode()
    {
        Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, null);

        var beside = Path.GetDirectoryName(Environment.ProcessPath);

        Assert.NotNull(beside);

        var marker = Path.Combine(beside, StorageEnvironment.PortableMarker);
        var existed = File.Exists(marker);

        try
        {
            if (!existed)
            {
                File.WriteAllText(marker, string.Empty);
            }

            Assert.True(ProductData.IsPortable);
            Assert.Equal(beside, ProductData.Directory);
            Assert.Equal(Path.Combine(beside, "storm.db"), ProductData.DefaultDatabasePath());
        }
        finally
        {
            if (!existed)
            {
                File.Delete(marker);
            }
        }
    }

    [Fact(DisplayName = "Маркер на уровень выше тоже включает режим — так устроен архив")]
    public void TheMarkerOneLevelUp_AlsoCounts()
    {
        Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, null);

        var beside = Path.GetDirectoryName(Environment.ProcessPath)!;
        var above = Path.GetDirectoryName(beside)!;
        var marker = Path.Combine(above, ".portable");
        var existed = File.Exists(marker);

        try
        {
            if (!existed)
            {
                File.WriteAllText(marker, string.Empty);
            }

            // В портативном архиве бинарь лежит в «current», а маркер — в корне.
            // Данные обязаны лечь к маркеру: «current» заменяют целиком при обновлении.
            Assert.True(ProductData.IsPortable);
            Assert.Equal(above, ProductData.Directory);
        }
        finally
        {
            if (!existed)
            {
                File.Delete(marker);
            }
        }
    }

    [Fact(DisplayName = "Маркер слабее переменной: явный путь остаётся явным")]
    public void TheMarker_DoesNotOverrideTheExplicitPath()
    {
        var beside = Path.GetDirectoryName(Environment.ProcessPath)!;
        var marker = Path.Combine(beside, StorageEnvironment.PortableMarker);
        var file = Path.Combine(Path.GetTempPath(), "storm-tests", $"поверх-{Guid.NewGuid():N}.db");
        var existed = File.Exists(marker);

        try
        {
            if (!existed)
            {
                File.WriteAllText(marker, string.Empty);
            }

            Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, file);

            Assert.Equal(Path.GetFullPath(file), ProductData.DefaultDatabasePath());
            Assert.False(ProductData.IsPortable);
        }
        finally
        {
            if (!existed)
            {
                File.Delete(marker);
            }
        }
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(StorageEnvironment.PathVariable, _previous);
}

/// <summary>Проверки каталога данных идут по одной: они делят переменную окружения процесса.</summary>
[CollectionDefinition("Каталог данных", DisableParallelization = true)]
public sealed class DataDirectoryTestGroup;
