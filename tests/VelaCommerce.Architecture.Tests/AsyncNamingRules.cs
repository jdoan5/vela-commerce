using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// The one naming convention worth failing a build over.
/// <para>
/// A method that returns a <see cref="Task"/> can be called without <c>await</c> and the compiler
/// will only warn, so the cost of getting this wrong is a fire-and-forget call that swallows its
/// exception and returns before the work is done. The <c>Async</c> suffix is what makes that
/// mistake visible at the call site, which is the only place anyone will be reading.
/// </para>
/// </summary>
public sealed class AsyncNamingRules
{
    /// <summary>
    /// Rule 7. Scoped to the Api and Infrastructure, the two assemblies that actually do I/O. The
    /// domain is synchronous by design — an aggregate that needed to await something would be
    /// reaching for a database — so there is nothing to name there.
    /// </summary>
    [Fact]
    public void Task_returning_methods_in_the_Api_and_Infrastructure_end_with_Async()
    {
        var asyncMethods = (
            from assembly in new[] { SolutionUnderTest.Api, SolutionUnderTest.Infrastructure }
            from type in assembly.GetTypes()
            where IsHandWritten(type)
            from method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly)
            where IsHandWritten(method) && ReturnsTaskLike(method)
            select (Assembly: assembly, Type: type, Method: method)).ToList();

        // Both assemblies do I/O, so both must contribute. Asserting per-assembly rather than
        // over the whole set is what catches the dangerous drift: IsHandWritten grows one more
        // exclusion, swallows an entire assembly, and the rule keeps passing while enforcing
        // nothing. Both assemblies have dozens of methods in scope, so an empty side is a bug in
        // the filter, never a fact about the code.
        foreach (var assembly in new[] { SolutionUnderTest.Api, SolutionUnderTest.Infrastructure })
        {
            Assert.True(
                asyncMethods.Any(candidate => candidate.Assembly == assembly),
                $"Found no hand-written Task-returning method in {assembly.GetName().Name}. That "
                + "assembly does I/O, so this rule is no longer inspecting it — IsHandWritten has "
                + "grown an exclusion that swallows real code.");
        }

        var found = asyncMethods
            .Where(static candidate => !candidate.Method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Select(static candidate =>
                $"{candidate.Type.FullName}.{candidate.Method.Name} returns "
                + $"{candidate.Method.ReturnType.Name} (in {candidate.Assembly.GetName().Name})")
            .ToList();

        if (found.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "A method returning Task, Task<T>, ValueTask or ValueTask<T> must end with 'Async'. "
                + "Without the suffix a caller who forgets to await gets a warning at most, and the "
                + "work silently runs detached.",
                found));
        }
    }

    /// <summary>
    /// True for code someone in this repository actually typed.
    /// <para>
    /// Two things have to be excluded. The compiler emits async state machines, closures and lambda
    /// caches as nested types whose names contain angle brackets — including
    /// <c>Program.&lt;Main&gt;$</c>, which top-level statements make an async <c>Task</c> method
    /// that nobody can rename. Source generators emit real, well-named types too: the OpenAPI XML
    /// comment generator puts transformers into the Api assembly. Neither is ours to rename, and a
    /// rule that fails on generated code would be turned off within a week.
    /// </para>
    /// </summary>
    private static bool IsHandWritten(Type type) =>
        !type.Name.Contains('<', StringComparison.Ordinal)
        && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && !type.IsDefined(typeof(GeneratedCodeAttribute), inherit: false)
        && (type.Namespace is null || type.Namespace.StartsWith("VelaCommerce", StringComparison.Ordinal));

    private static bool IsHandWritten(MethodInfo method) =>
        !method.Name.Contains('<', StringComparison.Ordinal)
        && !method.IsSpecialName
        && !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static bool ReturnsTaskLike(MethodInfo method)
    {
        var returned = method.ReturnType;

        return typeof(Task).IsAssignableFrom(returned)
               || returned == typeof(ValueTask)
               || (returned.IsGenericType && returned.GetGenericTypeDefinition() == typeof(ValueTask<>));
    }
}
