using Schgen.Core.Bom;
using Schgen.Core.Yaml;

namespace Schgen.Cli.Commands;

public static class BomCommand
{
    public static int Run(string[] args)
    {
        string? input = null, outPath = null, variant = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o":
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out needs a path"); return 2; }
                    outPath = args[++i];
                    break;
                case "--variant":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --variant needs a name"); return 2; }
                    variant = args[++i];
                    break;
                default:
                    if (input is null) input = args[i];
                    else { Console.Error.WriteLine($"error: unexpected argument '{args[i]}'"); return 2; }
                    break;
            }
        }
        if (input is null)
        {
            Console.Error.WriteLine("usage: schgen bom <circuit.yaml> [--variant <name>] [-o <out.csv>]");
            return 2;
        }

        var doc = YamlLoader.Load(input);
        YamlLoader.ExpandTemplateInstances(doc);
        if (variant is not null) YamlLoader.FilterVariant(doc, variant);
        YamlLoader.ConsolidateMultiUnitFields(doc);

        var lines = BomBuilder.Build(doc);
        var csv = BomBuilder.ToCsv(lines);
        if (outPath is null) Console.Out.Write(csv);
        else File.WriteAllText(outPath, csv);

        int placements = lines.Where(l => !l.Dnp).Sum(l => l.Qty);
        int missing = lines.Count(l => string.IsNullOrEmpty(l.Mpn));
        Console.Error.WriteLine(
            $"schgen bom: {lines.Count} line items, {placements} placements" +
            (variant is null ? "" : $" (variant {variant})") +
            (missing > 0 ? $", {missing} without MPN" : ", all sourced"));
        return 0;
    }
}
