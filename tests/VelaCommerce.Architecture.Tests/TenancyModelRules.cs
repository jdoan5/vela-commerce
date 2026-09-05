using Microsoft.EntityFrameworkCore;
using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// Every entity that carries a session id must be filtered by it.
/// <para>
/// The <c>DemoTenancy</c> filter is applied by hand, entity by entity, in
/// <c>VelaCommerceDbContext.ApplyDemoTenancy</c>. That list started at two and is now three, and
/// nothing has ever checked that it keeps up. A fourth entity with a <c>DemoSessionId</c> and no
/// filter would compile, migrate, pass every existing test, and show one visitor another visitor's
/// rows — the first symptom being a support question rather than a red build.
/// </para>
/// <para>
/// This rule was written because <see href="../../docs/adr/0007-the-tenancy-filter-fails-closed.md">
/// ADR 0007</see> had to record the absence honestly. The filter's own direction is carefully
/// argued and well tested; what was missing was anything ensuring it is <em>applied</em>.
/// </para>
/// <para>
/// It reads the EF model rather than IL, which is why it lives beside the Cecil rules rather than
/// among them. Building the model needs no database — <c>Model</c> is constructed from the
/// configuration alone — so this stays in the fast suite with no container behind it.
/// </para>
/// </summary>
public sealed class TenancyModelRules
{
    /// <summary>
    /// The property whose presence means "this row belongs to one visitor". Matched by name because
    /// that is how the tenancy is expressed; an entity that stores the session under a different
    /// name would slip past, which is a limitation worth stating rather than a hole worth hiding.
    /// </summary>
    private const string SessionIdProperty = "DemoSessionId";

    /// <summary>
    /// Entities allowed to carry a session id and no filter, each of which must be argued for here
    /// rather than in a reviewer's head. Empty, and it should stay that way: the one genuinely
    /// shared table, <c>stock_items</c>, does not carry a session id at all, so it never reaches
    /// this rule and needs no exemption.
    /// </summary>
    private static readonly string[] MayCarryASessionWithoutBeingFiltered = [];

    [Fact]
    public void Every_entity_with_a_session_id_carries_the_demo_tenancy_filter()
    {
        using var context = ModelOnlyContext();

        var offenders = new List<string>();
        var tenanted = 0;

        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (entity.FindProperty(SessionIdProperty) is null)
            {
                continue;
            }

            tenanted++;

            if (MayCarryASessionWithoutBeingFiltered.Contains(entity.ClrType.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var filters = entity.GetDeclaredQueryFilters();

            if (!filters.Any(filter =>
                    string.Equals(filter.Key, VelaCommerceDbContext.DemoTenancyFilter, StringComparison.Ordinal)))
            {
                offenders.Add(
                    $"{entity.ClrType.Name} has a {SessionIdProperty} and no "
                    + $"'{VelaCommerceDbContext.DemoTenancyFilter}' filter "
                    + $"(it has: {(filters.Count == 0 ? "no named filters" : string.Join(", ", filters.Select(f => f.Key ?? "<anonymous>")))})");
            }
        }

        // The same sanity assertion the Cecil rules carry, for the same reason: if the model stopped
        // reporting entities, this rule would pass by inspecting nothing at all — a green tick over
        // an unenforced boundary, which reads as evidence and is not.
        Assert.True(
            tenanted >= 3,
            $"Found only {tenanted} entities with a {SessionIdProperty}. Cart, Order and the price "
            + "overlay all carry one, so the model is not being read properly and this rule is not "
            + "enforcing anything.");

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                $"An entity with a {SessionIdProperty} belongs to one visitor, so it must be "
                + $"filtered by one. Add it to ApplyDemoTenancy in VelaCommerceDbContext with the "
                + "same fail-closed shape as the others — 'there is a session AND the row belongs "
                + "to it' — so a caller with no session matches nothing rather than everything.",
                offenders));
        }
    }

    /// <summary>
    /// A context built for its model and nothing else. The connection string names a host that does
    /// not exist on purpose: if anything in this rule ever tried to execute a query, it should fail
    /// loudly rather than quietly reach a database somebody happened to be running.
    /// </summary>
    private static VelaCommerceDbContext ModelOnlyContext()
    {
        var options = new DbContextOptionsBuilder<VelaCommerceDbContext>()
            .UseNpgsql("Host=model-only.invalid;Database=none;Username=none")
            .Options;

        // No ICurrentDemoSession. The filter's predicate is compiled into the model either way, and
        // a null accessor is the fail-closed case the whole design is built around.
        return new VelaCommerceDbContext(options);
    }
}
