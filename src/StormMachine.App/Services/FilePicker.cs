using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace StormMachine.App.Services;

/// <summary>Выбор файла для обмена пресетами.</summary>
/// <remarks>
/// Отдельная абстракция нужна потому, что диалоги Avalonia требуют окно, а модели
/// представления о нём не знают и знать не должны. Окно подставляется слоем представления.
/// </remarks>
public interface IFilePicker
{
    Task<string?> PickOpenAsync(string title, string extension);

    Task<string?> PickSaveAsync(string title, string suggestedName, string extension);
}

/// <summary>
/// Реализация поверх диалогов Avalonia.
/// </summary>
/// <remarks>
/// Ссылка на окно хранится изменяемой: контейнер собирается до появления окна,
/// а диалог без окна открыть нельзя. Окно подставляет себя само при загрузке.
/// </remarks>
public sealed class FilePicker : IFilePicker
{
    private TopLevel? _owner;

    public void Attach(TopLevel owner) => _owner = owner;

    public async Task<string?> PickOpenAsync(string title, string extension)
    {
        if (_owner?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [BuildFilter(extension)],
        }).ConfigureAwait(true);

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveAsync(string title, string suggestedName, string extension)
    {
        if (_owner?.StorageProvider is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = [BuildFilter(extension)],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    private static FilePickerFileType BuildFilter(string extension) => new($"Файл {extension.ToUpperInvariant()}")
    {
        Patterns = [$"*.{extension}"],
    };
}
