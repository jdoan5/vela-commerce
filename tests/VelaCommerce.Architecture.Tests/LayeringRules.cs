using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// Which project may point at which. These are the rules that keep the dependency arrows pointing
/// inward, and they are checked against compiled assemblies rather than against the project files:
/// a <c>ProjectReference</c> is an intention, an assembly reference is what the compiler actually
/// believed, and only the second one can make the domain untestable.
/// </summary>
public sealed class LayeringRules
{
    /// <summary>
    /// Everything the domain must never learn about. Matching is by assembly-name prefix, so
    /// <c>Microsoft.EntityFrameworkCore.Abstractions</c> and <c>System.Data.Common</c> are caught
    /// alongside their roots — adding one more EF or ADO.NET assembly cannot slip through on a
    /// name this list has not seen before.
    /// </summary>
    private static readonly string[] ForbiddenInTheDomain =
    [
        "VelaCommerce.Infrastructure",
        "VelaCommerce.Api",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "System.Data",
    ];

    /// <summary>
    /// Rule 1, the load-bearing one. The domain is where the interesting decisions live, and it is
    /// only worth reading if it can be exercised without a database, a web host or a container.
    /// The moment an aggregate can see <c>DbContext</c> or <c>IQueryable</c> over a real provider,
    /// persistence concerns start leaking into invariants and the 161 domain tests stop being
    /// evidence of anything.
    /// </summary>
    [Fact]
    public void The_domain_references_no_other_project_and_no_persistence_or_web_package()
    {
        var offenders = SolutionUnderTest.Domain
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(IsForbiddenInTheDomain)
            .ToList();

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "VelaCommerce.Domain must not reference Infrastructure, the Api, EF Core, Npgsql, "
                + "ASP.NET Core or System.Data. The domain is meant to be portable and testable "
                + "without any of them; move the offending code into Infrastructure and pass the "
                + "result into the domain as a plain value.",
                offenders));
        }
    }

    /// <summary>
    /// Rule 1, stated the strict way round. The previous test names the packages we already know
    /// are dangerous; this one refuses everything that is not the base class library, so the next
    /// tempting dependency — a JSON serialiser, a validation library, a mediator — fails on the
    /// day it is added instead of on the day someone tries to unpick it.
    /// </summary>
    [Fact]
    public void The_domain_compiles_against_the_base_class_library_alone()
    {
        var offenders = SolutionUnderTest.Domain
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => !IsBaseClassLibrary(name))
            .ToList();

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "VelaCommerce.Domain may reference the base class library and nothing else. A "
                + "dependency here is a dependency of every test, every tool and any future host "
                + "that wants to reuse these aggregates.",
                offenders));
        }
    }

    /// <summary>
    /// Rule 2. Dependencies point inward: the Api composes Infrastructure, never the reverse. An
    /// Infrastructure type that reaches back for an Api contract would make the persistence layer
    /// unusable from the seed tool, the migration host or any second front end, and would put the
    /// wire format in charge of the storage format.
    /// </summary>
    [Fact]
    public void Infrastructure_does_not_depend_on_the_Api()
    {
        // Two complementary checks. The manifest catches a project reference that exists at all;
        // the type graph catches the usage that made the compiler emit it, and names the type.
        var referenced = SolutionUnderTest.Infrastructure
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => name.StartsWith("VelaCommerce.Api", StringComparison.Ordinal))
            .ToList();

        if (referenced.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "VelaCommerce.Infrastructure must not reference VelaCommerce.Api. Dependencies "
                + "point inward: the host wires up persistence, persistence knows nothing of the host.",
                referenced));
        }

        IObjectProvider<IType> apiTypes = Types()
            .That().ResideInAssembly(SolutionUnderTest.Api)
            .As("the Api");

        // ArchUnitNET's positive-evaluation guard covers the rule's SUBJECT, not its OBJECT.
        // Pointed at an empty object set, NotDependOnAny renders as "not depend on any of no
        // types (always true)" and passes. Assert the target set is real, or a renamed Api
        // assembly would turn this rule into a green no-op.
        Assert.NotEmpty(apiTypes.GetObjects(SolutionUnderTest.Graph));

        Types()
            .That().ResideInAssembly(SolutionUnderTest.Infrastructure)
            .Should().NotDependOnAny(apiTypes)
            .Because(
                "dependencies point inward. Infrastructure is composed by the Api, so a type here "
                + "that needs something from the Api is describing a dependency that belongs in "
                + "the Api instead.")
            .Check(SolutionUnderTest.Graph);
    }

    private static bool IsForbiddenInTheDomain(string assemblyName) =>
        ForbiddenInTheDomain.Any(forbidden =>
            assemblyName.Equals(forbidden, StringComparison.Ordinal)
            || assemblyName.StartsWith(forbidden + ".", StringComparison.Ordinal));

    /// <summary>
    /// The runtime's own assemblies. Note that this deliberately admits <c>System.Data.*</c>,
    /// which <see cref="ForbiddenInTheDomain"/> then bans: ADO.NET ships in the box but is still
    /// persistence, so it takes both tests to describe the rule honestly.
    /// </summary>
    private static bool IsBaseClassLibrary(string assemblyName) =>
        assemblyName is "System" or "netstandard" or "mscorlib"
        || assemblyName.StartsWith("System.", StringComparison.Ordinal);
}
