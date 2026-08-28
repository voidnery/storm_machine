using System.Globalization;
namespace StormMachine.Application.Probes;

/// <summary>
/// Проверка значений параметров по объявлению пробы.
/// </summary>
/// <remarks>
/// Вынесено из пробы ICMP в И-2: с появлением шести проб стало ясно, что проверка границ
/// одинакова для всех и зависит только от <c>ProbeDescriptor</c>. Это же подтверждает
/// замысел «UI строит форму по объявлению»: если проверка выводима из объявления,
/// то и форма выводима.
/// <para>
/// Переехало в слой приложения в И-12, когда понадобилось второму проекту: удалённая
/// проба живёт не рядом с остальными, а копия проверки разошлась бы с оригиналом
/// на первой же правке.
/// </para>
/// </remarks>
public static class ProbeValidation
{
    public static IReadOnlyList<ProbeValidationError> Validate(ProbeDescriptor descriptor, ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ProbeValidationError>();

        foreach (var parameter in descriptor.Parameters)
        {
            if (!request.Parameters.TryGetValue(parameter.Name, out var raw) || raw is null)
            {
                continue;
            }

            if (parameter.Type is ProbeParameterType.Boolean or ProbeParameterType.Text)
            {
                continue;
            }

            // Выбор из списка — не число, и проверять его границами бессмысленно.
            // До И-12 такого параметра не было ни у одной пробы, и разбор молча
            // считал числом всё, что не текст и не флаг: первый же выбор «upload»
            // был отвергнут как «не число».
            if (parameter.Type == ProbeParameterType.Choice)
            {
                var text = raw.ToString() ?? string.Empty;

                if (parameter.Choices is { Count: > 0 } choices
                    && !choices.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(new ProbeValidationError(
                        parameter.Name,
                        $"Значение «{text}» не из списка: {string.Join(", ", choices)}."));
                }

                continue;
            }

            if (!TryToDouble(raw, out var value))
            {
                errors.Add(new ProbeValidationError(parameter.Name, $"Значение «{raw}» не является числом."));
                continue;
            }

            if (parameter.Minimum is { } min && value < min)
            {
                errors.Add(new ProbeValidationError(parameter.Name, $"Минимум — {min:0.###}, получено {value:0.###}."));
            }

            if (parameter.Maximum is { } max && value > max)
            {
                errors.Add(new ProbeValidationError(parameter.Name, $"Максимум — {max:0.###}, получено {value:0.###}."));
            }
        }

        return errors;
    }

    private static bool TryToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case double d: value = d; return true;
            case string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
