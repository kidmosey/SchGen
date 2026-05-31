using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.KiCad;

public class SchematicEmitterTests
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

    private const string MinYaml = """
        power_nets: [GND, VBUS_5V, VCC_3V3]
        sheets:
          power:
            components:
              - ref: U1
                symbol: Device:Regulator
                footprint: Package_TO:SOT-23
                pins:
                  VIN: VBUS_5V
                  GND: GND
                  VOUT: VCC_3V3
              - ref: C1
                symbol: Device:C
                value: 10uF
                pins:
                  1: VBUS_5V
                  2: GND
        root:
          instantiate:
            - sheet: power
        """;

    private static LibraryIndex Libs() => LibraryIndex.FromLibraries(
        new[] { SymbolLibrary.FromText(DeviceLib, "Device") });

    [Fact]
    public void Emits_files_for_each_sheet_plus_root_plus_project()
    {
        var doc = YamlLoader.LoadText(MinYaml);
        var libs = Libs();
        var placement = new SchPlacer(doc, libs).Run();
        var emitter = new SchematicEmitter(doc, libs, placement);

        var tmp = Directory.CreateTempSubdirectory("schgen_test_").FullName;
        try
        {
            emitter.Write(tmp, "minimal");
            File.Exists(Path.Combine(tmp, "minimal.kicad_pro")).Should().BeTrue();
            File.Exists(Path.Combine(tmp, "minimal.kicad_sch")).Should().BeTrue();
            // Singleton sheet flattens into the root file - no separate power.kicad_sch
            File.Exists(Path.Combine(tmp, "power.kicad_sch")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Sheet_kicad_sch_round_trips_through_sexpr()
    {
        var doc = YamlLoader.LoadText(MinYaml);
        var libs = Libs();
        var placement = new SchPlacer(doc, libs).Run();
        var emitter = new SchematicEmitter(doc, libs, placement);

        var tmp = Directory.CreateTempSubdirectory("schgen_test_").FullName;
        try
        {
            emitter.Write(tmp, "minimal");
            // Singleton flattens - everything is in the root file.
            var text = File.ReadAllText(Path.Combine(tmp, "minimal.kicad_sch"));
            var root = (SList)SExpr.Parse(text);
            root.Head.Should().Be("kicad_sch");
            root.First("paper")!.StringValue.Should().Be("A3");
            root.First("lib_symbols").Should().NotBeNull();
            // Two component instances (U1, C1) plus auto-emitted PWR_FLAGs.
            var instances = root.All("symbol").Where(s => s.First("lib_id") is not null).ToList();
            instances.Should().HaveCountGreaterThanOrEqualTo(2);
            instances.Count(s => s.First("lib_id")!.StringValue == "Device:Regulator").Should().Be(1);
            instances.Count(s => s.First("lib_id")!.StringValue == "Device:C").Should().Be(1);
            // Power nets become global labels; GND appears on both C1.2 and U1.GND
            root.All("global_label").Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Unlisted_pins_emit_no_connect_markers()
    {
        const string Yaml = """
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins:
                      VIN: VBUS_5V
                      # GND and VOUT intentionally unlisted
            root:
              instantiate: [{ sheet: s }]
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var libs = Libs();
        var placement = new SchPlacer(doc, libs).Run();
        var emitter = new SchematicEmitter(doc, libs, placement);
        var tmp = Directory.CreateTempSubdirectory("schgen_test_").FullName;
        try
        {
            emitter.Write(tmp, "min");
            // Singleton flattens - read the root.
            var sch = (SList)SExpr.Parse(File.ReadAllText(Path.Combine(tmp, "min.kicad_sch")));
            sch.All("no_connect").Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Template_emits_hierarchical_labels_on_port_nets()
    {
        const string Yaml = """
            sheets:
              port:
                template: true
                ports:
                  VBUS: { dir: power_in }
                  GND:  { dir: passive }
                components:
                  - ref: J1
                    symbol: Device:Regulator
                    pins:
                      VIN: VBUS
                      GND: GND
                      VOUT: INTERNAL
            root:
              instantiate:
                - { sheet: port, as: P1, params: { VBUS: VBUS_P1 } }
                - { sheet: port, as: P2, params: { VBUS: VBUS_P2 } }
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var libs = Libs();
        // Refdes collision: both P1 and P2 use J1. Make P2 use J2 - patch by
        // editing the parsed doc since the YAML only declares one sheet.
        // We'll use just one instantiate for this test.
        doc.Root.Instantiate.RemoveAt(1);
        var placement = new SchPlacer(doc, libs).Run();
        var emitter = new SchematicEmitter(doc, libs, placement);
        var tmp = Directory.CreateTempSubdirectory("schgen_test_").FullName;
        try
        {
            emitter.Write(tmp, "tpl");
            var sch = (SList)SExpr.Parse(File.ReadAllText(Path.Combine(tmp, "port.kicad_sch")));
            // VBUS and GND are ports; pin endpoints on those nets are hier labels.
            sch.All("hierarchical_label").Should().HaveCountGreaterThanOrEqualTo(2);
            // INTERNAL is not a port -> local label
            sch.All("label").Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
