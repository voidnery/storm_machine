using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StormMachine.App.Views.Controls;

/// <summary>Кнопка «В пресет» с окошком сохранения. Модель — <see cref="ViewModels.PresetSaveViewModel"/>.</summary>
public partial class PresetSave : UserControl
{
    public PresetSave() => AvaloniaXamlLoader.Load(this);
}
