using Avalonia.Controls;
using StormMachine.App.Services;

namespace StormMachine.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
