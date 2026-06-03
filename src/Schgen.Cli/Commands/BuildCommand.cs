using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;

namespace Schgen.Cli.Commands;

public static class BuildCommand
{
    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        var extraLibs = new List<string>();
        var schematicOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                case "-o":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out needs a path"); return 2; }
                    outDir = args[++i];
                    break;
                case "--lib":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --lib needs a path"); return 2; }
                    extraLibs.Add(args[++i]);
                    break;
                case "--schematic-only":
                case "--sch-only":
                    schematicOnly = true;
                    break;
                default:
                    if (input is null) input = args[i];
                    else { Console.Error.WriteLine($"error: unexpected argument '{args[i]}'"); return 2; }
                    break;
            }
        }
        if (input is null || outDir is null)
        {
            Console.Error.WriteLine("usage: schgen build <circuit.yaml> --out <dir> [--lib <path>]... [--schematic-only]");
            return 2;
        }

        var doc = YamlLoader.Load(input);
        // Template sheets (`template: true`) get expanded into per-instance
        // concrete sheets here, before library loading, so the placer / emit
        // pipeline sees regular sheets only and per-instance refdes stay
        // unique on the PCB.
        YamlLoader.ExpandTemplateInstances(doc);
        // Multi-unit symbols are authored one ComponentDef per unit; KiCad
        // treats Value/Footprint/Datasheet/BOM as symbol-wide, so stamp a
        // single canonical value across all units of each refdes before
        // validation + emit (otherwise units past the first emit empty values
        // and the schematic annotator rejects the part).
        var libs = LibraryIndex.FromDocument(doc, extraLibs);
        // Expand `units: all` components into one entry per symbol unit (needs
        // the library to know each symbol's unit count). Must run before the
        // multi-unit field consolidation + validation.
        YamlLoader.ExpandAllUnits(doc, libs);
        // Re-bind power nets now that all units exist (FromDocument's first pass
        // only saw the single pre-expansion entry's unit 1).
        libs.AutoBindPowerNets(doc);
        YamlLoader.ConsolidateMultiUnitFields(doc);

        var report = Validator.Validate(doc, libs);
        foreach (var w in report.Warnings) Console.Error.WriteLine($"warn: {w}");
        foreach (var e in report.Errors)   Console.Error.WriteLine($"error: {e}");
        if (!report.Ok)
        {
            Console.Error.WriteLine($"validation failed ({report.Errors.Count} error(s))");
            return 1;
        }
        if (doc.Config.StrictErc && report.Warnings.Count > 0)
        {
            Console.Error.WriteLine($"strict_erc: failing on {report.Warnings.Count} warning(s)");
            return 1;
        }

        // Validation passed - apply host: stub-net patches before placement.
        // Validator already verified each host:'s referent + matching net.
        LibraryIndex.ApplyHostStubNets(doc);

        var placer = new SchPlacer(doc, libs);
        // SCHGEN_PLACER_TRACE=1 streams every placer decision to stderr.
        // Use this to diagnose host-anchored caps falling through to the
        // standalone-passives shelf row.
        if (Environment.GetEnvironmentVariable("SCHGEN_PLACER_TRACE") == "1")
            placer.Tracer = new TextWriterPlacementTracer(Console.Error);
        var schPlacement = placer.Run();

        var projectName = Path.GetFileNameWithoutExtension(input);
        new SchematicEmitter(doc, libs, schPlacement).Write(outDir, projectName);

        // --schematic-only stops here, leaving any existing .kicad_pcb in place.
        // Use it to re-emit schematics after a YAML edit without discarding the
        // board outline, zones, vias, and routing the user has done in KiCad on
        // top of an earlier placement (the PCB emitter rewrites the file from
        // scratch). Re-sync the refreshed netlist/values into the board later
        // via KiCad's "Update PCB from Schematic".
        if (!schematicOnly)
        {
            var pcbPlacement = new PcbPlacer(doc, libs, schPlacement).Run();
            new PcbEmitter(doc, libs, pcbPlacement).Write(outDir, projectName);
        }

        Console.Out.WriteLine(
            $"schgen: wrote {projectName} to {outDir}{(schematicOnly ? " (schematic only)" : "")}");
        return 0;
    }
}
