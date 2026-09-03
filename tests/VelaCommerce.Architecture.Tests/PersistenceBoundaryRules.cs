using Mono.Cecil;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// Where the unit of work is allowed to appear.
/// <para>
/// <see cref="VelaCommerce.Infrastructure.Persistence.VelaCommerceDbContext"/> is the most
/// contagious type in the solution. Once a service, a contract or a helper takes one as a
/// parameter, the transaction boundary stops being a decision and becomes an accident of whoever
/// happened to have a context in scope, and every consumer of that type inherits a database.
/// Confining it to a named list of places keeps "who owns the unit of work" answerable by reading
/// this file.
/// </para>
/// </summary>
public sealed class PersistenceBoundaryRules
{
    private const string DbContextBaseType = "Microsoft.EntityFrameworkCore.DbContext";

    private const string VelaContext = "VelaCommerce.Infrastructure.Persistence.VelaCommerceDbContext";

    /// <summary>The mapping, the migrations and the design-time factory: the layer that owns the model.</summary>
    private const string PersistenceNamespace = "VelaCommerce.Infrastructure.Persistence";

    /// <summary>
    /// <c>CatalogSeeder</c> writes the generated catalog in one transaction, which is a unit-of-work
    /// job even though it is not mapping. Named explicitly rather than admitting all of
    /// Infrastructure, so a new namespace that grabs a context still has to justify itself here.
    /// </summary>
    private const string SeedingNamespace = "VelaCommerce.Infrastructure.Seeding";

    /// <summary>
    /// Endpoint classes take the context as a handler parameter and let DI scope it to the request.
    /// This is the intended shape for a Minimal API; the alternative is a repository layer that
    /// exists only to be passed straight through.
    /// </summary>
    private const string EndpointsNamespace = "VelaCommerce.Api.Endpoints";

    /// <summary>
    /// The composition root. Top-level statements put it in the global namespace, and the compiler
    /// hides its <c>AddDbContext&lt;T&gt;</c> registration and its <c>/health</c> lambda in nested
    /// closure types — which is precisely why this rule reads IL and reports against the outermost
    /// type instead of trusting reflection over declared members.
    /// </summary>
    private const string CompositionRoot = "Program";

    /// <summary>
    /// Rule 3. Nothing outside persistence, seeding, the endpoint classes and the composition root
    /// may so much as name the DbContext. In particular this fails the build if a context ever
    /// reaches an <c>Api.Contracts</c> record — the point at which the storage model and the wire
    /// model quietly become the same thing — or reaches the domain at all.
    /// </summary>
    [Fact]
    public void The_DbContext_does_not_escape_persistence_seeding_the_endpoints_or_the_composition_root()
    {
        var offenders = new List<string>();
        var holders = 0;

        foreach (var assembly in SolutionUnderTest.Production)
        {
            using var module = IlFacts.ReadModule(assembly);

            foreach (var type in module.GetTypes())
            {
                var mentioned = IlFacts.TypesMentionedBy(type);

                if (!mentioned.Contains(VelaContext) && !mentioned.Contains(DbContextBaseType))
                {
                    continue;
                }

                holders++;
                var authored = IlFacts.AuthoredType(type);

                if (!MayHoldTheUnitOfWork(authored))
                {
                    offenders.Add($"{authored.FullName} (in {module.Assembly.Name.Name})");
                }
            }
        }

        // The context is definitely used somewhere. If the IL walk stopped finding it, this rule
        // would pass by looking at nothing at all, which is the failure mode an architecture test
        // can least afford: a green tick over an unenforced boundary.
        Assert.True(
            holders > 0,
            "Found no type mentioning a DbContext anywhere in the solution, which cannot be true. "
            + "IlFacts.TypesMentionedBy has stopped seeing IL, so this rule is not enforcing anything.");

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "A DbContext may only be named by types in "
                + $"'{PersistenceNamespace}' (and its sub-namespaces), '{SeedingNamespace}', "
                + $"'{EndpointsNamespace}', or by the composition root '{CompositionRoot}'. "
                + "Everywhere else, take the data you need as a parameter or a projection so the "
                + "caller keeps control of the transaction.",
                offenders.Distinct(StringComparer.Ordinal)));
        }
    }

    private static bool MayHoldTheUnitOfWork(TypeDefinition authored) =>
        authored.FullName.Equals(CompositionRoot, StringComparison.Ordinal)
        || authored.Namespace.Equals(PersistenceNamespace, StringComparison.Ordinal)
        || authored.Namespace.StartsWith(PersistenceNamespace + ".", StringComparison.Ordinal)
        || authored.Namespace.Equals(SeedingNamespace, StringComparison.Ordinal)
        || authored.Namespace.Equals(EndpointsNamespace, StringComparison.Ordinal)
        || IsBackgroundService(authored);

    /// <summary>
    /// A hosted service is a transaction owner in the same sense an endpoint is.
    /// <para>
    /// The rule exists so business logic cannot reach past its caller and take control of the
    /// unit of work. A <c>BackgroundService</c> has no caller: it creates its own scope,
    /// decides its own batch and commits it, which is exactly the responsibility the allowed
    /// namespaces have. Recognising the base type rather than adding another namespace keeps
    /// the exemption tied to that responsibility — a helper class sitting beside the reaper
    /// still cannot touch a context.
    /// </para>
    /// </summary>
    private static bool IsBackgroundService(TypeDefinition authored)
    {
        for (var current = authored.BaseType; current is not null;)
        {
            if (current.FullName.Equals("Microsoft.Extensions.Hosting.BackgroundService", StringComparison.Ordinal))
                return true;

            var resolved = current.Resolve();
            current = resolved?.BaseType;
        }

        return false;
    }
}
