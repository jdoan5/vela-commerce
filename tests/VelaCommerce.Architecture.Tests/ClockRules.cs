using Mono.Cecil;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// Where "now" is allowed to come from.
/// <para>
/// Reading the ambient clock inside a type makes that type untestable at any instant but the
/// present one, and makes its behaviour depend on the machine it runs on. This codebase takes the
/// opposite line: time arrives as a parameter, the way
/// <c>Order.MarkPaid(Money, DateTimeOffset)</c> and <c>Entity.SoftDelete(DateTimeOffset)</c> take
/// it. That is what lets a test assert on a refund window or an expiring reservation without
/// sleeping, and what lets one checkout stamp every row it touches with a single consistent
/// timestamp instead of a handful of microseconds apart.
/// </para>
/// </summary>
public sealed class ClockRules
{
    /// <summary>
    /// Property getters compile to calls, so a clock read is a call to one of these. Named
    /// individually rather than banning the whole type: <c>DateTimeOffset</c> as a parameter or a
    /// stored value is the point of the convention, only reading the ambient clock is not.
    /// </summary>
    private static readonly (string DeclaringType, string Member)[] AmbientClockReads =
    [
        ("System.DateTime", "get_Now"),
        ("System.DateTime", "get_UtcNow"),
        ("System.DateTime", "get_Today"),
        ("System.DateTimeOffset", "get_Now"),
        ("System.DateTimeOffset", "get_UtcNow"),
    ];

    /// <summary>
    /// The designated place. Empty today, and that is the intended state: no type in this solution
    /// has earned an exemption. When one is genuinely needed — an expiry sweep, a background job —
    /// the seam is <see cref="TimeProvider"/> injected at the composition root, and the exemption
    /// belongs in this list with a comment saying why, so the decision stays visible in a diff.
    /// </summary>
    private static readonly string[] MayReadTheAmbientClock = [];

    /// <summary>
    /// Rule 6. Checked against IL, not source, because the interesting violations hide in property
    /// initialisers, constructors and lambdas where a source grep would have to guess.
    /// <para>
    /// Scope is the three production assemblies. The seed generator under <c>tools/</c> is out of
    /// scope on purpose: it is a build-time program, not part of the running system, and pulling it
    /// in would make this test project reference an executable it has no other business knowing.
    /// </para>
    /// </summary>
    [Fact]
    public void No_type_reads_the_ambient_clock_directly()
    {
        var offenders = new List<string>();
        var callsInspected = 0;

        foreach (var assembly in SolutionUnderTest.Production)
        {
            using var module = IlFacts.ReadModule(assembly);

            foreach (var type in module.GetTypes())
            {
                var authored = IlFacts.AuthoredType(type);

                if (MayReadTheAmbientClock.Contains(authored.FullName, StringComparer.Ordinal))
                {
                    continue;
                }

                foreach (var (from, called) in IlFacts.CallsMadeBy(type))
                {
                    callsInspected++;

                    if (IsAmbientClockRead(called))
                    {
                        offenders.Add(
                            $"{authored.FullName}.{ReadableName(from)} reads "
                            + $"{called.DeclaringType.Name}.{called.Name[4..]} (in {module.Assembly.Name.Name})");
                    }
                }
            }
        }

        // A scan that reads no calls would report no violations, and report them very convincingly.
        Assert.True(
            callsInspected > 0,
            "Inspected no method calls at all across the three production assemblies. "
            + "IlFacts.CallsMadeBy has stopped seeing IL, so this rule is not enforcing anything.");

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "Nothing may read DateTime.Now, DateTime.UtcNow, DateTime.Today, "
                + "DateTimeOffset.Now or DateTimeOffset.UtcNow directly. Time is an input: take it "
                + "as a DateTimeOffset parameter the way Order.MarkPaid(Money, DateTimeOffset) does, "
                + "and let the composition root read the clock once per operation.",
                offenders));
        }
    }

    private static bool IsAmbientClockRead(MethodReference called) =>
        AmbientClockReads.Any(read =>
            called.DeclaringType.FullName.Equals(read.DeclaringType, StringComparison.Ordinal)
            && called.Name.Equals(read.Member, StringComparison.Ordinal));

    /// <summary>
    /// Turns <c>.ctor</c> and the compiler's mangled state-machine method names into something a
    /// reviewer can search the source for.
    /// </summary>
    private static string ReadableName(MethodDefinition method) =>
        method.Name switch
        {
            ".ctor" => "constructor",
            ".cctor" => "static constructor",
            var name when name.StartsWith('<') && name.Contains('>') =>
                name[1..name.IndexOf('>', StringComparison.Ordinal)],
            var name => name,
        };
}
