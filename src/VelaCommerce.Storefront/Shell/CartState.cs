using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using VelaCommerce.Storefront.Catalog;

namespace VelaCommerce.Storefront.Shell;

/// <summary>
/// One line of the cart: a SKU, what it is called, what it cost when it was picked up, and
/// how many of it.
/// <para>
/// The price is a count of minor units, carried on the line rather than looked up again on
/// render, so a line renders the same whether or not the catalog snapshot is still in
/// memory. It is a record so a quantity change is a <c>with</c> expression and the list
/// never holds a half-mutated line.
/// </para>
/// </summary>
public sealed record CartLine
{
    /// <summary>The variant's SKU. Identity: one line per SKU, always.</summary>
    public string Sku { get; init; } = "";

    /// <summary>The owning product's slug, so a line can link back to <c>/p/{slug}</c>.</summary>
    public string Slug { get; init; } = "";

    /// <summary>The product name, as it read when the line was added.</summary>
    public string ProductName { get; init; } = "";

    /// <summary>The variant name — "Medium", "Bronze", "10 m" — along whatever axis the product varies.</summary>
    public string VariantName { get; init; } = "";

    /// <summary>Unit price in minor units. Never a decimal, never a double.</summary>
    public long UnitPriceMinorUnits { get; init; }

    /// <summary>How many. Always between 1 and <see cref="CartState.MaxLineQuantity"/>.</summary>
    public int Quantity { get; init; }

    /// <summary>The cart's currency, stamped on so the line can hand out a <see cref="CatalogMoney"/>.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>Unit price times quantity, in minor units. Integer arithmetic end to end.</summary>
    public long LineTotalMinorUnits => UnitPriceMinorUnits * Quantity;

    /// <summary>The unit price as money, for <see cref="MoneyFormatter"/>.</summary>
    public CatalogMoney UnitPrice => new(UnitPriceMinorUnits, Currency);

    /// <summary>The line total as money, for <see cref="MoneyFormatter"/>.</summary>
    public CatalogMoney LineTotal => new(LineTotalMinorUnits, Currency);
}

/// <summary>
/// The shopper's cart, held in the tab and mirrored into <c>localStorage</c>.
/// <para>
/// Nothing here touches the network. The API and the database scale to zero, so a cart that
/// needed a round trip to add a line would be a cart that does not work when the shop is
/// browsable — which is the whole point of this storefront. The cart is client state until
/// a checkout exists to hand it to, and checkout does not exist yet.
/// </para>
/// <para>
/// It enforces the same rule the server does: <see cref="MaxLineQuantity"/> mirrors
/// <c>VelaCommerce.Domain.Carts.CartLine.MaxQuantity</c>. Enforcing it here is not a
/// substitute for the domain guard, it is so the shopper is never shown a quantity the
/// server would go on to reject.
/// </para>
/// <para>
/// Every read and write of <c>localStorage</c> is wrapped. A private window, blocked site
/// data or a full quota all throw, and a storefront that white-screens because storage is
/// switched off is worse than one that quietly forgets the cart.
/// </para>
/// </summary>
public sealed class CartState
{
    /// <summary>
    /// The per-line cap, mirroring <c>VelaCommerce.Domain.Carts.CartLine.MaxQuantity</c>.
    /// Adding to a line that is already at the cap leaves it at the cap rather than climbing
    /// past it and being rejected later.
    /// </summary>
    public const int MaxLineQuantity = 99;

    /// <summary>
    /// How many distinct lines a restored cart may carry. Only ever hit by hand-edited or
    /// hostile storage; the UI cannot produce it. A restore that exceeds it is truncated
    /// rather than allowed to allocate without bound.
    /// </summary>
    private const int MaxLines = 200;

    /// <summary>The payload shape this build writes and is willing to read back.</summary>
    private const int CurrentStorageVersion = 1;

    /// <summary>The storage key. Versioned in the name so a future shape cannot be read as this one.</summary>
    private const string StorageKey = "vela.cart.v1";

    private readonly IJSRuntime _js;
    private readonly StorefrontState _shell;
    private readonly List<CartLine> _lines = [];

    private string _currency = "USD";
    private bool _restored;
    private bool _storageWorks = true;

    /// <summary>Creates the cart over the browser's storage and the shell it reports its badge to.</summary>
    public CartState(IJSRuntime js, StorefrontState shell)
    {
        _js = js;
        _shell = shell;
    }

    /// <summary>Raised after any mutation. Subscribers must unsubscribe on dispose.</summary>
    public event Action? Changed;

    /// <summary>The lines, in the order they were first added.</summary>
    public IReadOnlyList<CartLine> Lines => _lines;

    /// <summary>True when there is nothing in the cart.</summary>
    public bool IsEmpty => _lines.Count == 0;

    /// <summary>How many distinct SKUs are in the cart.</summary>
    public int LineCount => _lines.Count;

    /// <summary>Every unit across every line — what the header badge counts.</summary>
    public int TotalQuantity
    {
        get
        {
            var total = 0;
            foreach (var line in _lines)
                total += line.Quantity;

            return total;
        }
    }

    /// <summary>
    /// The cart's currency. The snapshot names one currency for the whole catalog, so a cart
    /// has exactly one too; it is taken from the first line added and kept.
    /// </summary>
    public string Currency => _currency;

    /// <summary>
    /// The sum of every line total, in minor units. Summed as integers — a subtotal is the
    /// last place anyone should meet a rounding error.
    /// </summary>
    public CatalogMoney Subtotal
    {
        get
        {
            long total = 0;
            foreach (var line in _lines)
                total += line.LineTotalMinorUnits;

            return new CatalogMoney(total, _currency);
        }
    }

    /// <summary>
    /// False once a read or a write has thrown. The drawer says so quietly rather than
    /// letting a shopper believe a cart is being kept that is not.
    /// </summary>
    public bool IsPersisted => _storageWorks;

    /// <summary>The line for a SKU, or null when that SKU is not in the cart.</summary>
    public CartLine? Find(string? sku) =>
        sku is null ? null : _lines.Find(line => string.Equals(line.Sku, sku, StringComparison.Ordinal));

    /// <summary>
    /// Adds a variant, merging into the existing line for that SKU rather than making a
    /// second one. The merged quantity is capped, never wrapped: adding ten to a line
    /// holding ninety-five leaves ninety-nine.
    /// </summary>
    /// <param name="product">The product the variant belongs to, for the name and the slug.</param>
    /// <param name="variant">The SKU being added.</param>
    /// <param name="quantity">How many, clamped into 1..<see cref="MaxLineQuantity"/>.</param>
    public void Add(CatalogProduct product, CatalogVariant variant, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(variant);

        Add(new CartLine
        {
            Sku = variant.Sku,
            Slug = product.Slug,
            ProductName = product.Name,
            VariantName = variant.Name,
            UnitPriceMinorUnits = variant.PriceMinorUnits,
            Currency = variant.Currency,
        },
        quantity);
    }

    /// <summary>
    /// Adds a prepared line. The line's own <c>Quantity</c> is ignored in favour of
    /// <paramref name="quantity"/>, so there is one place a quantity is clamped.
    /// </summary>
    public void Add(CartLine line, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Sku.Length == 0)
            return;

        var wanted = Math.Clamp(quantity, 1, MaxLineQuantity);
        var index = _lines.FindIndex(existing => string.Equals(existing.Sku, line.Sku, StringComparison.Ordinal));

        if (index >= 0)
        {
            var existing = _lines[index];
            var merged = Math.Min(MaxLineQuantity, existing.Quantity + wanted);
            if (merged == existing.Quantity)
            {
                // Already at the cap. Nothing changed, but the caller still asked for
                // something, so notify: the page shows "this line is at the cap".
                Changed?.Invoke();
                return;
            }

            _lines[index] = existing with { Quantity = merged };
        }
        else
        {
            if (_lines.Count == 0 && line.Currency.Length > 0)
                _currency = line.Currency;

            _lines.Add(line with { Quantity = wanted, Currency = _currency });
        }

        Commit();
    }

    /// <summary>
    /// Sets a line's quantity. Zero or less removes the line rather than storing a
    /// non-positive quantity — the same choice the domain makes when it says "remove the
    /// line instead". Above the cap is clamped to the cap.
    /// </summary>
    public void SetQuantity(string sku, int quantity)
    {
        var index = _lines.FindIndex(line => string.Equals(line.Sku, sku, StringComparison.Ordinal));
        if (index < 0)
            return;

        if (quantity <= 0)
        {
            _lines.RemoveAt(index);
            Commit();
            return;
        }

        var next = Math.Min(quantity, MaxLineQuantity);
        if (next == _lines[index].Quantity)
            return;

        _lines[index] = _lines[index] with { Quantity = next };
        Commit();
    }

    /// <summary>Removes a line outright. A no-op when the SKU is not in the cart.</summary>
    public void Remove(string sku)
    {
        var index = _lines.FindIndex(line => string.Equals(line.Sku, sku, StringComparison.Ordinal));
        if (index < 0)
            return;

        _lines.RemoveAt(index);
        Commit();
    }

    /// <summary>Empties the cart. A no-op when it is already empty, so it cannot spuriously re-render.</summary>
    public void Clear()
    {
        if (_lines.Count == 0)
            return;

        _lines.Clear();
        Commit();
    }

    /// <summary>
    /// Reads the cart back out of <c>localStorage</c>, once, after the first render. Any
    /// failure — storage switched off, a corrupt value, a value from a different build —
    /// ends with an empty cart and a working shop, never an exception.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (_restored)
            return;

        _restored = true;

        var raw = await ReadAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return;

        CartPersistedState? stored;
        try
        {
            stored = JsonSerializer.Deserialize(raw, CartJsonContext.Default.CartPersistedState);
        }
        catch (Exception)
        {
            // Corrupt, truncated or from a shape this build does not know. Discard it: a
            // cart that cannot be read is not an error worth showing a shopper.
            await ForgetAsync().ConfigureAwait(false);
            return;
        }

        if (stored is null)
            return;

        // The type documents this guard, so it has to exist. The key is versioned too, which
        // makes a mismatch unlikely today, but a payload from a shape this build does not
        // know is discarded rather than half-read into a cart the shopper then transacts on.
        if (stored.Version != CurrentStorageVersion)
        {
            await ForgetAsync().ConfigureAwait(false);
            return;
        }

        if (stored.Lines is not { Count: > 0 } storedLines)
            return;

        _currency = NormaliseCurrency(stored.Currency);

        var dropped = false;
        foreach (var candidate in storedLines)
        {
            if (_lines.Count >= MaxLines)
            {
                dropped = true;
                break;
            }

            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Sku) || candidate.UnitPriceMinorUnits < 0)
            {
                dropped = true;
                continue;
            }

            var quantity = Math.Clamp(candidate.Quantity, 1, MaxLineQuantity);
            if (quantity != candidate.Quantity)
                dropped = true;

            var index = _lines.FindIndex(line => string.Equals(line.Sku, candidate.Sku, StringComparison.Ordinal));
            if (index >= 0)
            {
                // Two lines for one SKU is not a shape this cart can produce, so storage was
                // edited. Merge rather than trusting it, and still respect the cap.
                _lines[index] = _lines[index] with
                {
                    Quantity = Math.Min(MaxLineQuantity, _lines[index].Quantity + quantity),
                };
                dropped = true;
                continue;
            }

            _lines.Add(new CartLine
            {
                Sku = candidate.Sku,
                Slug = candidate.Slug ?? "",
                ProductName = string.IsNullOrWhiteSpace(candidate.ProductName) ? candidate.Sku : candidate.ProductName,
                VariantName = candidate.VariantName ?? "",
                UnitPriceMinorUnits = candidate.UnitPriceMinorUnits,
                Quantity = quantity,
                Currency = _currency,
            });
        }

        _shell.SetCartItemCount(TotalQuantity);
        Changed?.Invoke();

        // Only rewrite when the stored value was not exactly what we now hold, so a clean
        // restore costs one read and no write.
        if (dropped)
            await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a mutation: badge first so the header is never a frame behind, then the
    /// drawer, then storage — which is allowed to fail without anybody noticing.
    /// </summary>
    private void Commit()
    {
        _shell.SetCartItemCount(TotalQuantity);
        Changed?.Invoke();
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (!_storageWorks)
            return;

        try
        {
            if (_lines.Count == 0)
            {
                await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey).ConfigureAwait(false);
                return;
            }

            var payload = new CartPersistedState
            {
                Currency = _currency,
                Lines = [.. _lines.Select(static line => new CartPersistedLine
                {
                    Sku = line.Sku,
                    Slug = line.Slug,
                    ProductName = line.ProductName,
                    VariantName = line.VariantName,
                    UnitPriceMinorUnits = line.UnitPriceMinorUnits,
                    Quantity = line.Quantity,
                })],
            };

            var json = JsonSerializer.Serialize(payload, CartJsonContext.Default.CartPersistedState);
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Private windows, blocked site data and a full quota all land here. The cart
            // goes on working for this tab; it just will not survive a reload, and the
            // drawer says so rather than pretending otherwise.
            MarkStorageUnavailable();
        }
    }

    private async Task<string?> ReadAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey).ConfigureAwait(false);
        }
        catch (Exception)
        {
            MarkStorageUnavailable();
            return null;
        }
    }

    private async Task ForgetAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey).ConfigureAwait(false);
        }
        catch (Exception)
        {
            MarkStorageUnavailable();
        }
    }

    private void MarkStorageUnavailable()
    {
        if (!_storageWorks)
            return;

        _storageWorks = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// A currency code we are willing to price a cart in. Anything else came from an edited
    /// store and falls back to the catalog's own default rather than reaching
    /// <see cref="MoneyFormatter"/> as junk.
    /// </summary>
    private static string NormaliseCurrency(string? currency)
    {
        if (currency is not { Length: 3 })
            return "USD";

        foreach (var c in currency)
        {
            if (!char.IsAsciiLetter(c))
                return "USD";
        }

        return currency.ToUpperInvariant();
    }
}

/// <summary>One stored line. Abbreviated names because this string lives in the browser, not in a file anybody reads.</summary>
internal sealed record CartPersistedLine
{
    [JsonPropertyName("sku")] public string Sku { get; init; } = "";
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("name")] public string? ProductName { get; init; }
    [JsonPropertyName("variant")] public string? VariantName { get; init; }
    [JsonPropertyName("price")] public long UnitPriceMinorUnits { get; init; }
    [JsonPropertyName("qty")] public int Quantity { get; init; }
}

/// <summary>
/// The whole stored cart. Carries its own version number as well as a versioned key, so a
/// value written by a newer build is recognisably not ours and can be discarded rather than
/// half-read.
/// </summary>
internal sealed record CartPersistedState
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1; // keep in step with CartState.CurrentStorageVersion
    [JsonPropertyName("currency")] public string Currency { get; init; } = "USD";
    [JsonPropertyName("lines")] public List<CartPersistedLine>? Lines { get; init; }
}

/// <summary>
/// Source-generated readers for the stored cart, for the same reason the catalog has them:
/// reflection-based serialisation drags the reflection stack into the download and produces
/// trim warnings on a Release publish.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CartPersistedState))]
internal sealed partial class CartJsonContext : JsonSerializerContext;
