namespace KassirERP.Api.Tests;

/// <summary>
/// tenant_id попадает в соединение только в одном месте. Если кто-то напишет
/// set_config('app.tenant_id', ...) где-то ещё — тест упадёт.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly string SrcDir = Path.Combine(FindRepoRoot(), "src");

    [Theory]
    [InlineData("app.tenant_id")]
    [InlineData("set_config")]
    public void Tenant_is_set_on_connection_only_in_TenantDb(string needle)
    {
        var files = SourceFiles().Where(f => File.ReadAllText(f).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(SrcDir, f).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["KassirERP.Infrastructure/Persistence/TenantDb.cs"], files);
    }

    [Fact]
    public void Raw_NpgsqlDataSource_is_not_exposed_outside_Infrastructure()
    {
        var offenders = SourceFiles()
            .Where(f => !f.Replace('\\', '/').Contains("/KassirERP.Infrastructure/"))
            .Where(f => File.ReadAllText(f).Contains("Npgsql", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KassirERP.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (KassirERP.slnx).");
    }
}
