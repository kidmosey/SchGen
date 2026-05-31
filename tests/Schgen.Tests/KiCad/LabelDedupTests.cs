using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.KiCad;

/// When a passive's host-side pin lands ON a host pin endpoint, BOTH would
/// emit a label of the same net at the same coordinate (one extending each
/// direction). The emitter must collapse these to one label per (coord, net).
public class LabelDedupTests
{
    private const string DeviceLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (property "Reference" "C" (at 0 0 0))
            (property "Value" "C" (at 0 0 0))
            (symbol "C_1_1"
              (pin passive line (at 0  3.81 270) (length 1.27)
                (name "~" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -3.81  90) (length 1.27)
                (name "~" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27)))))))
          (symbol "IC"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "IC" (at 0 0 0))
            (symbol "IC_1_1"
              (rectangle (start -5 -5) (end 5 5))
              (pin power_in line (at  7.62  2.54 180) (length 2.54)
                (name "VCC" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin power_in line (at  7.62 -2.54 180) (length 2.54)
                (name "GND" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27))))))))
        """;

    [Fact]
    public void No_duplicate_labels_at_same_position_with_same_net()
    {
        const string Yaml = """
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:IC
                    pins: { VCC: VCC_U1_1, GND: GND }
                  - ref: C1
                    symbol: Device:C
                    pins: { 1: VCC_U1_1, 2: GND }
            root:
              instantiate: [{ sheet: s }]
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var libs = LibraryIndex.FromLibraries(
            new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var placement = new SchPlacer(doc, libs).Run();
        var emitter = new SchematicEmitter(doc, libs, placement);

        var tmp = Directory.CreateTempSubdirectory("schgen_dedup_").FullName;
        try
        {
            emitter.Write(tmp, "min");
            var sch = (SList)SExpr.Parse(File.ReadAllText(Path.Combine(tmp, "min.kicad_sch")));

            // Collect every (x, y, name) triple across local + global + hier labels.
            var seen = new HashSet<(long, long, string)>();
            var dupes = new List<string>();
            foreach (var head in new[] { "label", "global_label", "hierarchical_label" })
            foreach (var lbl in sch.All(head))
            {
                var name = ((SAtom)lbl.Items[1]).Value;
                var at = lbl.First("at")!;
                double x = double.Parse(((SAtom)at.Items[1]).Value, System.Globalization.CultureInfo.InvariantCulture);
                double y = double.Parse(((SAtom)at.Items[2]).Value, System.Globalization.CultureInfo.InvariantCulture);
                var key = ((long)Math.Round(x * 100), (long)Math.Round(y * 100), name);
                if (!seen.Add(key)) dupes.Add($"{head} '{name}' @ ({x}, {y})");
            }
            dupes.Should().BeEmpty($"found duplicate label entries: {string.Join(", ", dupes)}");
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
