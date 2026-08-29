using System.Runtime.CompilerServices;

// Передатчик хода сопряжения — внутренний, а проверять его надо: именно на этом
// пути в И-19 нашлось, что указание оператору «набери на второй машине вот это»
// не доходило до графического клиента вовсе.
[assembly: InternalsVisibleTo("StormMachine.Agents.UnitTests")]
