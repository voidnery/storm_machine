using Avalonia;
using Avalonia.Controls;
using StormMachine.App.Services;

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
