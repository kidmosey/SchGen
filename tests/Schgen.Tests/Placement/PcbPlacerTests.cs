using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.Placement;

public class PcbPlacerTests
{
    private const string DeviceLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (property "Reference" "C" (at 0 0 0))
            (property "Value" "C" (at 0 0 0))
            (symbol "C_1_1"
              (pin passive line (at 0  2.5 270) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -2.5  90) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27)))))))
          (symbol "Regulator"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "Regulator" (at 0 0 0))
            (symbol "Regulator_1_1"
              (rectangle (start -5 -5) (end 5 5))
              (pin power_in  line (at -7.62  2.54 0) (length 2.54)
                (name "VIN"  (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin power_in  line (at -7.62 -2.54 0) (length 2.54)
                (name "GND"  (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27)))))
              (pin power_out line (at  7.62  0    180) (length 2.54)
                (name "VOUT" (effects (font (size 1.27 1.27)))) (number "3" (effects (font (size 1.27 1.27))))))))
        """;

    private const string Yaml = """
        sheets:
          s:
            components:
              - ref: U1
                symbol: Device:Regulator
                footprint: Package_TO:SOT-23
                pins: { VIN: VBUS, GND: GND, VOUT: VCC }
              - ref: C1
                symbol: Device:C
                footprint: Capacitor_SMD:C_0805
                pins: { 1: VBUS, 2: GND }
              - ref: J_EDGE
                symbol: Device:Regulator
                footprint: Connector:DSUB-9
                pcb_at: [100.0, 50.0]
                pcb_rotate: 90
                pins: { VIN: VBUS, GND: GND, VOUT: VCC }
        root:
          instantiate: [{ sheet: s }]
        """;

    [Fact]
    public void Honours_pcb_at_override_exactly()
    {
        var doc = YamlLoader.LoadText(Yaml);
        var libs = LibraryIndex.FromLibraries(new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();
        var je = pcb.Footprints.Single(f => f.Ref == "J_EDGE");
        je.X.Should().Be(100.0);
        je.Y.Should().Be(50.0);
        je.Rotation.Should().Be(90);
    }

    [Fact]
    public void Emits_pcb_file_with_nets()
    {
        var doc = YamlLoader.LoadText(Yaml);
        var libs = LibraryIndex.FromLibraries(new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();
        var emitter = new PcbEmitter(doc, libs, pcb);
        var tmp = Directory.CreateTempSubdirectory("schgen_pcb_").FullName;
        try
        {
            emitter.Write(tmp, "minimal");
            var path = Path.Combine(tmp, "minimal.kicad_pcb");
            File.Exists(path).Should().BeTrue();
            var root = (SList)SExpr.Parse(File.ReadAllText(path));
            root.Head.Should().Be("kicad_pcb");
            root.All("net").Should().NotBeEmpty();
            root.All("footprint").Should().HaveCount(3);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
