namespace StormMachine.Domain.Capabilities;

/// <summary>
/// Как продукт называет состояние возможности.
/// </summary>
/// <remarks>
/// Словарь один на консоль и окно — по той же причине, что
/// <see cref="StormMachine.Domain.Measurements.Units"/> и
/// <see cref="StormMachine.Domain.Results.VerdictWording"/>. К И-24+ слов было два
/// набора, и пять состояний из восьми назывались по-разному: окно говорило
/// «работает», консоль — «доступно»; окно «нужна вторая точка измерения»,
/// консоль «нужен агент на второй точке». Оператор пользуется обоими и сверяет
/// одно с другим — расхождение слов он читает как расхождение состояний.
/// <para>
/// Слова взяты из объявления самого перечисления: «работает сейчас», «работает,
/// но не в полную силу», «запланировано, но ещё не сделано».
/// </para>
/// </remarks>
public static class CapabilityWording
{
    /// <summary>Состояние словами: «работает», «нужен драйвер захвата».</summary>
    public static string State(CapabilityState state) => state switch
    {
        CapabilityState.Available => "работает",
        CapabilityState.Limited => "работает не в полную силу",
        CapabilityState.NeedsElevation => "нужны права администратора",
        CapabilityState.NeedsCredentials => "нужны учётные данные",
        CapabilityState.NeedsDriver => "нужен драйвер захвата",
        CapabilityState.NeedsData => "нужен файл базы",
        CapabilityState.NeedsAgent => "нужна вторая точка измерения",
        _ => "запланировано, но ещё не сделано",
    };

    /// <summary>
    /// Знак состояния для консоли.
    /// </summary>
    /// <remarks>
    /// Знак живёт рядом со словом: они об одном и том же, и разъехаться им незачем.
    /// </remarks>
    public static string Mark(CapabilityState state) => state switch
    {
        CapabilityState.Available => "+",
        CapabilityState.Limited => "~",
        CapabilityState.Planned => "·",
        _ => "!",
    };
}
