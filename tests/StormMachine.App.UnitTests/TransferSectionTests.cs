using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;
using StormMachine.Application.Profiles;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Секция переноса настроек: команды работают, а не только рисуются.
/// </summary>
/// <remarks>
/// Механизм переноса проверен своими тестами и приёмкой; здесь проверяется экранная
/// обвязка — то, что нажатие кнопки доходит до механизма и что итог показан словами,
/// а не остаётся в консоли, которой у графического клиента нет.
/// </remarks>
public sealed class TransferSectionTests
{
    /// <summary>На пустой базе выгрузка честно говорит, что переносить нечего.</summary>
    [Fact]
    public async Task Export_WithNothingToTransfer_SaysSo()
    {
        await using var services = AppServices.Build();

        var section = new TransferSectionViewModel(
            services.GetRequiredService<SettingsTransfer>(),
            new PickerStub(null, null));

        await section.ExportCommand.ExecuteAsync(null);

        Assert.Null(section.Error);
        Assert.NotNull(section.Message);
        Assert.Contains("Переносить нечего", section.Message, StringComparison.Ordinal);
    }

    /// <summary>Загрузка файла проходит до механизма и показывает итог.</summary>
    [Fact]
    public async Task Import_OfExportedFile_ReportsCounts()
    {
        await using var services = AppServices.Build();
        var transfer = services.GetRequiredService<SettingsTransfer>();

        var path = Path.Combine(
            Path.GetTempPath(), "storm-tests", $"перенос-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, SettingsTransfer.Write(await transfer.ExportAsync()));

        try
        {
            var section = new TransferSectionViewModel(transfer, new PickerStub(open: path, save: null));

            await section.ImportCommand.ExecuteAsync(null);

            Assert.Null(section.Error);
            Assert.NotNull(section.Message);
            Assert.StartsWith("Добавлено", section.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Испорченный файл объясняется, а не роняет страницу.</summary>
    [Fact]
    public async Task Import_OfBrokenFile_ExplainsInsteadOfCrashing()
    {
        await using var services = AppServices.Build();

        var path = Path.Combine(
            Path.GetTempPath(), "storm-tests", $"мусор-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "это не JSON");

        try
        {
            var section = new TransferSectionViewModel(
                services.GetRequiredService<SettingsTransfer>(),
                new PickerStub(open: path, save: null));

            await section.ImportCommand.ExecuteAsync(null);

            Assert.NotNull(section.Error);
            Assert.Contains("не разобран", section.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class PickerStub(string? open, string? save) : IFilePicker
    {
        public Task<string?> PickOpenAsync(string title, string extension) => Task.FromResult(open);

        public Task<string?> PickSaveAsync(string title, string suggestedName, string extension) =>
            Task.FromResult(save);
    }
}
