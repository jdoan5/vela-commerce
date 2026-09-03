using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using VelaCommerce.Storefront;
using VelaCommerce.Storefront.Catalog;
using VelaCommerce.Storefront.Checkout;
using VelaCommerce.Storefront.Lab;
using VelaCommerce.Storefront.Shell;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The one HttpClient in the application, and it points at the app's own origin — never at
// the API. Everything the storefront paints comes from a static file next to index.html,
// which is what lets the shop open while the API and the database are asleep.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Scoped, which in WebAssembly is one instance per tab: the catalog is fetched and indexed
// once and every later query is answered from memory.
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<StorefrontState>();

// The cart is held by the SERVER now, and this class mirrors its last answer. There is no
// localStorage copy, because two stores of one cart means one of them is wrong and neither
// knows which. It is still never touched on the first-paint path: the catalog is a static
// file, so browsing works with the API switched off, and the cart is fetched on the first
// genuine need — the drawer opening, or an add being pressed.
// CartDrawerState is only whether the panel is on screen, which is why it is not folded in.
builder.Services.AddScoped<CartState>();

// Holds the checkout's idempotency key and address draft for the tab rather than the component.
// Without it the key is minted per page instance, so a shopper who navigates away from a failed
// checkout and comes back places a SECOND order and takes a second payment.
builder.Services.AddStorefrontCheckout();

// Keeps a lab transcript and the cooldown countdown alive across navigation.
builder.Services.AddStorefrontLab();
builder.Services.AddScoped<CartDrawerState>();

await builder.Build().RunAsync();
