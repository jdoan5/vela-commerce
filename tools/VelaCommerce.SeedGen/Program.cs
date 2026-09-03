using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VelaCommerce.SeedGen;

// Generates the Vela Commerce demo catalog twice over, for its two very different readers.
//
//   catalog.seed.json      indented, complete, committed and read in review; the importer's
//                          input, so it carries stock and the attribution manifest.
//   catalog.snapshot.json  minified, stock-free, served to every visitor as a static file.
//                          It is the whole reason the storefront can browse, search, filter
//                          and sort with the API and database switched off.
//
// Both outputs are committed, so both must be byte-identical between runs: fixed random seed,
// no clock, no Guid.NewGuid, sorted attribute keys, and LF line endings written explicitly
// (System.Text.Json indents with Environment.NewLine, which would otherwise put CRLF in the
// seed file on Windows and produce a whole-file diff on the next macOS or CI run).
//
// Usage: SeedGen [seed-path] [snapshot-path]

var seedPath = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? Path.GetFullPath(args[0])
    : RepoPaths.DefaultSeedPath();

// Defaulted from the repository root rather than the working directory: the storefront reads
// this file from one fixed place, and a run from the wrong folder should not quietly leave it
// stale. Pass a path explicitly to write it somewhere else, as CI does when it only wants to
// compare two runs.
var snapshotPath = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
    ? Path.GetFullPath(args[1])
    : RepoPaths.DefaultSnapshotPath();

var catalog = CatalogGenerator.Generate();
var snapshot = CatalogSnapshotBuilder.From(catalog);

var seedJson = JsonSerializer.Serialize(catalog, SeedJson.Options).ReplaceLineEndings("\n") + "\n";
var snapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJson.Options) + "\n";

WriteUtf8(seedPath, seedJson);
WriteUtf8(snapshotPath, snapshotJson);

var metadata = catalog.Metadata;
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Wrote {metadata.ProductCount} products, {metadata.VariantCount} variants, {metadata.TotalStockUnits} units of stock to {seedPath} (seed {metadata.RandomSeed}; last-unit demo SKU {metadata.LastUnitDemoSku})."));

// The snapshot sits on the first-paint path, so its size is a budget, not a curiosity. The
// compressed figure is the one a visitor actually pays: the CDN serves this file encoded.
var snapshotBytes = Encoding.UTF8.GetBytes(snapshotJson);
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Wrote the client snapshot to {snapshotPath}: {snapshot.Categories.Count} categories, {snapshot.ProductCount} products, {snapshot.VariantCount} variants, no stock. {snapshotBytes.Length:N0} bytes minified, {GzipSize(snapshotBytes):N0} bytes gzipped. Prices {snapshot.MinPrice:N0}-{snapshot.MaxPrice:N0} {snapshot.Currency} minor units."));

static void WriteUtf8(string path, string content)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

static long GzipSize(byte[] content)
{
    using var measured = new MemoryStream();

    using (var gzip = new GZipStream(measured, CompressionLevel.SmallestSize, leaveOpen: true))
        gzip.Write(content);

    return measured.Length;
}
