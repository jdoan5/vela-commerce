using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace VelaCommerce.Api.Hosting;

/// <summary>
/// Serves the Blazor storefront from this host, so the shop and the API share one origin.
/// <para>
/// <strong>Why this exists at all.</strong> The demo session is an <c>HttpOnly; SameSite=Lax</c>
/// cookie sealed with Data Protection. A browser will not send it on a fetch made by a page loaded
/// from another origin, so a storefront on <c>localhost:5031</c> talking to an API on
/// <c>localhost:5008</c> gets a brand-new anonymous session on every request and an eternally empty
/// cart — with no error anywhere to explain it. The alternative fix, CORS plus
/// <c>SameSite=None</c>, weakens the cookie and diverges from production. So the storefront is
/// served from here instead, and the browser only ever sees one host, exactly as it will behind the
/// production rewrite.
/// </para>
/// </summary>
public static class StorefrontHostingExtensions
{
    /// <summary>
    /// Paths this host owns. The SPA fallback must never answer for one of them: a request to an API
    /// route that does not exist has to come back as a 404 the caller can act on, not as an HTML
    /// document that a fetch will then fail to parse as JSON — which is a far more confusing bug to
    /// be handed than a plain 404.
    /// <para>
    /// <c>/admin</c> is here for the server-rendered admin pages, and it is reserved BEFORE those
    /// pages exist rather than alongside them. Without it the fallback answers <c>/admin</c> with
    /// the storefront's shell: a reviewer following the link gets the shop again, with no error
    /// anywhere and nothing in a log to explain it.
    /// </para>
    /// </summary>
    private static readonly string[] ReservedPrefixes = ["/api", "/admin", "/health", "/alive", "/openapi", "/scalar"];

    /// <summary>
    /// Content types the default provider does not know but the WebAssembly runtime needs. A file
    /// whose extension is unmapped is not served at all — <see cref="StaticFileOptions"/> leaves
    /// <c>ServeUnknownFileTypes</c> off, correctly — so a missing entry here reads as a 404 on
    /// <c>icudt_EFIGS.dat</c> and a shop that never finishes booting.
    /// </summary>
    private static readonly (string Extension, string ContentType)[] AdditionalContentTypes =
    [
        (".wasm", "application/wasm"),
        (".dat", "application/octet-stream"),   // ICU globalisation data
        (".blat", "application/octet-stream"),  // Blazor asset bundles
        (".webcil", "application/octet-stream"),
        (".dll", "application/octet-stream"),
        (".pdb", "application/octet-stream"),
        (".map", "application/json"),
    ];

    /// <summary>
    /// Serves the storefront's files and adds the single-page fallback, so that a deep link such as
    /// <c>/p/bronze-cleat</c> survives a refresh instead of 404ing on a route only the client knows
    /// about.
    /// <para>
    /// <strong>Call this once, from the host, as <c>app.MapStorefront();</c></strong> — after the
    /// API's own endpoints are mapped. It takes <see cref="WebApplication"/> rather than
    /// <see cref="IEndpointRouteBuilder"/> because the two halves of this feature live on different
    /// sides of that interface and must not be wired separately: serving files is middleware,
    /// falling back to <c>index.html</c> is a routing decision, and a host that installed one
    /// without the other would either 404 every page or serve HTML in place of every asset. One
    /// method that cannot be half-called is worth more than a signature that matches a convention.
    /// </para>
    /// <para>
    /// <strong>Why the fallback is middleware and not <c>MapFallbackToFile</c>.</strong> This host
    /// never calls <c>UseRouting</c> explicitly, so <see cref="WebApplication"/> inserts routing at
    /// the very front of the pipeline — ahead of any middleware added here. A fallback
    /// <em>endpoint</em> matches every path, including <c>/_framework/dotnet.wasm</c>, and the
    /// static-file middleware deliberately stands down whenever an endpoint has already been
    /// selected. The result would be <c>index.html</c> returned for every asset in the application
    /// and a shop that never boots. Written as middleware, the fallback runs only when routing found
    /// nothing, which is precisely when it should.
    /// </para>
    /// <para>
    /// <strong>Development versus a published build.</strong> In development the files are read from
    /// the storefront project's own build output, via the static web assets manifest the SDK writes
    /// on every <c>dotnet build</c> — so <c>dotnet run --project src/VelaCommerce.Api</c> serves a
    /// working shop with no publish step, and an edit to <c>css/app.css</c> shows up on a refresh.
    /// A published build has no such manifest: <c>dotnet publish</c> on the storefront produces one
    /// flat <c>wwwroot</c>, that tree is copied next to the API, and it is found as
    /// <c>{ContentRoot}/wwwroot</c>. <see cref="StorefrontAssets.RootConfigurationKey"/> overrides
    /// both. If none of them resolve, the API serves itself and says so in the log — this host has
    /// to boot with no storefront present, because the build-time OpenAPI generator runs it and
    /// because a deployment may put the storefront on a CDN instead.
    /// </para>
    /// </summary>
    /// <param name="app">The host. Both an <see cref="IApplicationBuilder"/> and an <see cref="IEndpointRouteBuilder"/>, which is the point.</param>
    /// <returns>The same host, so the call chains.</returns>
    public static WebApplication MapStorefront(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var storefront = StorefrontAssets.Locate(app.Environment, app.Configuration, app.Logger);
        if (storefront is null)
        {
            // Already logged, with the list of places that were looked at. Deliberately not an
            // exception: an API with no shop attached is a valid, and in CI a routine, way to run.
            return app;
        }

        app.Logger.LogInformation(
            "Serving the storefront from {Origin}. The shop and the API are one origin, so the demo "
            + "session cookie rides along with cart requests.",
            storefront.Origin);

        var contentTypes = new FileExtensionContentTypeProvider();
        foreach (var (extension, contentType) in AdditionalContentTypes)
        {
            contentTypes.Mappings[extension] = contentType;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = storefront.Provider,
            ContentTypeProvider = contentTypes,

            // Left off on purpose. An extension this host has no mapping for is far more likely to
            // be a mistake than a file worth guessing a type for, and serving it as
            // application/octet-stream is how a source file ends up downloadable.
            ServeUnknownFileTypes = false,

            OnPrepareResponse = static context =>
            {
                var response = context.Context.Response;

                // A response that carries a credential must never be stored by a shared cache. The
                // session middleware mints the cookie on whichever request arrives first, and for a
                // visitor's very first visit that is index.html — exactly the kind of response a CDN
                // would otherwise keep and then hand, Set-Cookie and all, to the next person.
                if (response.Headers.ContainsKey(HeaderNames.SetCookie))
                {
                    response.Headers.CacheControl = "no-store";
                    return;
                }

                // Everything the SDK puts under /_framework carries a content hash in its file name,
                // so a changed file is a changed URL and the old one can be kept forever. Source maps
                // are the exception: they are not fingerprinted, and pinning them for a year makes
                // debugging a redeployed build quietly impossible.
                var path = context.Context.Request.Path;
                var immutable = path.StartsWithSegments("/_framework")
                    && !path.Value!.EndsWith(".map", StringComparison.OrdinalIgnoreCase);

                response.Headers.CacheControl = immutable
                    ? "public, max-age=31536000, immutable"
                    : "public, max-age=0, must-revalidate";
            },
        });

        app.Use(async (context, next) =>
        {
            if (!ShouldServeShell(context, storefront.Provider, out var shell))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";

            // The shell names fingerprinted assets, so it is the one file that must be revalidated
            // every time; caching it is how a browser ends up asking for a build that no longer
            // exists. no-store when it also carries the session cookie, for the reason above.
            context.Response.Headers.CacheControl =
                context.Response.Headers.ContainsKey(HeaderNames.SetCookie)
                    ? "no-store"
                    : "no-cache";

            await context.Response.SendFileAsync(shell, context.RequestAborted);
        });

        return app;
    }

    /// <summary>
    /// Decides whether this request is a client-side route that should be answered with the
    /// application shell.
    /// <para>
    /// Four things disqualify a request, and each one is a bug someone would otherwise have to
    /// diagnose from a page of HTML arriving where they expected JSON:
    /// </para>
    /// <list type="bullet">
    /// <item>Routing already chose an endpoint — the API owns this path, and the fallback must not
    /// shadow it.</item>
    /// <item>The method is not GET or HEAD. A POST to a route that does not exist is a 404 or a 405;
    /// answering it with a document would tell the caller it succeeded.</item>
    /// <item>The path is under a reserved prefix. An unknown <c>/api/…</c> route has no endpoint, so
    /// only this check keeps it from being answered with the shop.</item>
    /// <item>The last segment looks like a file name. A missing asset must 404, or the WebAssembly
    /// loader gets HTML where it expected a module and reports something unrecognisable.</item>
    /// </list>
    /// </summary>
    private static bool ShouldServeShell(HttpContext context, IFileProvider provider, out IFileInfo shell)
    {
        shell = default!;

        if (context.GetEndpoint() is not null)
        {
            return false;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path;
        foreach (var reserved in ReservedPrefixes)
        {
            if (path.StartsWithSegments(reserved))
            {
                return false;
            }
        }

        if (LooksLikeAFile(path))
        {
            return false;
        }

        var candidate = provider.GetFileInfo("index.html");
        if (!candidate.Exists)
        {
            return false;
        }

        shell = candidate;
        return true;
    }

    /// <summary>
    /// True when the last path segment carries an extension. Every route this storefront defines is
    /// a slug — <c>/p/bronze-cleat</c>, <c>/c/deck-hardware</c> — and slugs never contain a dot, so
    /// the test separates "a page we should render" from "a file that is missing" without a list of
    /// extensions to keep up to date.
    /// </summary>
    private static bool LooksLikeAFile(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var lastSlash = value.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? value.AsSpan(lastSlash + 1) : value.AsSpan();

        return lastSegment.Contains('.');
    }
}
