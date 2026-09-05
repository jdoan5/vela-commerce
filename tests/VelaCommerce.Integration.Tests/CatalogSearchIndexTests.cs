using Microsoft.EntityFrameworkCore;
using Xunit;

namespace VelaCommerce.Integration.Tests;

/// <summary>
/// The trigram index behind the catalog search, and the honest account of when it is used.
/// <para>
/// The search is <c>ILIKE '%term%'</c> against untouched columns, which no B-tree can serve — a
/// leading wildcard leaves no prefix to seek on, so the planner must read every row. A
/// <c>gin_trgm_ops</c> GIN index is the structure that can, and these tests pin three things: the
/// extension is installed, both indexes exist, and the query shape is one the index can actually
/// answer.
/// </para>
/// <para>
/// <b>They do NOT assert that the planner chooses it, because at this catalog size it does not, and
/// it is right not to.</b> Measured: 288 products is 17 buffers, a sequential scan costs 21.32, and
/// no index path beats that. PostgreSQL only starts preferring the index somewhere between 30,000
/// and 100,000 rows at realistic selectivity — see docs/measurements/trigram-search.md. A test
/// asserting "the index is used" would therefore be a test asserting something false, and the way
/// to make it pass would be to force the planner's hand in a way production never would.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class CatalogSearchIndexTests(PostgresFixture fixture)
{
    /// <summary>
    /// The extension, which is the part that silently is not there on a database nobody migrated.
    /// Dropping <c>HasPostgresExtension</c> leaves the index definitions referring to an operator
    /// class that does not exist, and the migration fails loudly — but only if a migration runs.
    /// </summary>
    [Fact]
    public async Task The_trigram_extension_is_installed()
    {
        await using var db = fixture.CreateContext();

        var installed = await db.Database
            .SqlQuery<bool>($"""SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') AS "Value" """)
            .SingleAsync();

        Assert.True(installed, "pg_trgm is not installed, so no trigram index can exist.");
    }

    [Theory]
    [InlineData("ix_products_name_trgm", "name")]
    [InlineData("ix_products_description_trgm", "description")]
    public async Task Each_searched_column_has_a_gin_trigram_index(string indexName, string column)
    {
        await using var db = fixture.CreateContext();

        // Read the access method and the operator class from the catalog rather than trusting the
        // index name. A B-tree called ix_products_name_trgm would satisfy a name check and serve
        // nothing, which is exactly the mistake worth catching.
        var definition = await db.Database
            .SqlQuery<string>($"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = {indexName} """)
            .SingleOrDefaultAsync();

        Assert.NotNull(definition);
        Assert.Contains("USING gin", definition, StringComparison.Ordinal);
        Assert.Contains("gin_trgm_ops", definition, StringComparison.Ordinal);
        Assert.Contains(column, definition, StringComparison.Ordinal);
    }

    /// <summary>
    /// The assertion with teeth: the index can serve the query the endpoint actually writes.
    /// <para>
    /// <c>enable_seqscan = off</c> asks the planner for its best index-based plan rather than its
    /// best plan. That is a diagnostic, not a production setting, and it is the only way to ask
    /// "could this index answer this query" separately from "is it worth it at this size". If
    /// <c>CatalogEndpoints</c> ever changes to <c>lower(name) LIKE …</c>, or the operator class is
    /// dropped, no index-based plan exists and this fails even with sequential scans forbidden.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_search_query_shape_can_be_answered_by_the_trigram_index()
    {
        await using var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync();

        var connection = db.Database.GetDbConnection();

        // SET LOCAL needs a transaction to be local to; without one PostgreSQL warns and ignores it.
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var setting = connection.CreateCommand())
        {
            setting.Transaction = transaction;
            setting.CommandText = "SET LOCAL enable_seqscan = off";
            await setting.ExecuteNonQueryAsync();
        }

        await using var explain = connection.CreateCommand();
        explain.Transaction = transaction;
        explain.CommandText =
            """
            EXPLAIN SELECT id FROM products
            WHERE deleted_at IS NULL
              AND (name ILIKE '%lamp%' OR description ILIKE '%lamp%')
            """;

        var plan = new System.Text.StringBuilder();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        var text = plan.ToString();

        Assert.False(string.IsNullOrWhiteSpace(text), "EXPLAIN returned no plan at all.");

        // A bitmap scan over the trigram indexes. Both halves matter: "Bitmap" alone would pass on
        // some other index, and "trgm" alone would pass on a plan that merely mentioned one.
        Assert.Contains("Bitmap", text, StringComparison.Ordinal);
        Assert.Contains("trgm", text, StringComparison.Ordinal);

        await transaction.RollbackAsync();
    }
}
