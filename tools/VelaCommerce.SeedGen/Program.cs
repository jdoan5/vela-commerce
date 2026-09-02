using System.Globalization;
using System.Text;
using System.Text.Json;
using VelaCommerce.SeedGen;

// Generates the Vela Commerce demo catalog as JSON for the importer to load.
//
// The output is committed, so it must be byte-identical between runs: fixed random seed,
// no clock, no Guid.NewGuid, sorted attribute keys, and LF line endings written explicitly
// (System.Text.Json indents with Environment.NewLine, which would otherwise put CRLF in the
// file on Windows and produce a whole-file diff on the next macOS or CI run).

var outputPath = Path.GetFullPath(
    args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : "catalog.seed.json");

var directory = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(directory))
    Directory.CreateDirectory(directory);

var catalog = CatalogGenerator.Generate();

var json = JsonSerializer.Serialize(catalog, SeedJson.Options).ReplaceLineEndings("\n");
File.WriteAllText(outputPath, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

var meta = catalog.Metadata;
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Wrote {meta.ProductCount} products, {meta.VariantCount} variants, {meta.TotalStockUnits} units of stock to {outputPath} (seed {meta.RandomSeed}; last-unit demo SKU {meta.LastUnitDemoSku})."));
