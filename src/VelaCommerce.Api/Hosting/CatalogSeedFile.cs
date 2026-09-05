namespace VelaCommerce.Api.Hosting;

/// <summary>
/// Finds the generated catalog on disk, so a host that seeds itself can find it wherever it is
/// running from.
/// <para>
/// This started as one line in <c>Program.cs</c>: <c>{ContentRoot}/../../seed/catalog.seed.json</c>.
/// That is a walk up the repository, and it is correct in exactly one situation — <c>dotnet run
/// --project src/VelaCommerce.Api</c> from a clone. Inside a container the content root is
/// <c>/app</c>, the walk resolves to <c>/seed/catalog.seed.json</c>, and there is nothing there:
/// the file was never published either, because the project had no <c>Content</c> item for it. The
/// image booted, served an empty shop, and said nothing about why.
/// </para>
/// <para>
/// Same shape as <see cref="StorefrontAssets"/>, deliberately, because it is the same problem: an
/// asset that lives in one place in a repository and a different place in an image, needed by a
/// host that has to work in both.
/// </para>
/// </summary>
internal static class CatalogSeedFile
{
    /// <summary>
    /// Configuration key holding an explicit path to the seed. Absolute, or relative to the content
    /// root. Set it in a layout the conventions below would not find; leave it unset otherwise.
    /// </summary>
    public const string PathConfigurationKey = "Seed:Path";

    /// <summary>The published name and folder, matching the <c>Content</c> item in the csproj.</summary>
    private const string PublishedRelativePath = "seed/catalog.seed.json";

    /// <summary>
    /// Returns the seed file's full path, or <see langword="null"/> when there is none to read.
    /// <para>
    /// <b>Null rather than an exception, on purpose.</b> A host that throws here crash-loops, and a
    /// crash-looping container is the one failure mode this project's cost notes single out: a
    /// restarting container never counts as idle, so it bills at the active rate around the clock.
    /// Booting with an empty catalog and an error in the log is recoverable and diagnosable;
    /// refusing to boot is neither, and it takes the health endpoint down with it.
    /// </para>
    /// </summary>
    public static string? Locate(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        var probed = new List<string>();

        // 1. An explicit path always wins, for the reason StorefrontAssets gives: a deployment that
        //    has been told where its files are should not have that overridden by a convention.
        if (configuration[PathConfigurationKey] is { Length: > 0 } configured)
        {
            var explicitPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
            probed.Add(explicitPath);

            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }

            // Its own line at Warning: someone set this deliberately and it did not work, which is a
            // different problem from "this layout has no seed".
            logger.LogWarning(
                "{Key} is set to {Path}, but there is no file there. Falling back to the conventional locations.",
                PathConfigurationKey,
                explicitPath);
        }

        // 2. The published layout — the seed copied next to the assembly. This is what the container
        //    image contains, and the branch that has to work when nobody has configured anything.
        var published = Path.GetFullPath(Path.Combine(environment.ContentRootPath, PublishedRelativePath));
        probed.Add(published);

        if (File.Exists(published))
        {
            return published;
        }

        // 3. The repository layout, development only. It reaches across the solution into a sibling
        //    directory, which in production could only resolve by accident.
        if (environment.IsDevelopment())
        {
            var inRepository = Path.GetFullPath(
                Path.Combine(environment.ContentRootPath, "..", "..", PublishedRelativePath));

            probed.Add(inRepository);

            if (File.Exists(inRepository))
            {
                return inRepository;
            }
        }

        // Logs every path tried, because "seed not found" without the list is a message that sends
        // someone to read this file to find out what it looked at.
        logger.LogError(
            "No catalog seed found; the shop will start empty. Looked at: {Probed}. Set {Key} to "
            + "point at one, or run tools/VelaCommerce.SeedGen to generate it.",
            string.Join(", ", probed),
            PathConfigurationKey);

        return null;
    }
}
