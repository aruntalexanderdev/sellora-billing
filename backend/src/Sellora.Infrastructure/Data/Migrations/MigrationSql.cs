using System.Reflection;

namespace Sellora.Infrastructure.Data.Migrations;

/// <summary>
/// Reads frozen SQL embedded next to the migrations (<c>Data/Migrations/Sql/*.sql</c>, logical name = file name). The files are pinned by a
/// SHA-256 test and forced to LF by <c>.gitattributes</c>, so an applied migration never runs different text than it was released with.
/// </summary>
internal static class MigrationSql
{
    public static string Read(string fileName)
    {
        var assembly = typeof(MigrationSql).Assembly;
        using var stream = assembly.GetManifestResourceStream(fileName)
            ?? throw new InvalidOperationException($"Embedded migration SQL '{fileName}' was not found.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
