using System.Runtime.CompilerServices;

// Разбор пакетов DNS написан вручную и содержит нетривиальные места: сжатие имён
// по указателям и защиту от зацикливания. Такой код обязан быть покрыт тестами,
// поэтому внутренние типы открыты тестовой сборке.
[assembly: InternalsVisibleTo("StormMachine.Probes.UnitTests")]
