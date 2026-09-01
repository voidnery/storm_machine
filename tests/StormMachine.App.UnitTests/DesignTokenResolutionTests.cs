using System.Reflection;
using Avalonia.Headless;
using StormMachine.App.Views.Controls;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Каждый ключ токена, названный в коде, есть в словаре приложения.
/// </summary>
/// <remarks>
/// Ключ из кода компилятор не проверяет: опечатка в строке даёт серую кисть вместо
/// цвета — карта нарисуется, тесты промолчат, и увидит это оператор. Здесь проверяются
/// все ключи <see cref="DesignTokens"/> и вся таблица категорий устройств: подсветка
/// категорий и была тем, ради чего цвета вообще заводились.
/// </remarks>
[Collection("Headless")]
public sealed class DesignTokenResolutionTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact]
    public async Task EveryNamedToken_ResolvesToBrush()
    {
        var missing = await _session.Dispatch(
            () =>
            {
                var keys = typeof(DesignTokens)
                    .GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
                    .Select(f => (string)f.GetRawConstantValue()!)
                    .Concat(TopologyCanvas.RoleTokens.Values)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                Assert.NotEmpty(keys);

                return Task.FromResult(keys.Where(key => !DesignTokens.Exists(key)).ToList());
            },
            CancellationToken.None);

        Assert.True(
            missing.Count == 0,
            "Ключей нет в словаре App.axaml — вместо цвета будет серая кисть: "
            + string.Join(", ", missing));
    }

    /// <summary>Каждая категория из классификатора умеет краситься на карте.</summary>
    /// <remarks>
    /// Новая категория без цвета не ломается, а тихо теряет подсветку — ровно ту,
    /// которую оператор просил в И-24, потому что тег текстом почти не виден.
    /// </remarks>
    [Fact]
    public void EveryKnownRole_HasColour()
    {
        var uncoloured = Domain.Discovery.DeviceClassifier.KnownRoles
            .Where(role => !TopologyCanvas.RoleTokens.ContainsKey(role))
            .ToList();

        Assert.True(
            uncoloured.Count == 0,
            "Категории без цвета на карте: " + string.Join(", ", uncoloured));
    }
}
