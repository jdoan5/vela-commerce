using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace VelaCommerce.Api.Hosting;

/// <summary>
/// The response headers that make a public, unsupervised demo safe to leave on the internet: a
/// Content-Security-Policy that actually permits Blazor WebAssembly, plus the small set of headers
/// that cost nothing and close whole categories of attack.
/// <para>
/// <strong>The governing rule here is that a policy which breaks the shop is worse than no policy
/// at all.</strong> A blocked <c>.wasm</c> is a blank page; a blocked import map is a blank page.
/// So every directive below was chosen against what this application actually loads, the hashes
/// are computed from the real shell rather than guessed, and every failure to compute them ends in
/// "send no policy and say so in the log" rather than "send a policy and hope".
/// </para>
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// Finds every inline <c>&lt;script&gt;</c> in the shell — one with no <c>src</c> attribute —
    /// and captures its exact text content, which is what a CSP hash is computed over.
    /// <para>
    /// A regex rather than an HTML parser, and that is a deliberate limit rather than a shortcut:
    /// the input is one file emitted by the .NET SDK from a template in this repository, not
    /// arbitrary markup, and the alternative is a parser dependency in a host that has none.
    /// Non-greedy so two script elements do not merge into one, and single-line so a multi-line
    /// import map is captured whole.
    /// </para>
    /// </summary>
    private static readonly Regex ScriptElements = new(
        @"<script\b(?<attributes>[^>]*)>(?<body>.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>Matches a <c>src</c> attribute, which is what separates an external script from an inline one.</summary>
    private static readonly Regex HasSourceAttribute = new(
        @"(^|\s)src\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>The shell whose inline scripts have to be permitted by hash.</summary>
    private const string EntryFile = "index.html";

    /// <summary>
    /// Headers sent on every response, document or not. Values are constants because none of them
    /// depends on what was served.
    /// </summary>
    private static readonly (string Name, string Value)[] AlwaysSent =
    [
        // Without this, a browser is free to guess that a JSON error body is HTML and run script
        // out of it. The API answers application/problem+json on every failure path, so this is the
        // header that keeps those answers inert.
        ("X-Content-Type-Options", "nosniff"),

        // no-referrer rather than the modern strict-origin-when-cross-origin default, because of
        // one specific URL: a confirmation link carries a signed retrieval token in its query
        // string (GET /api/orders/{number}?token=...). Any policy that sends a full URL anywhere
        // hands that capability to whoever receives the referrer - and this page loads a
        // stylesheet from a third-party font host on every visit. Nothing in this application
        // reads a referrer, so there is nothing to trade away.
        ("Referrer-Policy", "no-referrer"),

        // frame-ancestors below is the modern spelling and the one browsers honour; this is the
        // legacy header for anything that predates CSP2. Both say the same thing, and saying it
        // twice costs 22 bytes.
        ("X-Frame-Options", "DENY"),

        // A page opened from this one cannot reach back through window.opener. Nothing here opens
        // popups, which is precisely why the restriction is free.
        ("Cross-Origin-Opener-Policy", "same-origin"),

        // A shop needs none of these. Declaring the denial is what stops a future dependency
        // quietly asking a reviewer for their camera.
        ("Permissions-Policy",
            "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), "
            + "geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), usb=()"),
    ];

    /// <summary>
    /// Builds the policy this host will send, reading the storefront's shell if there is one.
    /// <para>
    /// Called once, while the pipeline is being built, so the file is read and hashed a single time
    /// rather than per request. That also means a rebuild of the storefront needs a restart of this
    /// host before its new import map is permitted — which is already true of
    /// <see cref="StorefrontAssets"/>, whose manifest is read at the same moment.
    /// </para>
    /// <para>
    /// Never throws. The build-time OpenAPI generator executes this host's entry point, and CI runs
    /// the API with no storefront beside it at all.
    /// </para>
    /// </summary>
    public static SecurityHeaderPolicySource Build(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger logger)
    {
        // NullLogger on purpose: MapStorefront runs the same probe a few lines later and logs what
        // it found. Two identical "no storefront here" warnings in a startup log would read as two
        // problems.
        var storefront = StorefrontAssets.Locate(environment, configuration, NullLogger.Instance);

        if (storefront is null)
        {
            // An API with no shop attached serves no documents, so there is no document policy to
            // send and nothing is lost. Info, not warning: MapStorefront already says this loudly.
            logger.LogInformation(
                "No storefront found, so no Content-Security-Policy is composed. The API's own "
                + "responses are not documents and carry the remaining security headers.");

            return SecurityHeaderPolicySource.None;
        }

        // The SAME file provider MapStorefront serves the shell from, which is the property that
        // makes this correct rather than approximately correct: whatever bytes a browser is about
        // to receive as index.html are the bytes that were hashed, whether they came from a
        // published wwwroot, from an obj/ manifest entry, or from a path in configuration.
        return new SecurityHeaderPolicySource(storefront.Provider, storefront.Origin, logger);
    }

    /// <summary>
    /// Attaches the headers to a response that has not started yet.
    /// <para>
    /// Registered as an <c>OnStarting</c> callback rather than written immediately, for two
    /// reasons. The <c>Content-Type</c> is not known when this middleware runs — it is set by
    /// whatever eventually answers, which for the shell is a fallback further down the pipeline —
    /// and the policy differs between a document and everything else. And a callback runs on every
    /// path out of the pipeline, including the exception handler's, so an error page cannot escape
    /// the headers by being produced somewhere unusual.
    /// </para>
    /// </summary>
    public static void Apply(HttpContext context, SecurityHeaderPolicySource source)
    {
        var response = context.Response;

        foreach (var (name, value) in AlwaysSent)
        {
            // Never overwrite. A handler that set one of these deliberately knows something this
            // middleware does not.
            if (!response.Headers.ContainsKey(name))
            {
                response.Headers[name] = value;
            }
        }

        // CSP GOVERNS DOCUMENTS AND WORKERS, AND THIS APPLICATION SERVES EXACTLY ONE DOCUMENT.
        //
        // Sending the policy on the API's JSON is not a second layer of defence, it is noise: a
        // response a browser never treats as a document has no CSP to enforce. Worse, a
        // default-src that reached a worker would govern that worker's fetches, and the Blazor
        // runtime is entitled to create one. So the policy goes on text/html and nowhere else, and
        // the JSON is kept inert by nosniff plus its own content type instead.
        //
        // This test is also what keeps the freshness check in Current() cheap: it is reached once
        // per page view, not once per WebAssembly module.
        if (!IsDocument(response.ContentType))
        {
            return;
        }

        if (source.Current() is { Length: > 0 } csp && !response.Headers.ContainsKey("Content-Security-Policy"))
        {
            response.Headers["Content-Security-Policy"] = csp;
        }
    }

    /// <summary>True when the response will be parsed as a document by a browser.</summary>
    private static bool IsDocument(string? contentType) =>
        contentType is { Length: > 0 }
        && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The policy itself, one directive per line so a reviewer can read why each exists.
    /// </summary>
    private static string Compose(IReadOnlyList<string> inlineScriptHashes)
    {
        var script = new StringBuilder("script-src 'self' 'wasm-unsafe-eval'");
        foreach (var hash in inlineScriptHashes)
        {
            script.Append(" '").Append(hash).Append('\'');
        }

        return string.Join(
            "; ",
            [
                // The floor. Everything not named below falls back to same-origin only.
                "default-src 'self'",

                // The document contains <base href="/">, which is same-origin, so 'self' permits
                // the one base element that exists and forbids an injected one from repointing
                // every relative URL in the application at another host.
                "base-uri 'self'",

                // No <object>, <embed> or <applet> anywhere, now or intentionally.
                "object-src 'none'",

                // Nobody frames this shop. Clickjacking a "reset my demo data" button is a small
                // prank; the directive is free either way.
                "frame-ancestors 'none'",

                // 'self' rather than 'none', and the distinction is worth a sentence. The checkout
                // page renders a real <form> whose submit is cancelled in the component, so under
                // normal operation nothing is ever posted anywhere and 'none' would also work. It
                // is not used, because the failure mode is asymmetric: if that handler ever stops
                // cancelling - a JavaScript error before the runtime attaches, a future refactor -
                // 'none' turns a working checkout into a page whose button silently does nothing,
                // while 'self' lets it post to its own origin, where there is no HTML form
                // endpoint to accept it and the failure is visible. What both forbid is the thing
                // worth forbidding: an injected form posting an address to somebody else's host.
                "form-action 'self'",

                // 'wasm-unsafe-eval' IS THE ONE DIRECTIVE THIS APPLICATION CANNOT DO WITHOUT.
                // Compiling a WebAssembly module counts as evaluation, so without it the runtime
                // never starts and the page stays on its boot skeleton forever. It is much narrower
                // than 'unsafe-eval': it permits WebAssembly compilation and nothing else - no
                // eval(), no new Function(), no string-to-code path a script injection could use.
                //
                // The hashes cover the SDK-generated inline import map in index.html, whose content
                // changes with every build because it carries asset fingerprints. Hashing it at
                // startup is what lets that be true without 'unsafe-inline' - see
                // TryHashInlineScripts, and note that a nonce is not available here because the
                // shell is served as a static file rather than rendered per request.
                script.ToString(),

                // 'unsafe-inline' is here and it is a genuine concession, so it is worth being
                // precise about what it does and does not permit. It covers inline STYLE, not
                // inline script: the storefront sets element widths from data in a handful of
                // places (the header's nav placeholders, the boot skeleton), and a style attribute
                // cannot be hashed the way a script element can. With script-src locked down, the
                // residual risk is a CSS injection that could restyle the page or leak coarse
                // information through selector-driven background images - not code execution. The
                // font host is named because index.html loads a stylesheet from it.
                "style-src 'self' https://fonts.googleapis.com 'unsafe-inline'",

                // The stylesheet above fetches its font files from a second Google host.
                "font-src 'self' https://fonts.gstatic.com",

                // data: is required by app.css, which draws the select control's chevron as an
                // inline SVG data URI rather than shipping a file for it. Everything else is local:
                // there are no third-party images and no remote product photography, because the
                // catalog draws its own motifs.
                "img-src 'self' data:",

                // The cart, the checkout and the catalog snapshot are all same-origin - which is
                // the whole point of this host serving the storefront. A cross-origin API would
                // need naming here, and would also need a different session cookie policy.
                "connect-src 'self'",

                // No web manifest is linked today. Naming the directive means adding one later is a
                // deliberate act rather than a silent inheritance from default-src.
                "manifest-src 'self'",

                // Blazor WebAssembly is single-threaded in this build and creates no worker. If a
                // future build enables threading, this is the directive that will need blob:.
                "worker-src 'self'",
            ]);
    }

    /// <summary>
    /// Composes the policy for one particular copy of the shell. Internal so
    /// <see cref="SecurityHeaderPolicySource"/> can call it when the file underneath it changes.
    /// </summary>
    internal static string? TryCompose(IFileProvider provider, string origin, ILogger logger)
    {
        var hashes = TryHashInlineScripts(provider, logger);

        if (hashes is null)
        {
            // THE FAIL-OPEN BRANCH, AND THE ONE PLACE THIS FILE DELIBERATELY GIVES UP SECURITY TO
            // KEEP THE SHOP WORKING. The shell contains an inline import map that the .NET SDK
            // regenerates - with different content - on every build. Without its hash, script-src
            // blocks it and the WebAssembly runtime never resolves a single module: a blank page,
            // in production, with a console error most visitors will never open. A demo nobody can
            // load teaches nothing. So an unreadable shell means no policy and a warning naming the
            // file, rather than a policy and a broken shop.
            logger.LogWarning(
                "Could not read {EntryFile} from {Origin} to hash its inline scripts, so no "
                + "Content-Security-Policy will be sent. The shop still works; it is simply "
                + "unprotected by CSP. Check that the storefront build output is intact.",
                EntryFile,
                origin);

            return null;
        }

        logger.LogInformation(
            "Content-Security-Policy composed from {Origin}: {Count} inline script(s) permitted by "
            + "SHA-256 hash, no 'unsafe-inline' in script-src.",
            origin,
            hashes.Count);

        return Compose(hashes);
    }

    /// <summary>
    /// Reads the shell and returns a CSP hash for each inline script in it, or null if the file
    /// cannot be read.
    /// <para>
    /// The hash is SHA-256 over the element's exact text content — the bytes between <c>&gt;</c>
    /// and <c>&lt;/script&gt;</c>, with no trimming, no normalisation and no re-encoding, because
    /// the browser hashes exactly those bytes and one stray newline is the difference between a
    /// working shop and a blank page.
    /// </para>
    /// <para>
    /// An empty result is a valid answer, not a failure: a shell with no inline script needs no
    /// hashes, and the policy is still composed. Null means the file itself could not be read,
    /// which is the case that must not produce a policy.
    /// </para>
    /// </summary>
    private static List<string>? TryHashInlineScripts(IFileProvider provider, ILogger logger)
    {
        string markup;

        try
        {
            var shell = provider.GetFileInfo(EntryFile);
            if (!shell.Exists)
            {
                return null;
            }

            using var stream = shell.CreateReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            markup = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogDebug(exception, "Could not read {EntryFile} while composing the CSP.", EntryFile);
            return null;
        }

        var hashes = new List<string>();

        try
        {
            foreach (Match match in ScriptElements.Matches(markup))
            {
                if (HasSourceAttribute.IsMatch(match.Groups["attributes"].Value))
                {
                    // An external script. 'self' already covers it, and a hash would not apply.
                    continue;
                }

                var body = match.Groups["body"].Value;
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(body));

                hashes.Add($"sha256-{Convert.ToBase64String(digest)}");
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            logger.LogDebug(exception, "Timed out scanning {EntryFile} for inline scripts.", EntryFile);
            return null;
        }

        return hashes;
    }
}

/// <summary>
/// The Content-Security-Policy for document responses, kept in step with the shell it describes.
/// <para>
/// <strong>Why this is not a value computed once at startup.</strong> It was, and it was wrong.
/// The policy names the SHA-256 of the SDK-generated inline import map in <c>index.html</c>, and
/// that file is rewritten with new asset fingerprints on every storefront build. A policy captured
/// at startup therefore describes a shell that may no longer exist, and the symptom is the worst
/// kind: the browser silently refuses the import map, the WebAssembly runtime never resolves a
/// module, and the page sits on its boot skeleton forever with one line in a console nobody has
/// open. It happened during this slice's own verification, on the first republish.
/// </para>
/// <para>
/// So the policy is tied to the file rather than to the process. Each document response checks the
/// shell's last-write time and length; when they move, the policy is recomposed from the same file
/// provider the shell is served from — so the bytes that were hashed are always the bytes that were
/// sent. The check costs one stat per page view, and only per page view: it is reached after the
/// content-type test in <see cref="SecurityHeaders.Apply"/>, so the several dozen WebAssembly
/// modules behind each page never touch it.
/// </para>
/// </summary>
internal sealed class SecurityHeaderPolicySource
{
    /// <summary>The instance used when there is no storefront at all: never a policy, never a stat.</summary>
    public static SecurityHeaderPolicySource None { get; } = new();

    private readonly IFileProvider? _provider;
    private readonly string _origin;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private string? _policy;
    private (DateTimeOffset Modified, long Length) _stamp;
    private bool _read;

    private SecurityHeaderPolicySource()
    {
        _provider = null;
        _origin = string.Empty;
        _logger = NullLogger.Instance;
    }

    public SecurityHeaderPolicySource(IFileProvider provider, string origin, ILogger logger)
    {
        _provider = provider;
        _origin = origin;
        _logger = logger;
    }

    /// <summary>
    /// The policy to send with this document, recomposing first if the shell has changed.
    /// <para>
    /// Locked, because a burst of concurrent page loads after a redeploy would otherwise all decide
    /// to recompose at once and read the same file several times over. Cheap: the fast path takes
    /// the lock, compares two values and returns.
    /// </para>
    /// </summary>
    public string? Current()
    {
        if (_provider is null)
        {
            return null;
        }

        var shell = _provider.GetFileInfo("index.html");
        var stamp = shell.Exists ? (shell.LastModified, shell.Length) : default;

        lock (_gate)
        {
            if (_read && stamp == _stamp)
            {
                return _policy;
            }

            if (_read)
            {
                _logger.LogInformation(
                    "The storefront's index.html changed underneath this host; recomposing the "
                    + "Content-Security-Policy so its inline-script hashes match what is now served.");
            }

            _stamp = stamp;
            _read = true;
            _policy = SecurityHeaders.TryCompose(_provider, _origin, _logger);

            return _policy;
        }
    }
}
