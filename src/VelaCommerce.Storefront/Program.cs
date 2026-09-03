using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using VelaCommerce.Storefront;
using VelaCommerce.Storefront.Catalog;
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

// The cart is client state, kept in the tab and mirrored into localStorage. It reaches no
// network at all: there is no checkout to hand it to yet, and the shop has to keep working
// with the API switched off. CartDrawerState is only whether the panel is on screen, which
// is why it is not folded into the cart itself.
builder.Services.AddScoped<CartState>();
builder.Services.AddScoped<CartDrawerState>();

await builder.Build().RunAsync();
