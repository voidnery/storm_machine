using Avalonia;
using Avalonia.Controls.Primitives;

namespace StormMachine.App.Controls;

/// <summary>
/// Карточка «почему так»: тезис виден всегда, объяснение — по требованию.
/// </summary>
/// <remarks>
/// Самый ценный жанр продукта — объяснение принятого решения (MOS считается без потерь,
/// профиль не переключается сам, шаги не складываются). До волны 2 он выглядел так же,
/// как подпись поля: серым десятым кеглем в подвале карточки. Форма разводит два уровня:
/// тезис читается за секунду и стоит первым, обоснование не занимает места, пока его
/// не спросили. Развёрнутый вид не запоминается между показами страницы намеренно:
/// объяснение нужно один раз, а место на экране нужно всегда.
/// </remarks>
public class MethodCard : TemplatedControl
{
    /// <summary>Тезис: одна фраза, которую видно без раскрытия.</summary>
    public static readonly StyledProperty<string?> ThesisProperty =
        AvaloniaProperty.Register<MethodCard, string?>(nameof(Thesis));

    /// <summary>Обоснование. Пусто — карточка остаётся одной строкой без кнопки.</summary>
    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<MethodCard, string?>(nameof(Detail));

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<MethodCard, bool>(nameof(IsOpen));

    public string? Thesis
    {
        get => GetValue(ThesisProperty);
        set => SetValue(ThesisProperty, value);
    }

    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }
}
