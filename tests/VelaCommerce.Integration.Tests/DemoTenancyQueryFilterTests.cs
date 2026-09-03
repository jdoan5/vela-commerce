using Microsoft.EntityFrameworkCore;
using VelaCommerce.Domain.Carts;
using VelaCommerce.Domain.Common;
using VelaCommerce.Infrastructure.Persistence;
using VelaCommerce.Infrastructure.Tenancy;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The filter itself, one layer below HTTP.
/// <para>
/// <see cref="SessionIsolationTests"/> proves the composed application does not leak. These tests
/// prove the reason it does not, which is a different claim and a more durable one: the
/// restriction is a property of the model, so a handler written next year that forgets to say
/// "where this cart is mine" inherits it anyway. Two things follow that only a test at this level
/// can see. A caller with no session sees nothing rather than everything — the direction of that
/// default is the entire security property, and no request over HTTP can produce the unbound state
/// to check it. And the session id is bound per query rather than baked into the model EF caches
/// once per context type, which is the failure that would hand one visitor's id to every visitor
/// and would leave every row-level assertion in this suite green.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DemoTenancyQueryFilterTests(PostgresFixture fixture)
{
    /// <summary>
    /// A stand-in for the real scoped holder, which is internal to the Infrastructure assembly on
    /// purpose so that nothing outside the composition root can mint an ambient override. The
    /// interface is the seam these tests are entitled to use, and a fixed value is all they need:
    /// the binding rules themselves belong to the middleware and are exercised through HTTP.
    /// </summary>
    private sealed class FixedSession(Guid? sessionId) : ICurrentDemoSession
    {
        public Guid? SessionId { get; } = sessionId;
    }

    private DbContextOptions<VelaCommerceDbContext> Options =>
        new DbContextOptionsBuilder<VelaCommerceDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    private VelaCommerceDbContext ContextFor(Guid? sessionId) => new(Options, new FixedSession(sessionId));

    /// <summary>
    /// A context built the way migrations and design-time tooling build one: no accessor at all,
    /// not merely an accessor holding null. The two reach the same place, and both are worth
    /// checking, because the optional constructor parameter is what makes the second possible and
    /// an argument-count change could silently turn it into the first.
    /// </summary>
    private VelaCommerceDbContext ContextWithNoAccessor() => new(Options);

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The same rows, the same connection, two sessions — and two different answers.
    /// </summary>
    [Fact]
    public async Task Two_sessions_reading_one_database_each_see_only_their_own_cart()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        var annaSku = await WriteCartAsync(anna, quantity: 2);
        var borisSku = await WriteCartAsync(boris, quantity: 5);

        await using (var db = ContextFor(anna))
        {
            var carts = await db.Carts.Include(cart => cart.Lines).ToListAsync();
            Assert.All(carts, cart => Assert.Equal(anna, cart.DemoSessionId));
            Assert.Contains(carts, cart => cart.Lines.Any(line => line.Sku == annaSku));
            Assert.DoesNotContain(carts, cart => cart.Lines.Any(line => line.Sku == borisSku));
        }

        await using (var db = ContextFor(boris))
        {
            var carts = await db.Carts.Include(cart => cart.Lines).ToListAsync();
            Assert.All(carts, cart => Assert.Equal(boris, cart.DemoSessionId));
            Assert.Contains(carts, cart => cart.Lines.Any(line => line.Sku == borisSku));
            Assert.DoesNotContain(carts, cart => cart.Lines.Any(line => line.Sku == annaSku));
        }
    }

    /// <summary>
    /// No session, no rows — the assertion the whole design is arranged around.
    /// <para>
    /// The predicate could have been written "no session, therefore no restriction", which reads
    /// more naturally and is exactly backwards: it would turn every path that never establishes a
    /// session — a background job, a migration, a misordered pipeline, a test harness — into a
    /// read of every visitor's carts at once. An empty screen is a bug report; a stranger's cart
    /// is an incident. This test is what keeps the arrow pointing the right way.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_context_with_no_session_sees_no_carts_at_all()
    {
        var anna = Guid.CreateVersion7();
        await WriteCartAsync(anna, quantity: 3);

        // Rows exist. That is the premise, and it is worth establishing rather than assuming,
        // because "no session sees nothing" is trivially true of an empty table.
        await using (var db = ContextFor(anna))
        {
            Assert.True(await db.Carts.AnyAsync());
        }

        // An accessor that was never bound: the shape of a scope where the middleware did not run.
        await using (var db = ContextFor(null))
        {
            Assert.Empty(await db.Carts.ToListAsync());
            Assert.Equal(0, await db.Carts.CountAsync());
            Assert.False(await db.Carts.AnyAsync());
        }

        // No accessor at all: the shape of `new VelaCommerceDbContext(options)`.
        await using (var db = ContextWithNoAccessor())
        {
            Assert.Empty(await db.Carts.ToListAsync());
            Assert.False(await db.Carts.AnyAsync());
        }
    }

    /// <summary>
    /// Orders are tenanted too, and are checked separately rather than assumed to follow the cart.
    /// <para>
    /// Tenancy is applied per entity, so "carts are filtered" says nothing about any other table.
    /// Orders are the half a leak would matter more in — a cart is a shopping list, an order has a
    /// name and a shipping address on it — and they are also where the next entity gets added,
    /// which is when somebody forgets. Written with raw SQL so the row exists regardless of what
    /// the checkout code currently does or does not do.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Orders_are_filtered_by_session_as_well_as_carts()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        var annaOrder = await WriteOrderAsync(anna);
        var borisOrder = await WriteOrderAsync(boris);

        await using (var db = ContextFor(anna))
        {
            var numbers = await db.Orders.Select(order => order.OrderNumber).ToListAsync();
            Assert.Contains(annaOrder, numbers);
            Assert.DoesNotContain(borisOrder, numbers);
        }

        await using (var db = ContextFor(null))
        {
            Assert.False(await db.Orders.AnyAsync());
        }
    }

    /// <summary>
    /// Standing outside one filter does not stand you outside the other.
    /// <para>
    /// This is why the two are named. An admin view that wants soft-deleted rows asks for
    /// <c>IgnoreQueryFilters(["SoftDelete"])</c> and keeps its tenancy; if the two had been merged
    /// into a single anonymous filter, that same request would quietly have become a read across
    /// every visitor, and it would have looked like a perfectly ordinary line of code. Both
    /// directions are asserted, so the escape hatch is documented as working too — an opt-out that
    /// silently did nothing would be its own kind of trap.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Suppressing_soft_delete_leaves_tenancy_in_force()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        await WriteCartAsync(anna, quantity: 1);
        var borisSku = await WriteCartAsync(boris, quantity: 1);

        await using var db = ContextFor(anna);

        var withDeleted = await db.Carts
            .IgnoreQueryFilters(["SoftDelete"])
            .Include(cart => cart.Lines)
            .ToListAsync();

        Assert.NotEmpty(withDeleted);
        Assert.All(withDeleted, cart => Assert.Equal(anna, cart.DemoSessionId));
        Assert.DoesNotContain(withDeleted, cart => cart.Lines.Any(line => line.Sku == borisSku));

        // The deliberate way out, for the nightly demo reset and for operators. Named, greppable,
        // and impossible to arrive at by accident.
        var everyone = await db.Carts
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Include(cart => cart.Lines)
            .ToListAsync();

        Assert.Contains(everyone, cart => cart.DemoSessionId == boris);
    }

    /// <summary>
    /// One cached statement, a different id bound to it each time.
    /// <para>
    /// EF builds the model — query filters included — once per context type and reuses it for the
    /// life of the process, which is why the predicate reads an instance member of the context
    /// instead of a captured local. Get that wrong and the id is fixed when the model is built,
    /// and every visitor afterwards is filtered by the first visitor's session.
    /// </para>
    /// <para>
    /// The row-level tests above do catch that mistake — measured, by making it: rewriting the
    /// predicate to close over a local turned thirteen tests in this project red. This test earns
    /// its place by pinning the mechanism rather than the symptom, and by catching the neighbouring
    /// mistake the row-level tests cannot see at all. A filter that inlined the id as a SQL literal
    /// would isolate visitors perfectly and still be wrong twice over: a separate query plan per
    /// visitor, and every visitor's session id written into the database's statement logs, where it
    /// is a credential sitting in a file with the wrong audience.
    /// </para>
    /// <para>
    /// <c>ToQueryString</c> gives both halves — a comment preamble declaring the parameters and
    /// their values, then the statement — so the two halves are asserted separately. The statement
    /// must be identical between the two sessions and free of either id; the preamble must carry
    /// each session's own id and not the other's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_session_id_is_bound_per_query_rather_than_baked_into_the_cached_model()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        await WriteCartAsync(anna, quantity: 1);
        await WriteCartAsync(boris, quantity: 1);

        string annaSql;
        string borisSql;

        await using (var db = ContextFor(anna))
        {
            annaSql = db.Carts.ToQueryString();
        }

        await using (var db = ContextFor(boris))
        {
            borisSql = db.Carts.ToQueryString();
        }

        // One statement, therefore one plan, therefore no per-visitor model.
        Assert.Equal(StatementOf(annaSql), StatementOf(borisSql));

        // ... and no session id inlined into it.
        foreach (var session in new[] { anna, boris })
        {
            Assert.DoesNotContain(session.ToString("D"), StatementOf(annaSql), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(session.ToString("N"), StatementOf(annaSql), StringComparison.OrdinalIgnoreCase);
        }

        // The part that a stale captured value would fail: each execution binds its own visitor.
        Assert.Contains(anna.ToString("D"), ParametersOf(annaSql), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(boris.ToString("D"), ParametersOf(annaSql), StringComparison.OrdinalIgnoreCase);

        Assert.Contains(boris.ToString("D"), ParametersOf(borisSql), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(anna.ToString("D"), ParametersOf(borisSql), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A primary key is not a way around the filter.
    /// <para>
    /// The cart's key never reaches a client — the API publishes no cart id at all — but ids do
    /// escape by other routes, and the guarantee worth having is that possessing one is simply not
    /// useful. Note which operators are in play: <c>Where(c =&gt; c.Id == ...)</c> composes onto
    /// the filtered set, so the filter is still there and the answer is null.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cart_stays_invisible_to_another_session_even_when_asked_for_by_its_key()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        await WriteCartAsync(anna, quantity: 1);
        var annaCartId = await CartIdOfAsync(anna);

        await using var db = ContextFor(boris);

        Assert.Null(await db.Carts.FirstOrDefaultAsync(cart => cart.Id == annaCartId));
        Assert.Null(await db.Carts.SingleOrDefaultAsync(cart => cart.Id == annaCartId));
        Assert.False(await db.Carts.AnyAsync(cart => cart.Id == annaCartId));
    }

    /// <summary>
    /// What <c>Find</c> does, measured — because the answer has two halves and only one of them is
    /// reassuring.
    /// <para>
    /// On a cold context <c>FindAsync</c> composes a query, so the filter applies and another
    /// session's cart comes back null. But <c>Find</c> is defined to answer from the change
    /// tracker when it can, and a tracked entity is returned without any query being composed —
    /// so if something earlier in the same unit of work loaded a foreign row with
    /// <c>IgnoreQueryFilters</c>, a later <c>Find</c> hands it over. That is not a bug in EF; it
    /// is what <c>Find</c> is for. It is the reason
    /// <c>CartEndpoints.LoadCartForWriteAsync</c> uses <c>FirstOrDefaultAsync</c>, and writing the
    /// behaviour down as a test is how that reason survives the next person who notices that
    /// <c>Find</c> would be shorter.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Find_applies_the_filter_on_a_cold_context_but_returns_what_is_already_tracked()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        await WriteCartAsync(anna, quantity: 1);
        var annaCartId = await CartIdOfAsync(anna);

        await using (var db = ContextFor(boris))
        {
            Assert.Null(await db.Carts.FindAsync(annaCartId));
        }

        await using (var db = ContextFor(boris))
        {
            // The explicit opt-out, which is allowed and visible. It also puts Anna's cart into
            // this context's change tracker.
            await db.Carts
                .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
                .FirstAsync(cart => cart.Id == annaCartId);

            // Now Find answers from memory and never asks the database, so no filter is involved.
            Assert.NotNull(await db.Carts.FindAsync(annaCartId));

            // A query, by contrast, is still filtered — the tracked instance does not make the
            // foreign row visible to LINQ.
            Assert.Null(await db.Carts.FirstOrDefaultAsync(cart => cart.Id == annaCartId));
        }
    }

    /// <summary>
    /// The boundary of the guarantee, asserted so it cannot be mistaken for a bug later.
    /// <para>
    /// A query filter restricts <em>reads</em>. Nothing here stops a context bound to one session
    /// from inserting a row stamped with another's, and no amount of filtering could: the row does
    /// not exist yet, so there is nothing to filter. That is why every write path takes the owner
    /// from <see cref="ICurrentDemoSession"/> and never from the request — see the fail-closed
    /// branch in <c>CartEndpoints.AddItemAsync</c>, which refuses rather than guessing when there
    /// is no session to take it from. This test exists so that reading the filter and concluding
    /// "writes are covered too" is contradicted by something that runs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Writes_are_not_filtered_which_is_why_the_owner_comes_from_the_session_and_not_the_request()
    {
        var anna = Guid.CreateVersion7();
        var boris = Guid.CreateVersion7();

        await using (var db = ContextFor(anna))
        {
            // A context that believes it is Anna, writing a cart owned by Boris. The database
            // accepts it without complaint.
            db.Carts.Add(new Cart(boris));
            await db.SaveChangesAsync();
        }

        await using (var db = ContextFor(anna))
        {
            Assert.DoesNotContain(await db.Carts.ToListAsync(), cart => cart.DemoSessionId == boris);
        }

        await using (var db = ContextFor(boris))
        {
            Assert.Contains(await db.Carts.ToListAsync(), cart => cart.DemoSessionId == boris);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Plumbing.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes one cart with one line for the given session and returns the line's SKU, which is
    /// what the assertions use to tell one visitor's cart from another's.
    /// </summary>
    private async Task<string> WriteCartAsync(Guid sessionId, int quantity)
    {
        await using var db = ContextFor(sessionId);

        var sku = $"TEN-{Guid.CreateVersion7():N}"[..20].ToUpperInvariant();
        var cart = new Cart(sessionId);
        cart.AddItem(Guid.CreateVersion7(), sku, "Tenancy fixture line", new Money(1_500), quantity);

        db.Carts.Add(cart);
        await db.SaveChangesAsync();

        return sku;
    }

    /// <summary>Reads a session's cart id from outside the filter, the way an attacker who had
    /// obtained one by some other route would already know it.</summary>
    private async Task<Guid> CartIdOfAsync(Guid sessionId)
    {
        await using var db = ContextWithNoAccessor();

        return await db.Carts
            .IgnoreQueryFilters([VelaCommerceDbContext.DemoTenancyFilter])
            .Where(cart => cart.DemoSessionId == sessionId)
            .Select(cart => cart.Id)
            .SingleAsync();
    }

    /// <summary>
    /// Inserts a minimal placed order for a session and returns its order number. Raw SQL rather
    /// than the domain, so this test does not become a test of whatever checkout looks like today.
    /// </summary>
    private async Task<string> WriteOrderAsync(Guid sessionId)
    {
        const string emptyJson = "{}";

        // Guid.NewGuid, not Guid.CreateVersion7, and this is not a style preference. A v7 guid
        // leads with its 48-bit millisecond timestamp, so the first 11 hex characters of two v7
        // guids minted in the same millisecond are identical — and 11 characters is exactly what
        // survives the truncation to the column's 16. Two orders written back to back therefore
        // collide on ux_orders_order_number rather than on whatever the test was asking about.
        // Measured here, not theorised: this test failed that way on its first run.
        var orderNumber = $"VELA-{Guid.NewGuid():N}"[..16];

        await using var db = ContextWithNoAccessor();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO orders (id, demo_session_id, order_number, idempotency_key, status, currency,
                                shipping_address, placed_at, shipping_amount, shipping_currency,
                                tax_amount, tax_currency, captured_amount, captured_currency,
                                refunded_amount, refunded_currency)
            VALUES (gen_random_uuid(), {sessionId}, {orderNumber}, {$"key-{Guid.CreateVersion7():N}"}, 0, 'USD',
                    {emptyJson}::jsonb, now(), 0, 'USD', 0, 'USD', 0, 'USD', 0, 'USD')
            """);

        return orderNumber;
    }

    /// <summary>
    /// The SQL EF would send, without the parameter-declaration comments it prepends. Those lines
    /// carry the bound values, so leaving them in would compare the ids rather than the statement
    /// — the opposite of what the statement half of the assertion is asking.
    /// </summary>
    private static string StatementOf(string queryString) =>
        SplitQueryString(queryString, comments: false);

    /// <summary>The comment preamble alone: the parameter names and the values bound to them.</summary>
    private static string ParametersOf(string queryString) =>
        SplitQueryString(queryString, comments: true);

    private static string SplitQueryString(string queryString, bool comments) =>
        string.Join(
            '\n',
            queryString
                .Split('\n')
                .Where(line => line.TrimStart().StartsWith("--", StringComparison.Ordinal) == comments));
}
