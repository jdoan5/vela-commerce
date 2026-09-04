using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VelaCommerce.Infrastructure.Checkout;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The real API host, in-process, pointed at the test container.
/// <para>
/// The isolation these tests exist to prove is not the work of any single component. The cookie
/// carries the identity, the middleware decrypts it and binds it, the DI scope holds it, and the
/// query filter reads it at translation time. Any one of those links can be reasoned about on its
/// own and still be wrong once the others are attached — a middleware registered after the
/// endpoints, a DbContext resolved from the wrong scope, a filter that quietly stopped applying.
/// So the tests drive <see cref="Program"/> itself through HTTP rather than calling handlers or
/// the DbContext directly: what is under test is the composition, and a test that assembles its
/// own pipeline would be testing an application nobody deploys.
/// </para>
/// <para>
/// Exactly three things are substituted, and each one is substituted because leaving it alone
/// would make the tests measure the developer's machine instead of the code.
/// </para>
/// </summary>
public sealed class DemoSessionHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    /// <summary>
    /// Binds a host to an already-running container.
    /// </summary>
    /// <param name="connectionString">
    /// <see cref="PostgresFixture.ConnectionString"/>. Passed in rather than rediscovered so that
    /// the whole assembly shares one container and one set of migrations; a factory that started
    /// its own would double the slowest thing in the suite and, worse, would let a schema drift
    /// between the two halves of these tests without anything failing.
    /// </param>
    public DemoSessionHost(string connectionString)
    {
        _connectionString = connectionString;

        // https, on a server that never negotiates TLS. The demo ships with Secure on the session
        // cookie, and System.Net.CookieContainer honours that attribute faithfully: over an http
        // base address it would accept the Set-Cookie and then decline to send it back, so every
        // request would look like a brand-new visitor and every isolation test would pass for
        // entirely the wrong reason. Naming the scheme https costs nothing here and keeps the
        // cookie's production flags under test instead of relaxed out of the way.
        ClientOptions.BaseAddress = new Uri("https://localhost/");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Production, not Development, for two independent reasons. It is the configuration the
        // shared demo actually runs under — Secure cookies included — and it skips the host's
        // dev-only migrate-and-seed block, which would parse the 400 KB catalog seed into the
        // container on every host build. The fixture has already migrated; nothing here needs the
        // catalog that seed file describes, because each test writes the two or three rows it
        // depends on and can therefore say what it expects to see.
        builder.UseEnvironment(Environments.Production);

        builder.ConfigureTestServices(services =>
        {
            // Program.cs reads VELA_DB_CONNECTION from the environment BEFORE it consults
            // IConfiguration, so a configuration entry cannot be relied on to win — on a machine
            // where that variable is set, the tests would silently run against the developer's
            // vela_dev database and write carts into it. Replacing the registration is the only
            // override that is not at the mercy of ambient state. The options configuration has to
            // go too, not just the built options: the original delegate throws when no connection
            // string is present at all, which is exactly the state a CI runner is in.
            services.RemoveAll<IDbContextOptionsConfiguration<VelaCommerceDbContext>>();
            services.RemoveAll<DbContextOptions<VelaCommerceDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<VelaCommerceDbContext>();

            // Re-registered with AddDbContext rather than a hand-rolled factory so the context is
            // still activated by the container the same way the real host activates it — which is
            // what supplies the optional ICurrentDemoSession parameter. Constructing it by hand
            // here would quietly test a context that has no accessor at all.
            services.AddDbContext<VelaCommerceDbContext>(options => options.UseNpgsql(_connectionString));

            // An in-memory key ring instead of the default one under ~/.aspnet/DataProtection-Keys.
            // Same cryptography — real AES-CBC plus HMAC, so "a forged cookie does not decrypt"
            // is being measured and not stubbed — but the keys live and die with the host. That
            // buys two things: the suite writes nothing to the developer's home directory, and a
            // session minted by one test's host is unusable by another's, so no test can pass
            // because of a cookie a previous run left behind. Where the real key ring is persisted
            // is a deployment question, and Program.cs already carries the note about it.
            services.RemoveAll<IDataProtectionProvider>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

            // THE REAPER IS SILENCED IN EVERY TEST HOST, FOR THE REASON SettlementHost STATES
            // ABOUT THE OTHER TWO WORKERS: every sweep in this suite should be one a test asked
            // for. It is registered — so the composition stays the real one — and its timer loop
            // returns immediately.
            //
            // Without this it swept the SHARED container on the system clock, on boot and every
            // minute after, from all three hosts at once. Reservations made by other classes
            // expire fifteen minutes out so they were usually safe, but any test that backdates
            // expires_at to make a reservation lapse was racing an uncontrolled third writer.
            services.RemoveAll<ReservationReaperOptions>();
            services.AddSingleton(new ReservationReaperOptions { Enabled = false });
        });
    }

    /// <summary>
    /// A browser. Each call gets its own cookie jar, so two visitors are genuinely two visitors
    /// and neither can accidentally inherit the other's session through a shared handler.
    /// </summary>
    public HttpClient NewBrowser() => CreateClient();

    /// <summary>
    /// A client with no cookie jar at all, for the tests that have to control the
    /// <c>Cookie</c> header by hand — sending none, sending a corrupted one, sending someone
    /// else's. A cookie container would helpfully rewrite or drop those, which is exactly the
    /// helpfulness an attacker would not extend to us.
    /// </summary>
    public HttpClient NewRawClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = ClientOptions.BaseAddress,
        HandleCookies = false,
    });
}
