namespace StormMachine.Cli;

/// <summary>
/// Ввод паролей.
/// </summary>
/// <remarks>
/// Пароль, набранный ключом командной строки, остаётся в истории оболочки, в списке
/// процессов и в логах терминала — трёх местах, откуда его никто потом не вычистит.
/// Поэтому спрашивается отдельно и не отображается при вводе.
/// <para>
/// Когда ввод перенаправлен — в сценарии, в CI, — спрашивать некого. В этом случае
/// команда честно отказывается вместо того, чтобы зависнуть на чтении из пустого
/// потока: зависшая команда выглядит как поломка продукта.
/// </para>
/// </remarks>
internal static class Secrets
{
    /// <summary>
    /// Спрашивает пароль. <c>null</c> — спросить не удалось.
    /// </summary>
    /// <param name="keep">
    /// Прежнее значение, если набор правится. Пустой ввод оставляет его на месте:
    /// менять порт у существующего набора, заново набирая пароль, незачем.
    /// </param>
    public static string? Read(string label, string? keep = null)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                $"{label} нужно ввести с клавиатуры, а ввод перенаправлен. "
                + "Запустите команду в обычном терминале.");

            return null;
        }

        Console.Write(keep is null ? $"{label}: " : $"{label} (пусто — оставить прежний): ");

        var typed = ReadHidden();

        Console.WriteLine();

        if (typed.Length == 0)
        {
            if (keep is not null)
            {
                return keep;
            }

            Console.Error.WriteLine($"{label} не задан.");

            return null;
        }

        return typed;
    }

    /// <summary>Читает строку, не отображая её.</summary>
    private static string ReadHidden()
    {
        var typed = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }

                continue;
            }

            // Управляющие клавиши пропускаются: стрелка или Home не должны попадать
            // в пароль символом-заглушкой.
            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}
