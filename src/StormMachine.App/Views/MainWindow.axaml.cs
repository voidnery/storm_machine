using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;

namespace StormMachine.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Палитра, открывшаяся без фокуса в поле, бесполезна: сочетание клавиш
        // экономит одно движение мышью только затем, чтобы потребовать другое.
        PaletteOverlay.PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty && args.NewValue is true)
            {
                PaletteBox.Focus();
                PaletteBox.SelectAll();
            }
        };

        // Щелчок мимо палитры закрывает её. Раньше выйти можно было только клавишей
        // Escape: окно, которое закрывается единственным способом, и тот с клавиатуры,
        // ловит мышь в тупик (замечание оператора).
        PaletteOverlay.AddHandler(PointerPressedEvent, ClosePaletteOnClickOutside, RoutingStrategies.Tunnel);
    }

    private void ClosePaletteOnClickOutside(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        // Щелчок внутри самой карточки — обычная работа с полем и списком.
        if (e.Source is Visual clicked && clicked.GetSelfAndVisualAncestors().Contains(PaletteCard))
        {
            return;
        }

        shell.Palette.Close();
    }

    /// <summary>
    /// Подставляет окно службе выбора файлов.
    /// </summary>
    /// <remarks>
    /// Диалоги Avalonia требуют окно, а контейнер собирается до его появления.
    /// Окно подставляет себя само — так модели представления о нём по-прежнему не знают.
    /// </remarks>
    public void AttachFilePicker(FilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        picker.Attach(this);
    }
}
