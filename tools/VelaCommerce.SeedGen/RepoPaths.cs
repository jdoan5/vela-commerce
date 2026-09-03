namespace VelaCommerce.SeedGen;

/// <summary>
/// Finds the repository root so the generator's default output paths mean the same thing
/// whether it is run from the repo root, from the tool's own directory, or as a built
/// executable out of <c>bin/</c>.
/// <para>
/// The alternative — a relative path from the current directory — silently writes the
/// storefront's catalog into whatever folder the shell happened to be sitting in, and the
/// next run appears to produce no change because the storefront is still reading yesterday's
/// file.
/// </para>
/// </summary>
internal static class RepoPaths
{
    private const string SolutionFile = "VelaCommerce.slnx";

    /// <summary>Where the storefront expects the committed snapshot, relative to the repo root.</summary>
    private static readonly string[] SnapshotSegments =
        ["src", "VelaCommerce.Storefront", "wwwroot", "catalog.snapshot.json"];

    /// <summary>Where the importer expects the committed seed, relative to the repo root.</summary>
    private static readonly string[] SeedSegments = ["seed", "catalog.seed.json"];

    public static string DefaultSnapshotPath() =>
        Path.GetFullPath(Path.Combine([Root(), .. SnapshotSegments]));

    /// <summary>
    /// Anchored to the repo root for the same reason as the snapshot. A path relative to the
    /// working directory drops a stray catalog.seed.json wherever the shell happened to be,
    /// while the real one under seed/ silently goes stale.
    /// </summary>
    public static string DefaultSeedPath() =>
        Path.GetFullPath(Path.Combine([Root(), .. SeedSegments]));

    /// <summary>
    /// Walks up from the working directory, then from the assembly's own location, looking for
    /// the solution file. Falls back to the working directory so the tool still writes
    /// something predictable if it is ever copied out of the repo.
    /// </summary>
    private static string Root() =>
        FindAncestorContainingSolution(Directory.GetCurrentDirectory())
        ?? FindAncestorContainingSolution(AppContext.BaseDirectory)
        ?? Directory.GetCurrentDirectory();

    private static string? FindAncestorContainingSolution(string startingPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startingPath));

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
