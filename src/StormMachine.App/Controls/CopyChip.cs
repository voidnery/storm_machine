using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace StormMachine.App.Controls;

/// <summary>
/// Моноширинный чип, копирующий своё содержимое по нажатию: команда консоли или путь.
/// </summary>
/// <remarks>
/// Форма <c>command-chip</c> из дизайн-плана, расширенная на пути: у обоих одна беда —
/// их вклеивают в предложение, а нужны они в буфере обмена. Набирать «storm runs purge»
/// с экрана руками — надёжный способ ошибиться в имени команды и решить, что её нет.
/// <para>
/// Кнопка, а не подпись: нажатие и клавиатура достаются даром, а вместе с ними
/// понятно, что элемент вообще нажимается.
/// </para>
/// </remarks>
public class CopyChip : Button
{
    /// <summary>Что показать и что положить в буфер.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CopyChip, string?>(nameof(Text));

    /// <summary>Подтверждение: держится пару секунд после нажатия.</summary>
    public static readonly StyledProperty<bool> IsCopiedProperty =
        AvaloniaProperty.Register<CopyChip, bool>(nameof(IsCopied));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsCopied
    {
        get => GetValue(IsCopiedProperty);
        private set => SetValue(IsCopiedProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(CopyChip);

    protected override void OnClick()
    {
        base.OnClick();

        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (clipboard is null)
        {
            return;
        }

        // Отказ буфера обмена не превращается в отказ страницы: чужой процесс
        // мог держать буфер открытым, и это не повод рушить показ измерения.
        _ = CopyAsync(clipboard, Text);
    }

    private async Task CopyAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }

        IsCopied = true;

        DispatcherTimer.RunOnce(() => IsCopied = false, TimeSpan.FromSeconds(2));
    }
}
