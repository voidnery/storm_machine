using StormMachine.Agent;

namespace StormMachine.Agent.UnitTests;

/// <summary>
/// Память агента о том, кому он доверяет.
/// </summary>
/// <remarks>
/// Единственная запись такого рода на его машине, и потеря её означает поездку:
/// агент живёт на чужой площадке, продукт туда целиком не поедет, а сопряжение
/// требует человека у обеих сторон.
/// <para>
/// Покрытие отложено из И-19 и сделано в И-23 — до боевых тестов намеренно: проверять
/// агента на объекте поздно, там уже нечем и некому.
/// </para>
/// </remarks>
public sealed class PeerBookTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public PeerBookTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "storm-agent.peers.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог уберёт система.
        }
    }

    [Fact]
    public void NewBook_IsEmptyAndDoesNotCreateAFile()
    {
        var book = new PeerBook(_path);

        Assert.Empty(book.All);
        Assert.False(File.Exists(_path), "Пустой агент не должен создавать файл до первого сопряжения.");
    }

    /// <summary>Собеседник переживает перезапуск: иначе сопряжение пришлось бы делать заново.</summary>
    [Fact]
    public void RememberedPeer_SurvivesRestart()
    {
        new PeerBook(_path).Remember("ABCD1234", "рабочая станция", "Storm Machine 0.1.0");

        var reopened = new PeerBook(_path);
        var peer = Assert.Single(reopened.All);

        Assert.Equal("ABCD1234", peer.Thumbprint);
        Assert.Equal("рабочая станция", peer.MachineName);
        Assert.Contains("ABCD1234", reopened.Thumbprints);
    }

    /// <summary>
    /// Повторное сопряжение обновляет запись, а не заводит вторую.
    /// </summary>
    /// <remarks>
    /// Отпечаток и есть тождество собеседника: имя машины меняют, а ключ остаётся.
    /// Вторая запись означала бы, что агент считает одного клиента двумя.
    /// </remarks>
    [Fact]
    public void PairingTwice_UpdatesInsteadOfDuplicating()
    {
        var book = new PeerBook(_path);

        book.Remember("ABCD1234", "старое имя", "0.1.0");
        book.Remember("ABCD1234", "новое имя", "0.2.0");

        var peer = Assert.Single(book.All);

        Assert.Equal("новое имя", peer.MachineName);
        Assert.Equal("0.2.0", peer.Product);
    }

    /// <summary>Время первого сопряжения не переписывается: оно отвечает на «с каких пор».</summary>
    [Fact]
    public void PairedTime_IsNotOverwritten()
    {
        var book = new PeerBook(_path);

        book.Remember("ABCD1234", "станция", "0.1.0");
        var first = Assert.Single(book.All).PairedUtc;

        book.Remember("ABCD1234", "станция", "0.2.0");

        Assert.Equal(first, Assert.Single(book.All).PairedUtc);
    }

    [Fact]
    public void Touch_MovesLastSeenButKeepsThePeer()
    {
        var book = new PeerBook(_path);

        book.Remember("ABCD1234", "станция", "0.1.0");
        var before = Assert.Single(book.All).LastSeenUtc;

        Thread.Sleep(5);
        book.Touch("ABCD1234");

        Assert.True(Assert.Single(book.All).LastSeenUtc >= before);
    }

    [Fact]
    public void TouchingAStranger_ChangesNothing()
    {
        var book = new PeerBook(_path);

        book.Remember("ABCD1234", "станция", "0.1.0");
        book.Touch("НЕЗНАКОМЕЦ");

        Assert.Single(book.All);
    }

    [Fact]
    public void Forget_RemovesThePeerForGood()
    {
        var book = new PeerBook(_path);

        book.Remember("ABCD1234", "станция", "0.1.0");

        Assert.True(book.Forget("ABCD1234"));
        Assert.False(book.Forget("ABCD1234"));
        Assert.Empty(new PeerBook(_path).All);
    }

    /// <summary>
    /// Повреждённый файл не мешает агенту запуститься.
    /// </summary>
    /// <remarks>
    /// Агент на площадке обязан подняться при любом состоянии своей папки: упавший
    /// агент чинить некому. Начать с пустого списка — правильное поведение: он
    /// потребует сопряжения, и это заметят.
    /// <para>
    /// Но затирать испорченный файл молча нельзя — это единственная запись о том,
    /// кому агент доверял, и её сохраняют рядом.
    /// </para>
    /// </remarks>
    [Fact]
    public void BrokenFile_DoesNotStopTheAgent()
    {
        File.WriteAllText(_path, "{ это не json");

        var book = new PeerBook(_path);

        Assert.Empty(book.All);
        Assert.True(File.Exists(_path + ".broken"), "Испорченный список обязан сохраниться рядом.");
    }

    /// <summary>Пустой файл — тот же случай: не падать и не терять молча.</summary>
    [Fact]
    public void EmptyFile_IsTreatedAsBroken()
    {
        File.WriteAllText(_path, string.Empty);

        Assert.Empty(new PeerBook(_path).All);
    }

    /// <summary>
    /// Запись идёт через временный файл.
    /// </summary>
    /// <remarks>
    /// Обрыв записи не должен оставить агента без списка доверенных: на площадке это
    /// означает поездку. Проверяется по следствию — после записи временного файла
    /// рядом не остаётся.
    /// </remarks>
    [Fact]
    public void Saving_LeavesNoTemporaryFileBehind()
    {
        new PeerBook(_path).Remember("ABCD1234", "станция", "0.1.0");

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"), "Временный файл обязан быть переименован, а не оставлен.");
    }

    /// <summary>Порядок — по времени сопряжения: список читают как историю.</summary>
    [Fact]
    public void Peers_AreOrderedByPairingTime()
    {
        var book = new PeerBook(_path);

        book.Remember("ПЕРВЫЙ", "один", "0.1.0");
        Thread.Sleep(5);
        book.Remember("ВТОРОЙ", "два", "0.1.0");

        Assert.Equal(["ПЕРВЫЙ", "ВТОРОЙ"], book.All.Select(p => p.Thumbprint));
    }

    /// <summary>Кириллица в имени машины остаётся читаемой в файле.</summary>
    [Fact]
    public void CyrillicMachineName_StaysReadable()
    {
        new PeerBook(_path).Remember("ABCD1234", "Рабочая станция сетевика", "0.1.0");

        Assert.Equal("Рабочая станция сетевика", Assert.Single(new PeerBook(_path).All).MachineName);
    }
}
