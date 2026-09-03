using VelaCommerce.Storefront.Cart;
using VelaCommerce.Storefront.Catalog;

namespace VelaCommerce.Storefront.Shell;

/// <summary>
/// One line of the cart, as the storefront draws it.
/// <para>
/// Two prices, deliberately. <see cref="UnitPriceMinorUnits"/> is what was captured when the line
/// was added and is what the line total is computed from; <see cref="CurrentUnitPriceMinorUnits"/>
/// is what the catalog charges now, and is null when the variant has been withdrawn altogether. The
/// cart never silently reprices, so where the two disagree the drawer has to say so — see
/// <see cref="PriceChanged"/>, which is a headline feature of this shop and not a detail.
/// </para>
/// <para>
/// Identity is the SKU, because that is what the shopper reads off a part and what the components
/// key on. <see cref="VariantId"/> is the server's identity for the same thing and is what the cart
/// endpoints address lines by; it is carried here so a quantity change or a removal costs no lookup.
/// </para>
/// </summary>
public sealed record CartLine
{
    /// <summary>The variant's SKU. Identity: one line per SKU, always.</summary>
    public string Sku { get; init; } = "";

    /// <summary>
    /// The server's id for this variant, or <see cref="Guid.Empty"/> on a line that has been added
    /// optimistically and not yet confirmed. Empty is not an error, it is "the round trip that will
    /// tell us has not landed yet".
    /// </summary>
    public Guid VariantId { get; init; }

    /// <summary>The owning product's slug, so a line can link back to <c>/p/{slug}</c>.</summary>
    public string Slug { get; init; } = "";

    /// <summary>The product name, as it read when the line was added.</summary>
    public string ProductName { get; init; } = "";

    /// <summary>The variant name — "Medium", "Bronze", "10 m" — along whatever axis the product varies.</summary>
    public string VariantName { get; init; } = "";

    /// <summary>Captured unit price in minor units. Never a decimal, never a double.</summary>
    public long UnitPriceMinorUnits { get; init; }

    /// <summary>
    /// The catalog's live unit price in minor units, or null when the variant is no longer sellable.
    /// Null means something different from "unchanged": there is nothing left to compare against and
    /// this line cannot be checked out as it stands.
    /// </summary>
    public long? CurrentUnitPriceMinorUnits { get; init; }

    /// <summary>How many. Always between 1 and <see cref="CartState.MaxLineQuantity"/>.</summary>
    public int Quantity { get; init; }

    /// <summary>The cart's currency, stamped on so the line can hand out a <see cref="CatalogMoney"/>.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// True while this line is the client's guess and the server has not yet answered. The drawer can
    /// use it to mark a line as settling; nothing about the line is trusted once the response lands,
    /// because the response replaces it outright.
    /// </summary>
    public bool IsPending { get; init; }

    /// <summary>Captured unit price times quantity, in minor units. Integer arithmetic end to end.</summary>
    public long LineTotalMinorUnits => UnitPriceMinorUnits * Quantity;

    /// <summary>The captured unit price as money, for <see cref="MoneyFormatter"/>.</summary>
    public CatalogMoney UnitPrice => new(UnitPriceMinorUnits, Currency);

    /// <summary>The line total as money, for <see cref="MoneyFormatter"/>.</summary>
    public CatalogMoney LineTotal => new(LineTotalMinorUnits, Currency);

    /// <summary>The live unit price as money, or null when the variant has been withdrawn.</summary>
    public CatalogMoney? CurrentUnitPrice =>
        CurrentUnitPriceMinorUnits is { } current ? new CatalogMoney(current, Currency) : null;

    /// <summary>
    /// Whether the catalog moved under this line. Derived from the two prices rather than sent
    /// alongside them, so the flag and the amounts can never tell a shopper two different stories.
    /// </summary>
    public bool PriceChanged =>
        CurrentUnitPriceMinorUnits is { } current && current != UnitPriceMinorUnits;

    /// <summary>
    /// Live price minus captured price, in minor units, or null when nothing moved. Signed on
    /// purpose: positive means the shopper is holding a bargain, negative means re-adding it would
    /// cost less.
    /// </summary>
    public long? PriceDifferenceMinorUnits =>
        PriceChanged ? CurrentUnitPriceMinorUnits!.Value - UnitPriceMinorUnits : null;

    /// <summary>
    /// Whether the variant is still sellable. False means the SKU was withdrawn after it was added;
    /// the line is kept and shown rather than deleted behind the shopper's back, because a line that
    /// vanishes silently is indistinguishable from a bug in the cart.
    /// </summary>
    public bool StillInCatalog => CurrentUnitPriceMinorUnits is not null;
}

/// <summary>
/// Where the conversation with the shop has got to.
/// <para>
/// <see cref="Waking"/> is the state this enum exists for. The API scales to zero, so the first cart
/// call after a quiet period waits on a container starting and a database resuming. A spinner that
/// looks identical to a two-hundred-millisecond one is a lie by omission; naming the state lets the
/// drawer say "waking the shop up" and offer a way out.
/// </para>
/// </summary>
public enum CartSyncState
{
    /// <summary>Nothing has been asked of the API yet. The normal state while somebody is only browsing.</summary>
    Idle,

    /// <summary>A call is in flight and has not yet been slow enough to be worth mentioning.</summary>
    Loading,

    /// <summary>A call is in flight and has passed the point where a cold start is the likeliest explanation.</summary>
    Waking,

    /// <summary>The lines on screen are the ones the server last confirmed.</summary>
    Ready,

    /// <summary>The last call failed and there is no confirmed cart to show. <see cref="CartState.RetryAsync"/> tries again.</summary>
    Failed,
}

/// <summary>
/// The shopper's cart, held by the server and mirrored here.
/// <para>
/// <strong>This used to be a localStorage cart, and the change of authority is the point of the
/// phase.</strong> The server decides quantities, prices and what a cart even contains; this class
/// holds the last answer it gave and reconciles to the next one. There is no second copy anywhere —
/// no localStorage mirror, no merge on startup — because two stores of the same cart means one of
/// them is wrong and neither knows which.
/// </para>
/// <para>
/// <strong>Nothing here runs on the first-paint path.</strong> The catalog is a static file and the
/// shop browses, searches, filters and sorts with the API switched off; that must stay true, so the
/// cart is fetched on the first genuine need — the drawer opening, or an add being pressed — and
/// never from a layout's initialisation. <see cref="RestoreAsync"/> is kept and does nothing, for
/// exactly that reason.
/// </para>
/// <para>
/// The mutating methods stay synchronous and <c>void</c>. That is not laziness: a button handler
/// wants to update the screen in the frame it was clicked in, so a change is applied locally at
/// once, sent, and then replaced wholesale by whatever the server says. The optimistic copy is a
/// guess with a lifetime of one round trip, and it is never merged with the answer — it is
/// discarded by it.
/// </para>
/// </summary>
public sealed class CartState : IDisposable
{
    /// <summary>
    /// The per-line cap, mirroring <c>VelaCommerce.Domain.Carts.CartLine.MaxQuantity</c>.
    /// <para>
    /// The server is the authority and enforces this itself; the copy here is a courtesy that keeps
    /// the shopper from being shown a quantity the API is going to refuse, and keeps a doomed request
    /// off a cold connection. It is never treated as the rule — the response is.
    /// </para>
    /// </summary>
    public const int MaxLineQuantity = 99;

    /// <summary>
    /// RETRY POLICY, and why it is shaped like this.
    /// <para>
    /// The API scales to zero. A first call after an idle period pays for a container starting and a
    /// serverless PostgreSQL resuming — seconds, sometimes more than ten, and occasionally a first
    /// attempt that never lands at all because the platform dropped it while starting.
    /// <see cref="HttpClient"/>'s default hundred-second timeout is useless at both ends of that: far
    /// too long to leave a shopper looking at a spinner, and a single attempt gives the platform no
    /// second chance once it is finally warm.
    /// </para>
    /// <para>
    /// So: three attempts for a read, with deadlines of six, fourteen and twenty-five seconds and no
    /// sleeping in between. The wait <em>is</em> the backoff — the server is starting, and pausing on
    /// the client only adds to the total — and each deadline is longer than the last because a second
    /// failure means the start is slower than typical, not that the server is gone. Past the whole
    /// budget of roughly forty-five seconds the loop stops and offers a manual retry, because a
    /// storefront that retries forever is a storefront that never admits anything is wrong.
    /// </para>
    /// <para>
    /// Forty-five seconds is the worst case, not the usual one, and only when the server accepts the
    /// connection and then goes quiet. A request that is refused outright — nothing listening, no
    /// route to the host — fails in milliseconds, so all three attempts are spent and the retry
    /// button is on screen before a shopper has finished reading the panel. Measured: three attempts
    /// inside seventy milliseconds against a host that rejects immediately.
    /// </para>
    /// </summary>
    private static readonly TimeSpan[] ReadDeadlines =
    [
        TimeSpan.FromSeconds(6),
        TimeSpan.FromSeconds(14),
        TimeSpan.FromSeconds(25),
    ];

    /// <summary>
    /// The write budget: one attempt, and a generous one.
    /// <para>
    /// <strong>Adding to a cart must not be retried, and this is the line of code that says so.</strong>
    /// <c>POST /api/cart/items</c> merges its quantity into the existing line, so it is an increment
    /// and not an assignment, and it carries no idempotency key. A retry after a response was lost in
    /// transit would add the item twice — and from inside a browser there is no way to tell a request
    /// that never arrived from one whose answer did not come back. One attempt with a long deadline,
    /// and on failure a plain re-read of the cart, which is safe and tells the shopper what actually
    /// happened. Quantity changes and removals are absolute and idempotent, so they use the read
    /// budget above.
    /// </para>
    /// </summary>
    private static readonly TimeSpan[] WriteDeadlines = [TimeSpan.FromSeconds(30)];

    /// <summary>
    /// How long a call may take before the UI is told to stop pretending this is normal. Short,
    /// because the honest thing is to explain the wait early rather than to hope it ends.
    /// </summary>
    private static readonly TimeSpan WakeNotice = TimeSpan.FromSeconds(2);

    private readonly CartApiClient _api;
    private readonly CatalogService _catalog;
    private readonly StorefrontState _shell;
    private readonly CartDrawerState _drawer;

    /// <summary>
    /// Serialises everything that talks to the cart endpoints.
    /// <para>
    /// Not for thread safety — WebAssembly is single-threaded — but for ordering. The cart endpoints
    /// are read-modify-write with no row lock, and two overlapping requests from one session are
    /// documented in <c>CartEndpoints</c> to lose an update or duplicate a line. A shopper leaning on
    /// a quantity stepper is exactly how you produce two overlapping requests, so they are queued and
    /// each one starts from the state the last one returned.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// SKU to variant id. The catalog snapshot carries SKUs but no ids — ids are database keys and
    /// the snapshot is a generated static file — so the first add of a product costs one catalog
    /// lookup. Every cart response then refills this for free, because its lines carry both.
    /// </summary>
    private readonly Dictionary<string, Guid> _variantIds = new(StringComparer.Ordinal);

    private List<CartLine> _lines = [];
    private Dictionary<string, CatalogPlacement>? _placements;
    private Task? _load;
    private string _currency = "USD";
    private int _busy;

    /// <summary>
    /// Creates the cart over the app's own <see cref="HttpClient"/>, the catalog snapshot it enriches
    /// lines from, and the shell it reports its badge to.
    /// <para>
    /// It takes the client and builds its own <see cref="CartApiClient"/> rather than having one
    /// injected, for two reasons. The adapter holds no state and has no lifetime worth managing. And
    /// a second registered collaborator would give this class two viable constructors the moment
    /// anybody registered it, which container constructor selection resolves by throwing — at the
    /// instant a shopper first opens the drawer, which is the worst possible time to find out.
    /// </para>
    /// <para>
    /// The <see cref="HttpClient"/> is the storefront's one client, whose base address is the app's
    /// own origin. That is the entire same-origin argument in one parameter: because the API host now
    /// serves this application's files, the app's origin <em>is</em> the API's, so the
    /// <c>HttpOnly; SameSite=Lax</c> session cookie is first-party on every cart call and the browser
    /// sends it without being asked.
    /// </para>
    /// </summary>
    public CartState(HttpClient http, CatalogService catalog, StorefrontState shell, CartDrawerState drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);

        _api = new CartApiClient(http);
        _catalog = catalog;
        _shell = shell;
        _drawer = drawer;

        // The drawer opening is a genuine need for the cart, and it is the need that arrives first
        // for most visitors. Subscribing here rather than making every drawer implementation
        // remember to call EnsureLoadedAsync is what keeps "the cart loads lazily, exactly once,
        // when it is first wanted" a property of this class instead of a convention. Nothing is
        // fetched until this fires.
        _drawer.Changed += OnDrawerChanged;
    }

    /// <summary>Raised after any change to the lines or to <see cref="SyncState"/>. Subscribers must unsubscribe on dispose.</summary>
    public event Action? Changed;

    /// <summary>The lines, in the order the server lists them — which is the order they were added.</summary>
    public IReadOnlyList<CartLine> Lines => _lines;

    /// <summary>True when there is nothing in the cart. Also true before the cart has been fetched, which is why <see cref="SyncState"/> exists.</summary>
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
    /// The cart's currency. The server fixes it from the first item added and refuses to mix
    /// currencies, so a cart has exactly one.
    /// </summary>
    public string Currency => _currency;

    /// <summary>
    /// The sum of every line total at its captured price, in minor units. Summed as integers, and
    /// summed here rather than read from the response's own subtotal so there is one arithmetic path
    /// and not two numbers that can disagree.
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

    /// <summary>Where the conversation with the API has got to.</summary>
    public CartSyncState SyncState { get; private set; } = CartSyncState.Idle;

    /// <summary>
    /// True while a call is taking long enough that a cold start is the likeliest explanation. The
    /// drawer should say so in words — "waking the shop up" — rather than showing a spinner that is
    /// indistinguishable from a broken one.
    /// </summary>
    public bool IsWaking => SyncState == CartSyncState.Waking;

    /// <summary>True while any cart call is in flight, so controls can be disabled without guessing.</summary>
    public bool IsBusy => _busy > 0;

    /// <summary>True once the server has confirmed a cart, so "empty" can be told apart from "not asked yet".</summary>
    public bool IsLoaded => SyncState == CartSyncState.Ready;

    /// <summary>
    /// A sentence for a shopper about the most recent failure, or null when the last call succeeded.
    /// Set independently of <see cref="SyncState"/>: a change that was refused while a good cart is
    /// still on screen leaves the cart usable and the message visible, which is the honest pairing.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The technical line behind <see cref="ErrorMessage"/>, for a disclosure rather than a dialog.</summary>
    public string? ErrorDetail { get; private set; }

    /// <summary>True when the last failure is one that trying again could plausibly fix.</summary>
    public bool CanRetry { get; private set; }

    /// <summary>True when at least one line's price has moved since it was added. The drawer must say so.</summary>
    public bool HasPriceChanges
    {
        get
        {
            foreach (var line in _lines)
            {
                if (line.PriceChanged)
                    return true;
            }

            return false;
        }
    }

    /// <summary>True when a line refers to a variant the catalog no longer sells, so checkout will not pass unchanged.</summary>
    public bool HasUnavailableLines
    {
        get
        {
            foreach (var line in _lines)
            {
                if (!line.StillInCatalog)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Retained so the drawer's storage warning still compiles, and now always true: the cart lives
    /// on the server, so there is nothing this browser can refuse to keep.
    /// <para>
    /// The warning it guards is dead and should be deleted along with this property when the drawer
    /// is next touched. It is left in place rather than removed under someone else's file.
    /// </para>
    /// </summary>
    public bool IsPersisted => true;

    /// <summary>The line for a SKU, or null when that SKU is not in the cart.</summary>
    public CartLine? Find(string? sku) =>
        sku is null ? null : _lines.Find(line => string.Equals(line.Sku, sku, StringComparison.Ordinal));

    /// <summary>
    /// Kept so the app shell compiles, and deliberately does nothing.
    /// <para>
    /// It used to read the cart back out of <c>localStorage</c> after the layout's first render.
    /// There is no local cart any more, and turning this into the server fetch would be the single
    /// worst change available: the drawer is mounted in the layout, so it runs on every page load,
    /// which would mean every visitor to a catalog page waking a sleeping API to ask about a cart
    /// they have not started. The cart loads when the drawer opens or when something is added —
    /// see <see cref="EnsureLoadedAsync"/>.
    /// </para>
    /// </summary>
    public Task RestoreAsync() => Task.CompletedTask;

    /// <summary>
    /// Fetches the cart, once, on the first genuine need.
    /// <para>
    /// Safe to await from anywhere that is not a first paint: concurrent callers share the one
    /// in-flight task, and a completed load returns synchronously. A failed load clears itself so the
    /// next call — or <see cref="RetryAsync"/> — starts a fresh attempt.
    /// </para>
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if (SyncState == CartSyncState.Ready)
            return Task.CompletedTask;

        // Share the attempt that is already running; start a new one otherwise. Checked on the task's
        // own completion rather than on a latch this method has to remember to clear, because a
        // latch cleared inside the attempt races the assignment that stores it — and the way that
        // race loses is a permanently cached failed task and a cart that never loads again.
        if (_load is { IsCompleted: false })
            return _load;

        return _load = LoadAsync();
    }

    /// <summary>
    /// Tries the last failed call again. This is what the drawer's retry button calls; a shopper on
    /// a cold API should not have to reload the whole application to get their cart.
    /// </summary>
    public Task RetryAsync()
    {
        ClearError();

        return EnsureLoadedAsync();
    }

    /// <summary>
    /// Adds a variant to the cart.
    /// <para>
    /// The line appears immediately at the quantity asked for, then the server's answer replaces the
    /// whole cart. Two round trips at worst — one to turn the SKU into a variant id, one to add it —
    /// and only on the first add of a given product; after that the id is cached, and every cart
    /// response refreshes the cache anyway.
    /// </para>
    /// </summary>
    /// <param name="product">The product the variant belongs to, for the name, the slug and the id lookup.</param>
    /// <param name="variant">The SKU being added.</param>
    /// <param name="quantity">How many, clamped into 1..<see cref="MaxLineQuantity"/>.</param>
    public void Add(CatalogProduct product, CatalogVariant variant, int quantity = 1)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(variant);

        Add(
            new CartLine
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

        if (line.Sku.Length == 0 || line.Slug.Length == 0)
            return;

        var wanted = Math.Clamp(quantity, 1, MaxLineQuantity);
        var existing = Find(line.Sku);

        // The courtesy cap. The server enforces the real one and would answer a line pushed past 99
        // with a 400 that names the rule; sending that request anyway would cost a cold-start wait to
        // be told something already known here. The increment is trimmed to what will fit, and an
        // add to a line already at the cap is not sent at all — it just re-renders, so the page can
        // say "that line is at the cap".
        var room = existing is null ? MaxLineQuantity : MaxLineQuantity - existing.Quantity;
        if (room <= 0)
        {
            Changed?.Invoke();
            return;
        }

        var sending = Math.Min(wanted, room);

        ApplyOptimisticAdd(line, sending);

        var slug = line.Slug;
        var sku = line.Sku;

        _ = MutateAsync(
            async cancellationToken =>
            {
                var variantId = await ResolveVariantIdAsync(slug, sku, cancellationToken).ConfigureAwait(false);
                return await _api.AddItemAsync(variantId, sending, cancellationToken).ConfigureAwait(false);
            },
            safeToRepeat: false);
    }

    /// <summary>
    /// Sets a line's quantity. Zero or less removes the line, matching what the domain does; above
    /// the cap is clamped to the cap.
    /// </summary>
    public void SetQuantity(string sku, int quantity)
    {
        var index = _lines.FindIndex(line => string.Equals(line.Sku, sku, StringComparison.Ordinal));
        if (index < 0)
            return;

        if (quantity <= 0)
        {
            Remove(sku);
            return;
        }

        var next = Math.Min(quantity, MaxLineQuantity);
        var line = _lines[index];
        if (next == line.Quantity)
            return;

        _lines[index] = line with { Quantity = next, IsPending = true };
        Publish();

        var slug = line.Slug;
        _ = MutateAsync(
            async cancellationToken =>
            {
                var variantId = await ResolveVariantIdAsync(slug, sku, cancellationToken).ConfigureAwait(false);
                return await _api.SetQuantityAsync(variantId, next, cancellationToken).ConfigureAwait(false);
            },
            // The quantity is absolute, so repeating the call cannot drift the number. That is the
            // property the API was designed for and the reason a stepper is safe on a flaky link.
            safeToRepeat: true);
    }

    /// <summary>Removes a line outright. A no-op when the SKU is not in the cart.</summary>
    public void Remove(string sku)
    {
        var index = _lines.FindIndex(line => string.Equals(line.Sku, sku, StringComparison.Ordinal));
        if (index < 0)
            return;

        var line = _lines[index];
        _lines.RemoveAt(index);
        Publish();

        var slug = line.Slug;
        _ = MutateAsync(
            async cancellationToken =>
            {
                var variantId = await ResolveVariantIdAsync(slug, sku, cancellationToken).ConfigureAwait(false);
                return await _api.RemoveItemAsync(variantId, cancellationToken).ConfigureAwait(false);
            },
            // Removing a line that is already gone is a 200 with the cart as it stands, on purpose,
            // so a repeat is harmless.
            safeToRepeat: true);
    }

    /// <summary>Empties the cart. A no-op when it is already empty, so it cannot spuriously re-render.</summary>
    public void Clear()
    {
        if (_lines.Count == 0)
            return;

        _lines.Clear();
        Publish();

        _ = MutateAsync(_api.ClearAsync, safeToRepeat: true);
    }

    /// <summary>Unsubscribes from the drawer. Both objects live for the tab, so this only matters in tests.</summary>
    public void Dispose()
    {
        _drawer.Changed -= OnDrawerChanged;
        _gate.Dispose();
    }

    private void OnDrawerChanged()
    {
        if (_drawer.IsOpen)
        {
            _ = EnsureLoadedAsync();
        }
    }

    /// <summary>
    /// The one read that <see cref="EnsureLoadedAsync"/> shares. It never throws —
    /// <see cref="MutateAsync"/> reports failure through <see cref="ErrorMessage"/> instead — so a
    /// completed task here means "the attempt is over", not "the cart is loaded". Which of the two
    /// happened is <see cref="SyncState"/>'s job to say.
    /// </summary>
    private Task LoadAsync() => MutateAsync(_api.GetCartAsync, safeToRepeat: true);

    /// <summary>
    /// Runs one cart call, applies whatever the server said, and never lets an exception escape into
    /// a fire-and-forget task.
    /// <para>
    /// Calls are queued rather than overlapped, because the cart endpoints are read-modify-write with
    /// no row lock and two in-flight requests from one session can lose an update.
    /// </para>
    /// </summary>
    private async Task MutateAsync(Func<CancellationToken, Task<CartDocument>> call, bool safeToRepeat)
    {
        _busy++;
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Ready survives a queued call: the lines already on screen are the server's, and
            // demoting to Loading would flash a spinner over a cart that is perfectly readable.
            if (SyncState is CartSyncState.Idle or CartSyncState.Failed)
            {
                SetSyncState(CartSyncState.Loading);
            }

            var cart = await CallAsync(call, safeToRepeat).ConfigureAwait(false);

            await ApplyAsync(cart).ConfigureAwait(false);
            ClearError();
            SetSyncState(CartSyncState.Ready);
        }
        catch (CartApiException exception)
        {
            await FailAsync(exception, safeToRepeat).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A fire-and-forget task that throws is an unobserved exception and a cart that has
            // silently stopped working. Everything lands somewhere the shopper can see it.
            Fail(
                "Something went wrong while talking to the shop.",
                $"{exception.GetType().Name}: {exception.Message}",
                canRetry: true);
        }
        finally
        {
            _busy--;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Applies the retry policy described on <see cref="ReadDeadlines"/> and
    /// <see cref="WriteDeadlines"/>, and flips the UI into <see cref="CartSyncState.Waking"/> as soon
    /// as an attempt looks like a cold start rather than a request.
    /// </summary>
    private async Task<CartDocument> CallAsync(Func<CancellationToken, Task<CartDocument>> call, bool safeToRepeat)
    {
        var deadlines = safeToRepeat ? ReadDeadlines : WriteDeadlines;
        CartApiException? last = null;

        for (var attempt = 0; attempt < deadlines.Length; attempt++)
        {
            using var deadline = new CancellationTokenSource(deadlines[attempt]);

            try
            {
                return await WithWakeNoticeAsync(call(deadline.Token)).ConfigureAwait(false);
            }
            catch (CartApiException exception) when (exception.Retryable)
            {
                last = exception;
            }
        }

        throw last ?? new CartApiException(
            "The shop did not answer.",
            "Every attempt passed its deadline without a response.",
            statusCode: null,
            retryable: true);
    }

    /// <summary>
    /// Awaits a call, and says "waking the shop up" out loud once it has been slow for
    /// <see cref="WakeNotice"/>. Racing a timer rather than showing the notice up front matters: the
    /// warm case is a few tens of milliseconds, and announcing a cold start on every add would make
    /// the honest message meaningless.
    /// </summary>
    private async Task<CartDocument> WithWakeNoticeAsync(Task<CartDocument> work)
    {
        if (!work.IsCompleted)
        {
            var first = await Task.WhenAny(work, Task.Delay(WakeNotice)).ConfigureAwait(false);
            if (!ReferenceEquals(first, work))
            {
                SetSyncState(CartSyncState.Waking);
            }
        }

        return await work.ConfigureAwait(false);
    }

    /// <summary>
    /// Reports a failed call, and — when the call was a write — finds out what actually happened.
    /// <para>
    /// This is the honest half of the no-retry rule on adds. A write whose response was lost may or
    /// may not have been applied, and guessing either way puts a wrong number on screen. A plain
    /// re-read is safe, cheap against a now-warm API, and answers the question, so the shopper is
    /// shown the real cart alongside the message about what failed.
    /// </para>
    /// </summary>
    private async Task FailAsync(CartApiException exception, bool safeToRepeat)
    {
        Fail(exception.Message, exception.Detail, exception.Retryable);

        if (safeToRepeat)
        {
            return;
        }

        try
        {
            var cart = await CallAsync(_api.GetCartAsync, safeToRepeat: true).ConfigureAwait(false);
            await ApplyAsync(cart).ConfigureAwait(false);

            // The cart on screen is now the server's, so it is trustworthy — but the message about
            // the change that did not go through stays, because it is still true.
            SetSyncState(CartSyncState.Ready);
        }
        catch (CartApiException)
        {
            // The shop is not answering at all. The message already set says so, and the optimistic
            // lines stay on screen marked pending rather than being wiped, which would look like the
            // cart had been emptied.
        }
    }

    /// <summary>
    /// Replaces the whole cart with the server's answer.
    /// <para>
    /// Wholesale, never merged. The server knows the quantities, the captured prices and the live
    /// prices; the client knew a guess. Merging would preserve the guess in whichever field the
    /// server happened not to mention, which is how a cart ends up with a total nobody can explain.
    /// </para>
    /// </summary>
    private async Task ApplyAsync(CartDocument cart)
    {
        await EnsurePlacementsAsync().ConfigureAwait(false);

        _currency = NormaliseCurrency(cart.Currency);

        var lines = new List<CartLine>(cart.Lines?.Count ?? 0);

        foreach (var line in cart.Lines ?? [])
        {
            if (line.Sku.Length == 0)
                continue;

            if (line.VariantId != Guid.Empty)
            {
                // Free refill of the id cache: every cart response names both identities for every
                // line, so after one load no quantity change or removal needs a catalog lookup.
                _variantIds[line.Sku] = line.VariantId;
            }

            var placement = Locate(line.Sku, line.DisplayName);

            lines.Add(new CartLine
            {
                Sku = line.Sku,
                VariantId = line.VariantId,
                Slug = placement.Slug,
                ProductName = placement.ProductName,
                VariantName = placement.VariantName,
                UnitPriceMinorUnits = line.UnitPrice?.Amount ?? 0,
                CurrentUnitPriceMinorUnits = line.CurrentUnitPrice?.Amount,
                Quantity = line.Quantity,
                Currency = line.UnitPrice?.Currency is { Length: 3 } lineCurrency
                    ? lineCurrency.ToUpperInvariant()
                    : _currency,
            });
        }

        _lines = lines;
        _shell.SetCartItemCount(TotalQuantity, confirmed: true);
    }

    /// <summary>
    /// Puts the line on screen before the server has been asked, so a button click paints in the
    /// frame it happened in. The guess is marked pending and lives exactly until
    /// <see cref="ApplyAsync"/> throws it away.
    /// </summary>
    private void ApplyOptimisticAdd(CartLine line, int quantity)
    {
        var index = _lines.FindIndex(existing => string.Equals(existing.Sku, line.Sku, StringComparison.Ordinal));

        if (index >= 0)
        {
            var existing = _lines[index];
            _lines[index] = existing with
            {
                Quantity = Math.Min(MaxLineQuantity, existing.Quantity + quantity),
                IsPending = true,
            };
        }
        else
        {
            if (_lines.Count == 0 && line.Currency.Length > 0)
            {
                _currency = NormaliseCurrency(line.Currency);
            }

            _lines.Add(line with
            {
                Quantity = quantity,
                Currency = _currency,
                // The captured price is a guess too: the server charges the catalog's current price,
                // which may already have moved. It is shown because a line with no price at all reads
                // as broken, and it is replaced within one round trip.
                CurrentUnitPriceMinorUnits = line.UnitPriceMinorUnits,
                IsPending = true,
            });
        }

        Publish();
    }

    /// <summary>
    /// Turns a SKU into the variant id the cart endpoints address lines by, from the cache when
    /// possible and from the catalog API when not.
    /// </summary>
    private async Task<Guid> ResolveVariantIdAsync(string slug, string sku, CancellationToken cancellationToken)
    {
        if (_variantIds.TryGetValue(sku, out var cached))
        {
            return cached;
        }

        var ids = await _api.GetVariantIdsAsync(slug, cancellationToken).ConfigureAwait(false);
        foreach (var (variantSku, id) in ids)
        {
            _variantIds[variantSku] = id;
        }

        if (_variantIds.TryGetValue(sku, out var resolved))
        {
            return resolved;
        }

        // The snapshot says this SKU exists and the live catalog says it does not. That is a real
        // answer, not a transport failure: the variant has been withdrawn since the snapshot was
        // generated, and no amount of retrying will bring it back.
        throw new CartApiException(
            "That item is no longer in the catalog.",
            $"SKU {sku} is in the catalog snapshot this storefront browses from, but the live catalog "
            + $"has no such variant of {slug}. The snapshot is regenerated at build time, so it can "
            + "describe a shop that has moved on.",
            statusCode: null,
            retryable: false);
    }

    /// <summary>
    /// Builds the SKU index used to put a product name, a variant name and a slug back on a server
    /// line.
    /// <para>
    /// The API answers with one composed display name and no slug, because a cart line stores what
    /// the product was called when it was added and the API has no opinion about the storefront's
    /// routes. The snapshot has all three, and loading it is free of the constraint this whole
    /// storefront is built around: it is a static file on this origin, so it cannot be asleep.
    /// </para>
    /// </summary>
    private async Task EnsurePlacementsAsync()
    {
        if (_placements is not null)
            return;

        await _catalog.EnsureLoadedAsync().ConfigureAwait(false);

        if (_catalog.Snapshot is not { } snapshot)
        {
            // Left null so a later apply tries again. The fallback below still produces a readable
            // line, so a failed snapshot costs a link, not a cart.
            return;
        }

        var placements = new Dictionary<string, CatalogPlacement>(StringComparer.Ordinal);
        foreach (var product in snapshot.Products)
        {
            foreach (var variant in product.Variants)
            {
                placements[variant.Sku] = new CatalogPlacement(product.Slug, product.Name, variant.Name);
            }
        }

        _placements = placements;
    }

    /// <summary>
    /// Finds a SKU in the snapshot, or takes the server's composed name apart when it is not there —
    /// which happens for a variant withdrawn since the snapshot was generated. Those lines still have
    /// to render, and render legibly, because they are exactly the ones a shopper needs to act on.
    /// </summary>
    private CatalogPlacement Locate(string sku, string displayName)
    {
        if (_placements is { } placements && placements.TryGetValue(sku, out var placement))
        {
            return placement;
        }

        // The server joins the two names with a spaced em dash. Splitting it back is a fallback, not
        // a contract: getting it wrong costs a slightly odd label on a line for a product that no
        // longer exists.
        var separator = displayName.IndexOf(" — ", StringComparison.Ordinal);

        return separator < 0
            ? new CatalogPlacement(Slug: "", displayName, VariantName: "")
            : new CatalogPlacement(
                Slug: "",
                displayName[..separator],
                displayName[(separator + 3)..]);
    }

    /// <summary>Badge first so the header is never a frame behind the drawer, then the drawer itself.</summary>
    private void Publish()
    {
        _shell.SetCartItemCount(TotalQuantity);
        Changed?.Invoke();
    }

    private void SetSyncState(CartSyncState state)
    {
        if (SyncState == state)
            return;

        SyncState = state;
        Changed?.Invoke();
    }

    private void Fail(string message, string? detail, bool canRetry)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
        CanRetry = canRetry;

        // Only demote to Failed when there is nothing trustworthy on screen. A refused change over a
        // cart the server has already confirmed leaves the cart readable and the message visible.
        if (SyncState != CartSyncState.Ready)
        {
            SetSyncState(CartSyncState.Failed);
        }
    }

    private void ClearError()
    {
        ErrorMessage = null;
        ErrorDetail = null;
        CanRetry = false;
    }

    /// <summary>
    /// A currency code we are willing to price a cart in. Anything else reaches
    /// <see cref="MoneyFormatter"/> as junk, so it falls back to the catalog's own currency.
    /// </summary>
    private string NormaliseCurrency(string? currency)
    {
        if (currency is not { Length: 3 })
            return _catalog.Currency;

        foreach (var character in currency)
        {
            if (!char.IsAsciiLetter(character))
                return _catalog.Currency;
        }

        return currency.ToUpperInvariant();
    }

    /// <summary>Where a SKU sits in the catalog: the route to it, and the two names that make up its label.</summary>
    private readonly record struct CatalogPlacement(string Slug, string ProductName, string VariantName);
}
