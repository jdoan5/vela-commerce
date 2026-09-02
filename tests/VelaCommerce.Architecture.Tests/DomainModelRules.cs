using System.Reflection;
using ArchUnitNET.xUnit;
using VelaCommerce.Domain.Common;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// The shape every persisted aggregate has to keep.
/// <para>
/// Both rules here exist because breaking them fails somewhere far away from the change. An
/// unsealed entity breaks <see cref="Entity.Equals(object?)"/>, which compares
/// <c>GetType()</c> and so decides a subclass is never equal to its base — a silent duplicate in
/// a change tracker rather than a compile error. A missing parameterless constructor breaks only
/// when EF Core materialises that type from a real query, which is to say in integration tests or
/// in production, never in the 161 domain unit tests.
/// </para>
/// </summary>
public sealed class DomainModelRules
{
    /// <summary>
    /// The concrete aggregates and their children. Held once so both rules can refuse to pass
    /// vacuously: an architecture test that silently matches nothing is worse than no test, because
    /// it looks like coverage on the CI summary.
    /// </summary>
    private static readonly Type[] PersistedEntities = SolutionUnderTest.Domain
        .GetTypes()
        .Where(static type => type.IsClass && !type.IsAbstract && typeof(Entity).IsAssignableFrom(type))
        .OrderBy(static type => type.FullName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Rule 4. <see cref="Entity"/> is abstract on purpose — it is a base, not a thing. Everything
    /// derived from it is sealed, because entity identity here is type-plus-id: a subclass would
    /// compare unequal to its own base instance and quietly split one row into two objects.
    /// Sealing also keeps the aggregates free to change their private shape.
    /// </summary>
    [Fact]
    public void Entities_are_sealed_unless_they_are_the_deliberately_abstract_base()
    {
        Assert.NotEmpty(PersistedEntities);

        Assert.True(
            typeof(Entity).IsAbstract,
            $"{typeof(Entity).FullName} must stay abstract: it carries identity and soft-delete for "
            + "its subclasses and is never a thing in its own right.");

        Classes()
            .That().ResideInAssembly(SolutionUnderTest.Domain)
            .And().AreAssignableTo(typeof(Entity))
            .And().AreNotAbstract()
            .Should().BeSealed()
            .Because(
                "Entity.Equals compares GetType(), so a subclass is never equal to its base. An "
                + "unsealed entity therefore produces two unequal objects for one row instead of a "
                + "compile error. If a type needs variation, model it as a value on the aggregate.")
            .Check(SolutionUnderTest.Graph);
    }

    /// <summary>
    /// Rule 5. EF Core materialises entities without calling a domain constructor, so every mapped
    /// type needs a parameterless one it can reach. Keeping it non-public means the rest of the
    /// codebase still has to go through the real constructor that enforces the invariants — the
    /// convention in this codebase is <c>private Product() { } // EF</c>.
    /// <para>
    /// Without this rule, adding an aggregate compiles, passes every domain test, and then throws
    /// <c>InvalidOperationException</c> the first time a query returns one.
    /// </para>
    /// </summary>
    [Fact]
    public void Entities_keep_a_non_public_parameterless_constructor_for_EF_materialisation()
    {
        Assert.NotEmpty(PersistedEntities);

        var offenders = PersistedEntities
            .Where(static type => ParameterlessConstructor(type) is not { IsPublic: false })
            .Select(static type => type.FullName ?? type.Name)
            .ToList();

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "Every type derived from Entity needs a non-public parameterless constructor so EF "
                + "Core can materialise it, and only a non-public one, so application code still has "
                + "to use the constructor that enforces the invariants. Add `private TypeName() { } "
                + "// EF`.",
                offenders));
        }
    }

    private static ConstructorInfo? ParameterlessConstructor(Type type) =>
        type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
}
