using System.Globalization;
using System.Text;

namespace StormMachine.Cli.UnitTests;

/// <summary>
/// Перехватывает то, что рендерер напечатал.
/// </summary>
/// <remarks>
/// Проверять показ по строкам — единственный способ проверить его вообще: рендерер
/// ничего не возвращает, он пишет в консоль. Утверждения ниже сознательно опираются
/// на числа и слова, а не на разметку: выравнивание столбцов менять можно, а вот
/// число, которое прочтёт оператор, менять нельзя незаметно.
/// </remarks>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly StringWriter _out = new(CultureInfo.InvariantCulture);
    private readonly StringWriter _error = new(CultureInfo.InvariantCulture);

    public ConsoleCapture()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
    }

    public string Text => _out.ToString();

    public string ErrorText => _error.ToString();

    public IReadOnlyList<string> Lines =>
        Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>Первая строка, содержащая указанный кусок текста.</summary>
    public string Line(string contains) =>
        Lines.FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"В выводе нет строки с «{contains}». Вывод был:{Environment.NewLine}{Text}");

    public bool Has(string contains) => Text.Contains(contains, StringComparison.Ordinal);

    public void Dispose()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        _out.Dispose();
        _error.Dispose();
    }

    /// <summary>Собирает вывод действия в строку.</summary>
    public static string Of(Action action)
    {
        using var capture = new ConsoleCapture();
        action();

        return capture.Text;
    }
}
