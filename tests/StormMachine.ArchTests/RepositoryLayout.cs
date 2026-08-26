using System.Xml.Linq;

namespace StormMachine.ArchTests;

/// <summary>Один файл проекта с разобранными ссылками.</summary>
internal sealed record ProjectFile(
    string Name,
    string Path,
    string RelativePath,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences);

/// <summary>
/// Чтение структуры репозитория для архитектурных проверок.
/// </summary>
/// <remarks>
/// Правила проверяются по файлам проектов, а не по загруженным сборкам: так видно
/// и ссылки на пакеты, и их отсутствие. Отсутствие зависимости невозможно доказать
/// рефлексией, а именно это и требуется для слоя Domain.
/// </remarks>
internal static class RepositoryLayout
{
    private const string SolutionFileName = "StormMachine.slnx";

    public static string Root { get; } = FindRoot();

    public static IReadOnlyList<ProjectFile> SourceProjects { get; } = Load("src");

    public static IReadOnlyList<ProjectFile> PluginProjects { get; } = Load("plugins");

    public static ProjectFile? FindProject(string name) =>
        SourceProjects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public static IEnumerable<string> SourceFiles(string subdirectory)
    {
        var path = Path.Combine(Root, subdirectory);
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            : [];
    }

    /// <summary>
    /// Возвращает код файла без комментариев.
    /// </summary>
    /// <remarks>
    /// Нужно проверкам, которые ищут запрещённые обращения к API: упоминание такого API
    /// в комментарии, объясняющем запрет, — не нарушение, а документация. Разбор грубый,
    /// построчный: полноценный синтаксический анализ здесь избыточен.
    /// </remarks>
    public static string StripComments(string source)
    {
        var result = new System.Text.StringBuilder(source.Length);
        var inBlockComment = false;

        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine;

            if (inBlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                line = line[(end + 2)..];
                inBlockComment = false;
            }

            var blockStart = line.IndexOf("/*", StringComparison.Ordinal);
            if (blockStart >= 0)
            {
                var end = line.IndexOf("*/", blockStart + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inBlockComment = true;
                    line = line[..blockStart];
                }
                else
                {
                    line = line[..blockStart] + line[(end + 2)..];
                }
            }

            var lineComment = line.IndexOf("//", StringComparison.Ordinal);
            if (lineComment >= 0)
            {
                line = line[..lineComment];
            }

            result.Append(line).Append('\n');
        }

        return result.ToString();
    }

    public static string Relative(string absolutePath) =>
        Path.GetRelativePath(Root, absolutePath).Replace('\\', '/');

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                $"Не найден корень репозитория: поиск {SolutionFileName} вверх от {AppContext.BaseDirectory}");
    }

    private static IReadOnlyList<ProjectFile> Load(string subdirectory)
    {
        var path = Path.Combine(Root, subdirectory);
        if (!Directory.Exists(path))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Select(Parse)
            .OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    private static ProjectFile Parse(string csprojPath)
    {
        var document = XDocument.Load(csprojPath);

        var projectRefs = document.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
            .ToList();

        var packageRefs = document.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        return new ProjectFile(
            Path.GetFileNameWithoutExtension(csprojPath),
            csprojPath,
            Relative(csprojPath),
            projectRefs,
            packageRefs);
    }
}
