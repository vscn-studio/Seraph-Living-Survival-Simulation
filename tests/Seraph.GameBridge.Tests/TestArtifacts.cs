// Keep diagnostic renders and reports separate from the release packages.
internal static class TestArtifacts
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Seraph-tests");

    internal static string DirectoryPath(string name) =>
        Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

    internal static string FilePath(string name)
    {
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, name);
    }
}
