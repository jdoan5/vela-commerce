using System.Reflection;
using ArchUnitNET.Loader;
using VelaCommerce.Domain.Common;
using VelaCommerce.Infrastructure.Persistence;
using ArchitectureGraph = ArchUnitNET.Domain.Architecture;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// The three production assemblies, plus the ArchUnitNET type graph built from them.
/// <para>
/// The assemblies are reached through a type rather than by name — <c>typeof(Entity).Assembly</c>
/// instead of <c>Assembly.Load("VelaCommerce.Domain")</c> — so that renaming a project breaks the
/// compile here rather than silently reducing these rules to no-ops against an assembly that no
/// longer exists. An architecture test that cannot find its subject must fail loudly, not pass.
/// </para>
/// <para>
/// The graph is built once in a static initialiser because loading three assemblies through
/// Mono.Cecil costs more than every rule in this project put together, and no rule mutates it.
/// </para>
/// </summary>
internal static class SolutionUnderTest
{
    /// <summary>The aggregates. Nothing in the solution is allowed to point at this one's dependencies.</summary>
    internal static readonly Assembly Domain = typeof(Entity).Assembly;

    /// <summary>EF Core, Npgsql, mapping and seeding.</summary>
    internal static readonly Assembly Infrastructure = typeof(VelaCommerceDbContext).Assembly;

    /// <summary>The Minimal API host. <c>Program</c> is the composition root and lives in the global namespace.</summary>
    internal static readonly Assembly Api = typeof(Program).Assembly;

    /// <summary>
    /// The ArchUnitNET view of all three assemblies. Rules stated against this see the real type
    /// graph — base types, member signatures and the IL of method bodies — not just the manifest.
    /// </summary>
    internal static readonly ArchitectureGraph Graph = new ArchLoader()
        .LoadAssemblies(Domain, Infrastructure, Api)
        .Build();

    /// <summary>Every production assembly, for rules that hold across the whole solution.</summary>
    internal static IReadOnlyList<Assembly> Production { get; } = [Domain, Infrastructure, Api];

    /// <summary>
    /// Formats a rule violation so the failure output names the rule first and then every
    /// offending type, one per line. A reviewer reading a red build should not have to open this
    /// project to find out what broke or why the rule exists.
    /// </summary>
    internal static string Explain(string rule, IEnumerable<string> offenders) =>
        string.Join(
            Environment.NewLine,
            [rule, "Offenders:", .. offenders.Order(StringComparer.Ordinal).Select(static o => "  - " + o)]);
}
