using Mono.Cecil;

namespace VelaCommerce.Architecture.Tests;

/// <summary>
/// Where the admin's price overlay is allowed to appear.
/// <para>
/// A visitor's price comes from one of two places: the seeded catalog row everybody shares, or a
/// per-session override laid over it. Which one wins is a single expression, and it is correct only
/// while it exists once. Written twice it will differ, and the way it differs is that one copy
/// forgets the overlay — so a cart captures the shared price while checkout compares against the
/// overridden one, the price-changed guard fires on a line the shopper never touched, and re-adding
/// the item captures the shared price again and re-arms it. A loop with no error in it.
/// </para>
/// <para>
/// <c>EffectiveCatalogPrices</c> has claimed to be the only place naming the overlay since the day
/// it was written. The claim was prose, nothing enforced it, and the admin console falsified it
/// within a day: its page reader queried the table directly to dodge a LINQ translation failure,
/// and the sentence went on asserting otherwise in three files. This rule is the answer to that,
/// and the entity being <c>internal</c> is the other half — outside Infrastructure the compiler
/// refuses, and in here this test does.
/// </para>
/// <para>
/// Read from IL rather than source, like every rule in this suite. A Razor component's
/// <c>OnInitializedAsync</c> compiles to a nested closure type that reflection over declared
/// members would not attribute to the page, and a page quietly reading the overlay is exactly the
/// regression worth catching.
/// </para>
/// </summary>
public sealed class CatalogOverlayRules
{
    private const string Overlay =
        "VelaCommerce.Infrastructure.Persistence.CatalogOverrides.DemoCatalogPriceOverride";

    /// <summary>The resolution point itself: the one expression that decides which price wins.</summary>
    private const string Gateway =
        "VelaCommerce.Infrastructure.Persistence.CatalogOverrides.EffectiveCatalogPrices";

    /// <summary>The EF mapping. It names the entity because describing a table requires naming it.</summary>
    private const string Configuration =
        "VelaCommerce.Infrastructure.Persistence.Configurations.DemoCatalogPriceOverrideConfiguration";

    /// <summary>
    /// The context, which names the entity once — to attach the <c>DemoTenancy</c> query filter.
    /// That line is the reason no query in the gateway writes a session predicate by hand, so it
    /// belongs here as much as the gateway does.
    /// </summary>
    private const string Context = "VelaCommerce.Infrastructure.Persistence.VelaCommerceDbContext";

    /// <summary>
    /// Four names, and the entity is one of them because a type mentions itself. Deliberately not a
    /// namespace: admitting all of <c>CatalogOverrides</c> would let a second resolution point be
    /// added beside the first and still pass, which is the exact failure this rule exists to stop.
    /// </summary>
    private static readonly string[] MayNameTheOverlay = [Overlay, Gateway, Configuration, Context];

    /// <summary>
    /// The migrations are absent from that list on purpose, and it costs nothing.
    /// <para>
    /// A migration and its model snapshot describe the table as string literals — <c>"demo_catalog_
    /// price_overrides"</c>, column names, an index name — and never reference the CLR type, so the
    /// IL walk does not see them and no exemption is needed. Adding a namespace wildcard "to be
    /// safe" would be an unearned exemption, which is the thing <c>ClockRules</c> advertises its own
    /// exemption list does not contain.
    /// </para>
    /// </summary>
    [Fact]
    public void The_price_overlay_is_named_only_by_its_gateway_its_mapping_and_the_context()
    {
        var offenders = new List<string>();
        var namers = 0;

        foreach (var assembly in SolutionUnderTest.Production)
        {
            using var module = IlFacts.ReadModule(assembly);

            foreach (var type in module.GetTypes())
            {
                if (!IlFacts.TypesMentionedBy(type).Contains(Overlay))
                {
                    continue;
                }

                namers++;
                var authored = IlFacts.AuthoredType(type);

                if (!MayNameTheOverlay.Contains(authored.FullName, StringComparer.Ordinal))
                {
                    offenders.Add($"{authored.FullName} (in {module.Assembly.Name.Name})");
                }
            }
        }

        // The overlay is definitely named somewhere — the gateway alone names it five times. If the
        // count is zero the IL walk has stopped seeing what it is walking, and this rule would pass
        // by inspecting nothing: a green tick over an unenforced boundary, which is worse than no
        // rule because it reads as evidence.
        Assert.True(
            namers > 0,
            $"Found no type naming {Overlay} anywhere in the production assemblies, which cannot be "
            + "true while the gateway compiles. IlFacts.TypesMentionedBy has stopped seeing IL, so "
            + "this rule is not enforcing anything.");

        if (offenders.Count > 0)
        {
            Assert.Fail(SolutionUnderTest.Explain(
                "The per-session price overlay may only be named by its gateway "
                + $"('{Gateway}'), its EF mapping ('{Configuration}') and the DbContext that applies "
                + "the DemoTenancy filter to it. Everywhere else, go through EffectiveCatalogPrices "
                + "— price resolution written twice is price resolution that will differ, and the "
                + "difference is a cart and a checkout disagreeing about what a line costs.",
                offenders.Distinct(StringComparer.Ordinal)));
        }
    }
}
