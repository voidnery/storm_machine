using System.Runtime.CompilerServices;

// Тело webhook собирается внутренним методом, а проверять надо именно его: канал,
// молча отправивший не то, хуже отсутствующего — на него рассчитывают.
[assembly: InternalsVisibleTo("StormMachine.Alerting.UnitTests")]
