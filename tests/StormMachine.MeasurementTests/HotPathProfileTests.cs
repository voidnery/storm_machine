using System.Globalization;
using Xunit.Abstractions;

namespace StormMachine.MeasurementTests;

/// <summary>
/// Профиль горячего пути: во что обходится каждый следующий сэмпл.
/// </summary>
/// <remarks>
/// Профилирование — обязательство И-19 из плана. Бюджет аллокаций у горячего пути
/// проверялся с И-1, но одним числом на фиксированной длине ряда, и одного числа мало:
/// оно не отличает «проба стоит N байт» от «проба стоит N байт, и ещё столько же
/// накапливается на каждый сэмпл».
/// <para>
/// Разница существенная. Постоянный расход на сэмпл — это принцип 9 соблюдён:
/// непрерывный монитор может работать неделю, и десятитысячный сэмпл обойдётся
/// как первый. Растущий расход означает, что где-то копится состояние, и такой
/// монитор доживёт до первой сборки второго поколения посреди измерения — а её
/// пауза подмешается прямо в измеряемый джиттер.
/// </para>
/// <para>
/// Одним числом это не ловится: на двухстах сэмплах накопление ещё незаметно.
/// Ловится сравнением наклона на разных длинах ряда, что здесь и делается.
/// </para>
/// </remarks>
public sealed class HotPathProfileTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Цена сэмпла не растёт с длиной ряда.
    /// </summary>
    /// <remarks>
    /// Считается предельная цена — разность расхода между длинным и коротким рядом,
    /// делённая на разность длин. Так из числа уходит постоянная часть: разрешение
    /// имени, открытие сокета, разовые буферы. Остаётся ровно то, что тратится
    /// на каждый следующий сэмпл, — предмет принципа 9.
    /// </remarks>
    [Fact]
    public async Task CostPerSample_DoesNotGrowWithSeriesLength()
    {
        await using var services = MeasurementHarness.BuildServices();

        // Прогрев: первая проба платит за компиляцию и разовые буферы, и без него
        // самый короткий ряд оказался бы самым дорогим.
        await MeasurementHarness.RunAsync(services, MeasurementHarness.LoopbackRequest(50));

        var measurements = new List<(int Count, long Allocated, double PerSample)>();

        foreach (var count in new[] { 100, 400, 1600 })
        {
            // Лучший из трёх: полный прогон идёт параллельно с другими тестовыми
            // сборками, и единичный замер ловит чужую нагрузку. Меряется достижимый
            // пол — то же решение, что в остальных проверках этой сборки.
            var best = long.MaxValue;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var before = GC.GetTotalAllocatedBytes(precise: true);

                await MeasurementHarness.RunAsync(services, MeasurementHarness.LoopbackRequest(count));

                best = Math.Min(best, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            measurements.Add((count, best, best / (double)count));

            _output.WriteLine(
                $"{count,6} сэмплов: {best,10:N0} байт всего, {best / (double)count,8:N0} байт на сэмпл");
        }

        var shortRun = measurements[0];
        var longRun = measurements[^1];

        // Предельная цена: сколько стоит каждый сэмпл сверх постоянных расходов.
        var marginal = (longRun.Allocated - shortRun.Allocated)
                       / (double)(longRun.Count - shortRun.Count);

        _output.WriteLine(string.Empty);
        _output.WriteLine(
            $"Предельная цена сэмпла: {marginal.ToString("N0", CultureInfo.InvariantCulture)} байт");
        _output.WriteLine(
            $"Средняя на коротком ряду: {shortRun.PerSample.ToString("N0", CultureInfo.InvariantCulture)} байт");
        _output.WriteLine(
            $"Средняя на длинном ряду:  {longRun.PerSample.ToString("N0", CultureInfo.InvariantCulture)} байт");

        // Главное утверждение: средняя цена на длинном ряду не превышает среднюю
        // на коротком. Растущая означала бы, что в горячем пути копится состояние
        // и непрерывный монитор обречён на паузы сборщика посреди измерения.
        Assert.True(
            longRun.PerSample <= shortRun.PerSample,
            $"Сэмпл на ряду из {longRun.Count} обходится в {longRun.PerSample:N0} байт против "
            + $"{shortRun.PerSample:N0} на ряду из {shortRun.Count}. Расход растёт с длиной ряда — "
            + "в горячем пути копится состояние, и непрерывный монитор до него доживёт.");

        Assert.True(
            marginal > 0,
            "Предельная цена сэмпла неположительна — замер недостоверен, "
            + "скорее всего измерение не выполнялось.");

        // Потолок предельной цены отдельно от бюджета И-1: тот считает вместе
        // с постоянными расходами, этот — только повторяющуюся часть.
        Assert.True(
            marginal <= 4096,
            $"Каждый сэмпл обходится в {marginal:N0} байт сверх постоянных расходов. "
            + "Это уже не «ноль аллокаций в горячем пути».");
    }
}
