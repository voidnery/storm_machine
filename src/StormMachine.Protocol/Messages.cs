using System.Text.Json.Serialization;

namespace StormMachine.Protocol;

/// <summary>Вид сообщения. Значения фиксированы: они уходят в провод.</summary>
public enum MessageKind
{
    /// <summary>Представление: версия протокола, продукт, отпечаток, возможности.</summary>
    Hello = 1,

    /// <summary>Представление принято, собеседник опознан.</summary>
    Welcome = 2,

    /// <summary>Отказ с причиной. Всегда последнее сообщение в обмене.</summary>
    Refused = 3,

    /// <summary>Проверка живости соединения.</summary>
    Ping = 4,

    Pong = 5,

    /// <summary>Начать измерение с указанными параметрами.</summary>
    StartTest = 6,

    /// <summary>Собеседник готов принимать поток на указанном порту.</summary>
    TestReady = 7,

    /// <summary>Ход измерения — промежуточные числа для живого показа.</summary>
    TestProgress = 8,

    /// <summary>Итог измерения со стороны собеседника.</summary>
    TestResult = 9,

    /// <summary>Прервать измерение.</summary>
    Abort = 10,
}

/// <summary>Почему отказано. Значения фиксированы: они уходят в провод.</summary>
public enum RefusalReason
{
    /// <summary>Версии протокола несовместимы.</summary>
    Version = 1,

    /// <summary>Код сопряжения не подошёл.</summary>
    Pairing = 2,

    /// <summary>Отпечаток собеседника не совпал с запомненным.</summary>
    Thumbprint = 3,

    /// <summary>Собеседник неизвестен, а сопряжение не запрашивалось.</summary>
    Unknown = 4,

    /// <summary>Занят другим измерением.</summary>
    Busy = 5,

    /// <summary>Просьба непонятна или не поддерживается.</summary>
    Unsupported = 6,
}

/// <summary>Что умеет сторона. Список открытый: незнакомое имя просто игнорируется.</summary>
public static class Capabilities
{
    public const string TcpThroughput = "tcp-throughput";

    public const string UdpQuality = "udp-quality";

    /// <summary>Точная темповка пакетов доступна: гибридный spin-wait в выделенном потоке.</summary>
    public const string PrecisePacing = "precise-pacing";
}

/// <summary>
/// Одно сообщение протокола.
/// </summary>
/// <remarks>
/// Один тип на все виды вместо иерархии намеренно. Сообщений немного, и различать их
/// по <see cref="Kind"/> дешевле, чем вести полиморфную сериализацию: она требует
/// дискриминатора в JSON, а с обрезкой сборки — ещё и явного перечисления производных
/// типов, которое рано или поздно разойдётся с действительностью.
/// <para>
/// Неиспользуемые поля не пишутся в провод, поэтому широкий тип не делает кадры толще.
/// </para>
/// </remarks>
public sealed record ProtocolMessage
{
    public required MessageKind Kind { get; init; }

    /// <summary>Номер обмена: ответ несёт номер вопроса. Ноль — сообщение само по себе.</summary>
    public int Exchange { get; init; }

    // --- Hello / Welcome ---

    public int ProtocolMajor { get; init; }

    public int ProtocolMinor { get; init; }

    /// <summary>Название и версия продукта — для показа оператору, не для решений.</summary>
    public string? Product { get; init; }

    /// <summary>Имя машины: чтобы в списке агентов было видно, кто есть кто.</summary>
    public string? MachineName { get; init; }

    /// <summary>Отпечаток собственного сертификата — то, что запоминается при сопряжении.</summary>
    public string? Thumbprint { get; init; }

    /// <summary>Что сторона умеет. Незнакомые имена игнорируются.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Доказательство знания кода сопряжения. Пусто — сопряжение не запрашивается.</summary>
    public string? PairingProof { get; init; }

    // --- Refused ---

    public RefusalReason? Reason { get; init; }

    /// <summary>Причина отказа словами оператора, а не кодом.</summary>
    public string? Explanation { get; init; }

    // --- StartTest / TestReady / TestProgress / TestResult ---

    public TestRequest? Request { get; init; }

    /// <summary>Порт, на котором собеседник ждёт поток измерения.</summary>
    public int DataPort { get; init; }

    public TestSnapshot? Snapshot { get; init; }
}

/// <summary>Каким измерением занять собеседника.</summary>
public sealed record TestRequest
{
    /// <summary>Идентификатор измерения: связывает управляющий канал с потоком данных.</summary>
    public required Guid Id { get; init; }

    public required TestKind Kind { get; init; }

    /// <summary>Сколько секунд длится измерение, не считая прогрева.</summary>
    public int DurationSeconds { get; init; } = 10;

    /// <summary>Сколько секунд отбрасывается на разгон (RFC 6349 §5).</summary>
    public int WarmupSeconds { get; init; } = 2;

    /// <summary>Число потоков TCP. Один поток не наполняет канал: окно упирается в RTT.</summary>
    public int Streams { get; init; } = 4;

    /// <summary>Целевая скорость UDP в мегабитах в секунду.</summary>
    public double TargetMbps { get; init; } = 10;

    /// <summary>Размер полезной нагрузки датаграммы.</summary>
    public int PayloadBytes { get; init; } = 172;

    /// <summary>Кто отправляет: сторона, начавшая измерение, или собеседник.</summary>
    public TestDirection Direction { get; init; } = TestDirection.Upload;
}

public enum TestKind
{
    /// <summary>Пропускная способность по TCP: N потоков, прогрев, отбрасывание разгона.</summary>
    TcpThroughput = 1,

    /// <summary>Качество канала по UDP: потери, дрожание, переупорядочивание.</summary>
    UdpQuality = 2,
}

public enum TestDirection
{
    /// <summary>Инициатор отправляет, собеседник принимает.</summary>
    Upload = 1,

    /// <summary>Собеседник отправляет, инициатор принимает.</summary>
    Download = 2,
}

/// <summary>
/// Снимок измерения: то, что видит принимающая сторона.
/// </summary>
/// <remarks>
/// Считает именно принимающая. Отправитель знает, сколько он отдал в сокет, а это
/// не то же самое, сколько дошло: потери и переупорядочивание видны только на приёме.
/// Отсюда и направление сообщений — снимки идут от того, кто принимает.
/// </remarks>
public sealed record TestSnapshot
{
    public required Guid Id { get; init; }

    /// <summary>Секунд от начала измерения, не считая прогрева.</summary>
    public required double ElapsedSeconds { get; init; }

    public required long Bytes { get; init; }

    public required long Packets { get; init; }

    /// <summary>
    /// Мегабит в секунду: у промежуточного снимка — за отрезок с прошлого снимка,
    /// у итогового — среднее за всё измерение.
    /// </summary>
    /// <remarks>
    /// Промежуточный намеренно не средний: канал, просевший на секунду в середине
    /// теста, на графике средних выглядит ровной линией, и просадку увидеть нельзя.
    /// У итога вопрос другой — «сколько всего», — и там среднее и есть ответ.
    /// </remarks>
    public required double Mbps { get; init; }

    /// <summary>Пакетов не пришло: разрыв в нумерации, не закрытый до конца измерения.</summary>
    public long Lost { get; init; }

    /// <summary>Пакетов пришло позже своей очереди.</summary>
    public long OutOfOrder { get; init; }

    /// <summary>Дрожание по RFC 3550 §6.4.1, миллисекунды.</summary>
    public double JitterMs { get; init; }

    /// <summary>Измерение завершено, снимок окончательный.</summary>
    public bool IsFinal { get; init; }

    /// <summary>Что помешало, если помешало.</summary>
    public string? Failure { get; init; }
}

[JsonSerializable(typeof(ProtocolMessage))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
