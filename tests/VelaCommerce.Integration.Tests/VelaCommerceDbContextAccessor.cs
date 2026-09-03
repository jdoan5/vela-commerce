using VelaCommerce.Infrastructure.Persistence;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// A context over the test container, plus a way to get a second one.
/// <para>
/// The teardown tests need a FRESH context to read back with: the one that ran the deletes has
/// the deleted entities still tracked, so a query answered from the change tracker would report
/// a row that is no longer in the database — which is the reverse of the mistake being tested for.
/// </para>
/// </summary>
internal sealed class VelaCommerceDbContextAccessor(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly List<VelaCommerceDbContext> _contexts = [];

    public VelaCommerceDbContext Context => field ??= Track(fixture.CreateContext());

    /// <summary>A context with an empty change tracker, for reading back what actually persisted.</summary>
    public VelaCommerceDbContext Fresh() => Track(fixture.CreateContext());

    private VelaCommerceDbContext Track(VelaCommerceDbContext context)
    {
        _contexts.Add(context);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }
    }
}
