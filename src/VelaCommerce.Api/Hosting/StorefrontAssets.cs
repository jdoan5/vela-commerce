using System.Text.Json;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;

namespace VelaCommerce.Api.Hosting;

/// <summary>
/// Finds the storefront's static files on disk, so the API host can serve them from its own origin.
/// <para>
/// This exists because of one decision that shapes the whole phase: the demo session is an
/// <c>HttpOnly; SameSite=Lax</c> cookie, and a browser will not attach that cookie to a fetch
/// issued by a page on another origin. The production topology puts a rewrite in front of both —
/// the browser sees one host, <c>/api/*</c> goes to the API and everything else to the storefront's
/// files — and local development has to mirror it exactly, or the cart works in production and
/// silently never works on a developer's machine. The cheapest way to have one origin locally is
/// for the API host to serve the storefront's files itself.
/// </para>
/// <para>
/// Nothing here throws. The API must boot with no storefront present at all: the build-time OpenAPI
/// generator executes the real entry point, CI runs the API alone against the Bruno collection, and
/// the deployed API may well sit behind a CDN that serves the storefront itself. In every one of
/// those cases the correct behaviour is "serve the API, log that there is no shop attached", not a
/// host that refuses to start.
/// </para>
/// </summary>
internal static class StorefrontAssets
{
    /// <summary>
    /// Configuration key holding an explicit path to the storefront's published <c>wwwroot</c>.
    /// Absolute, or relative to the content root. Set this in a deployment that lays the files out
    /// somewhere the conventions below would not find them; leave it unset otherwise.
    /// </summary>
    public const string RootConfigurationKey = "Storefront:Root";

    /// <summary>The file whose presence proves a directory really is a built storefront and not an empty folder.</summary>
    private const string EntryFile = "index.html";

    /// <summary>
    /// Build configurations probed in development, most likely first. A developer running
    /// <c>dotnet run --project src/VelaCommerce.Api</c> has almost certainly just built Debug.
    /// </summary>
    private static readonly string[] ProbedConfigurations = ["Debug", "Release"];

    /// <summary>
    /// Locates the storefront, or returns null when there is none to serve.
    /// </summary>
    /// <param name="environment">Supplies the content root every relative probe is anchored to, and gates the development-only probes.</param>
    /// <param name="configuration">Read for <see cref="RootConfigurationKey"/>.</param>
    /// <param name="logger">Told what was found and — more usefully — what was looked at when nothing was.</param>
    public static StorefrontFiles? Locate(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger logger)
    {
        var probed = new List<string>();

        // 1. An explicit path always wins. A deployment that has been told where the files are
        //    should never have that overridden by a convention that happens to match first.
        if (configuration[RootConfigurationKey] is { Length: > 0 } configured)
        {
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
            probed.Add(root);

            if (TryDirectory(root) is { } fromConfiguration)
            {
                return fromConfiguration;
            }

            // Worth its own line at Warning: somebody set the key deliberately and it did not work,
            // which is a different problem from "no storefront was ever deployed here".
            logger.LogWarning(
                "{Key} is set to {Path}, but there is no {EntryFile} there. Falling back to the conventional locations.",
                RootConfigurationKey,
                root,
                EntryFile);
        }

        // 2. The published layout: the storefront's published wwwroot copied next to the API. This
        //    is what a container image contains, and it is the only branch that runs in production.
        var webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        probed.Add(webRoot);
        if (TryDirectory(webRoot) is { } published)
        {
            return published;
        }

        // The remaining probes reach across the repository into a sibling project's build output.
        // That is a development convenience and must never be reachable in production, where such a
        // path could only resolve by accident.
        if (!environment.IsDevelopment())
        {
            LogNotFound(logger, environment, probed);
            return null;
        }

        var storefrontProject = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, "..", "VelaCommerce.Storefront"));

        // 3. The static web assets manifest the SDK writes on every build. This is the branch that
        //    makes `dotnet run --project src/VelaCommerce.Api` give a working shop straight after a
        //    plain `dotnet build`, with no publish step to remember — see StaticWebAssetsManifest.
        foreach (var configurationName in ProbedConfigurations)
        {
            var manifest = Path.Combine(
                storefrontProject, "obj", configurationName, "net10.0", StaticWebAssetsManifest.FileName);

            probed.Add(manifest);

            if (StaticWebAssetsManifest.TryLoad(manifest, logger) is { } fromManifest)
            {
                return new StorefrontFiles(fromManifest, manifest);
            }
        }

        // 4. The storefront's own publish output. Slower to produce than a build, but it is the
        //    exact tree production serves, so it is the right thing to test a deploy question
        //    against.
        foreach (var configurationName in ProbedConfigurations)
        {
            var publishRoot = Path.Combine(
                storefrontProject, "bin", configurationName, "net10.0", "publish", "wwwroot");

            probed.Add(publishRoot);

            if (TryDirectory(publishRoot) is { } fromPublish)
            {
                return fromPublish;
            }
        }

        LogNotFound(logger, environment, probed);
        return null;
    }

    /// <summary>
    /// Accepts a directory only if it actually contains the storefront's entry document. A
    /// directory that exists but is empty is the failure mode worth catching: it produces a host
    /// that serves 404s for every page instead of one that says out loud that it has no storefront.
    /// </summary>
    private static StorefrontFiles? TryDirectory(string root)
    {
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, EntryFile)))
        {
            return null;
        }

        return new StorefrontFiles(new PhysicalFileProvider(root), root);
    }

    private static void LogNotFound(ILogger logger, IWebHostEnvironment environment, List<string> probed)
    {
        logger.LogWarning(
            "No storefront found, so this host serves the API only. Looked at: {Probed}. In development, "
            + "run `dotnet build src/VelaCommerce.Storefront` and restart; in a deployment, copy the "
            + "storefront's published wwwroot to {WebRoot} or set {Key}.",
            string.Join("; ", probed),
            Path.Combine(environment.ContentRootPath, "wwwroot"),
            RootConfigurationKey);
    }
}

/// <summary>
/// A located storefront: the files, and a human-readable note about where they came from so the
/// startup log can say which of the four probes won.
/// </summary>
/// <param name="Provider">The file provider the static-file middleware and the SPA fallback both read.</param>
/// <param name="Origin">The path that was matched, for logging only.</param>
internal sealed record StorefrontFiles(IFileProvider Provider, string Origin);

/// <summary>
/// A reader for the static web assets manifest that the .NET SDK writes beside every web project's
/// build output.
/// <para>
/// The manifest exists because a Blazor WebAssembly project's files are not all in one folder after
/// a build, and cannot be. <c>index.html</c> is rewritten at build time — the source file contains
/// <c>blazor.webassembly#[.{fingerprint}].js</c>, which is not a URL — so the servable copy lives
/// under <c>obj/</c> with a hashed name. The framework files live under <c>bin/</c>. Everything else
/// (<c>css/app.css</c>, <c>catalog.snapshot.json</c>) is served from the project's own
/// <c>wwwroot/</c>. The manifest is the SDK's own index over those roots, and reading it is what
/// lets this host serve a correct storefront after nothing more than <c>dotnet build</c>.
/// </para>
/// <para>
/// This is the mechanism ASP.NET Core itself uses for referenced projects in development, reached
/// here without a project reference on purpose: the API must not have to build the WebAssembly app
/// to build itself, CI regenerates the OpenAPI document by executing this host, and in production
/// the storefront is served as published static files rather than compiled into the API.
/// </para>
/// <para>
/// Development only, and every failure is a silent null: a manifest from a future SDK that this
/// reader does not understand must degrade to "no storefront found", never to an exception on a
/// developer's first run.
/// </para>
/// </summary>
internal static class StaticWebAssetsManifest
{
    /// <summary>The SDK's name for the development-time manifest, beside <c>obj/{Configuration}/{tfm}/</c>.</summary>
    public const string FileName = "staticwebassets.development.json";

    /// <summary>
    /// Reads a manifest into a file provider, or returns null if it is absent, unreadable or does
    /// not describe a storefront.
    /// </summary>
    public static IFileProvider? TryLoad(string manifestPath, ILogger logger)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("ContentRoots", out var contentRootsElement)
                || contentRootsElement.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("Root", out var treeElement))
            {
                return null;
            }

            var contentRoots = contentRootsElement
                .EnumerateArray()
                .Select(static element => element.GetString() ?? string.Empty)
                .ToArray();

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var patternRoots = new List<IFileProvider>();

            Walk(treeElement, string.Empty, contentRoots, files, patternRoots);

            // An index with no index.html is not a storefront. Saying so here is what lets the
            // caller move on to the next probe rather than mounting a provider that 404s everything.
            if (!files.ContainsKey("index.html"))
            {
                return null;
            }

            return new ManifestFileProvider(files, [.. patternRoots]);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A half-written manifest during a concurrent build, or a shape a later SDK changed.
            // Neither is worth failing a boot over; the next probe or the API-only path takes over.
            logger.LogDebug(
                exception,
                "Could not read the storefront's static web assets manifest at {Manifest}; trying the next location.",
                manifestPath);

            return null;
        }
    }

    /// <summary>
    /// Walks the manifest's trie, flattening it into "request path -> file on disk".
    /// <para>
    /// Two node shapes matter. An <c>Asset</c> names one file explicitly and is what resolves
    /// <c>index.html</c> to its rewritten copy under <c>obj/</c>. A <c>Patterns</c> entry is a glob
    /// fallback over a whole content root, and is how the project's own <c>wwwroot/</c> — the
    /// stylesheet, the catalog snapshot — is served without every file being listed. Explicit assets
    /// are collected here and win at lookup time; patterns become plain physical providers consulted
    /// afterwards.
    /// </para>
    /// </summary>
    private static void Walk(
        JsonElement node,
        string path,
        string[] contentRoots,
        Dictionary<string, string> files,
        List<IFileProvider> patternRoots)
    {
        if (node.TryGetProperty("Asset", out var asset) && asset.ValueKind == JsonValueKind.Object)
        {
            var subPath = asset.TryGetProperty("SubPath", out var subPathElement) ? subPathElement.GetString() : null;
            var index = asset.TryGetProperty("ContentRootIndex", out var indexElement) && indexElement.TryGetInt32(out var i)
                ? i
                : -1;

            // A SubPath containing a brace is a pre-compressed variant whose real name carries an
            // integrity placeholder. Nothing requests those — the browser asks for the plain file
            // and gets it — so mapping them would only ever produce a path that does not exist.
            if (subPath is { Length: > 0 }
                && !subPath.Contains('{', StringComparison.Ordinal)
                && index >= 0
                && index < contentRoots.Length
                && path.Length > 0)
            {
                files[path] = Path.GetFullPath(Path.Combine(contentRoots[index], subPath));
            }
        }

        if (node.TryGetProperty("Patterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array)
        {
            foreach (var pattern in patterns.EnumerateArray())
            {
                if (!pattern.TryGetProperty("ContentRootIndex", out var indexElement)
                    || !indexElement.TryGetInt32(out var index)
                    || index < 0
                    || index >= contentRoots.Length)
                {
                    continue;
                }

                var directory = contentRoots[index];
                if (Directory.Exists(directory))
                {
                    patternRoots.Add(new PhysicalFileProvider(directory));
                }
            }
        }

        if (!node.TryGetProperty("Children", out var children) || children.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var child in children.EnumerateObject())
        {
            var childPath = path.Length == 0 ? child.Name : $"{path}/{child.Name}";
            Walk(child.Value, childPath, contentRoots, files, patternRoots);
        }
    }

    /// <summary>
    /// Serves the flattened manifest.
    /// <para>
    /// Deliberately minimal: <see cref="GetFileInfo"/> is the only member the static-file middleware
    /// and the SPA fallback call. Directory listings are never wanted — a storefront that can be
    /// enumerated over HTTP is a mistake, not a feature — and change tokens are not either, because
    /// a rebuild rewrites the manifest and a restart is what picks it up.
    /// </para>
    /// </summary>
    private sealed class ManifestFileProvider : IFileProvider
    {
        private readonly Dictionary<string, string> _files;
        private readonly IFileProvider[] _patternRoots;

        public ManifestFileProvider(Dictionary<string, string> files, IFileProvider[] patternRoots)
        {
            _files = files;
            _patternRoots = patternRoots;
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            var key = subpath.AsSpan().TrimStart('/').ToString();

            if (key.Length > 0 && _files.TryGetValue(key, out var physical))
            {
                var info = new PhysicalFileInfo(new FileInfo(physical));
                if (info.Exists)
                {
                    return info;
                }
            }

            foreach (var provider in _patternRoots)
            {
                var candidate = provider.GetFileInfo(subpath);
                if (candidate.Exists)
                {
                    return candidate;
                }
            }

            return new NotFoundFileInfo(subpath);
        }

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
