using System.Runtime.CompilerServices;

// Читатель формата MaxMind DB и распознавание частных диапазонов написаны вручную.
// Ошибка в них не падает, а тихо возвращает чужую автономную систему — такой код
// обязан быть покрыт тестами, поэтому внутренние типы открыты тестовой сборке.
[assembly: InternalsVisibleTo("StormMachine.Platform.UnitTests")]
