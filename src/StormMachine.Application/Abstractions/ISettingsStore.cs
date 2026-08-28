namespace StormMachine.Application.Abstractions;

/// <summary>Одна настройка в том виде, в каком её показывают человеку.</summary>
/// <param name="Key">Ключ: <c>alerts.webhook.url</c>.</param>
/// <param name="Value">Значение. У секретов — заменено на пометку, а не на пустоту.</param>
/// <param name="IsSecret">Хранится ли значение зашифрованным.</param>
public sealed record SettingEntry(string Key, string? Value, bool IsSecret);

/// <summary>
/// Настройки продукта: ключ — значение.
/// </summary>
/// <remarks>
/// Появилось ради каналов оповещения, и пока этим и ограничено. Полноценный экран
/// настроек — И-16; здесь заложено только хранилище, чтобы адрес webhook и параметры
/// почты не пришлось держать в переменных окружения или в файле рядом с базой.
/// </remarks>
public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записывает значение.
    /// </summary>
    /// <param name="secret">
    /// Значение шифруется средствами операционной системы и привязывается к учётной
    /// записи. Копия базы, унесённая на другую машину, секрет не раскроет — но и
    /// не восстановит, и об этом сказано прямо при вводе.
    /// </param>
    Task SetAsync(string key, string? value, bool secret = false, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Все настройки с заданным началом ключа. Секреты возвращаются скрытыми.</summary>
    Task<IReadOnlyList<SettingEntry>> ListAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Защита секретов средствами машины.
/// </summary>
/// <remarks>
/// Порт, а не прямой вызов DPAPI: слой приложения не должен знать про Windows.
/// Реализация привязывает шифротекст к учётной записи пользователя — это и есть
/// то свойство, ради которого всё затевалось: пароль от почтового ящика не должен
/// лежать в базе открытым текстом, потому что базу копируют и присылают в поддержку.
/// </remarks>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <summary>Расшифровывает. Возвращает <see langword="null"/>, если значение не наше.</summary>
    string? Unprotect(string protectedValue);
}
