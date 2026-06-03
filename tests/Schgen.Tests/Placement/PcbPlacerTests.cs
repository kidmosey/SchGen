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
                pins: { VIN: VBUS, GND: GND, VOUT: VCC }
        root:
          instantiate: [{ sheet: s }]
        """;

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

    /// Components on two separate sheets land in DIFFERENT clusters. The two
    /// SoC stand-ins (U_A in sheet `a`, U_B in sheet `b`) sit far enough apart
    /// that neither's decoupling caps end up adjacent to the wrong SoC.
    [Fact]
    public void Sheets_become_separate_clusters()
    {
        const string yaml = """
            sheets:
              a:
                components:
                  - ref: U_A
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS_A, GND: GND, VOUT: VCC_A }
                  - ref: C_A
                    symbol: Device:C
                    footprint: Capacitor_SMD:C_0805
                    pins: { 1: VCC_A, 2: GND }
              b:
                components:
                  - ref: U_B
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS_B, GND: GND, VOUT: VCC_B }
                  - ref: C_B
                    symbol: Device:C
                    footprint: Capacitor_SMD:C_0805
                    pins: { 1: VCC_B, 2: GND }
            root:
              instantiate: [{ sheet: a }, { sheet: b }]
            """;
        var doc = YamlLoader.LoadText(yaml);
        var libs = LibraryIndex.FromLibraries(new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();

        var uA = pcb.Footprints.Single(f => f.Ref == "U_A");
        var cA = pcb.Footprints.Single(f => f.Ref == "C_A");
        var uB = pcb.Footprints.Single(f => f.Ref == "U_B");
        var cB = pcb.Footprints.Single(f => f.Ref == "C_B");

        // Each sheet's cap sits closer to its OWN sheet's SoC than to the
        // other sheet's SoC.
        Distance(cA, uA).Should().BeLessThan(Distance(cA, uB));
        Distance(cB, uB).Should().BeLessThan(Distance(cB, uA));
    }

    /// All four "mezzanine" connectors in one sheet cluster tightly together
    /// regardless of which other refs they share nets with elsewhere.
    [Fact]
    public void Mezzanine_sheet_clusters_tightly()
    {
        const string yaml = """
            sheets:
              soc:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
                  - ref: C1
                    symbol: Device:C
                    footprint: Capacitor_SMD:C_0805
                    pins: { 1: VCC, 2: GND }
                  - ref: C2
                    symbol: Device:C
                    footprint: Capacitor_SMD:C_0805
                    pins: { 1: VCC, 2: GND }
              mezzanine:
                components:
                  - ref: M1A
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
                  - ref: M1B
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
                  - ref: M2A
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
                  - ref: M2B
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
            root:
              instantiate: [{ sheet: soc }, { sheet: mezzanine }]
            """;
        var doc = YamlLoader.LoadText(yaml);
        var libs = LibraryIndex.FromLibraries(new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();

        var m1a = pcb.Footprints.Single(f => f.Ref == "M1A");
        var m1b = pcb.Footprints.Single(f => f.Ref == "M1B");
        var m2a = pcb.Footprints.Single(f => f.Ref == "M2A");
        var m2b = pcb.Footprints.Single(f => f.Ref == "M2B");
        var u1  = pcb.Footprints.Single(f => f.Ref == "U1");

        // Mezzanine connectors cluster within a small mutual envelope.
        double diameter = new[]
        {
            Distance(m1a, m1b), Distance(m1a, m2a), Distance(m1a, m2b),
            Distance(m1b, m2a), Distance(m1b, m2b), Distance(m2a, m2b),
        }.Max();
        diameter.Should().BeLessThan(25.0, "all four mezzanine connectors live in one cluster");

        // The mezzanine cluster sits at a meaningful distance from the SoC
        // anchor — they don't all collapse on top of U1.
        Distance(m1a, u1).Should().BeGreaterThan(15.0);
    }

    /// A sheet whose components are board-edge connectors (USB, microSD)
    /// gets tagged as an edge cluster and placed at the board edge, not in
    /// the radial flow around the main anchor.
    [Fact]
    public void Edge_cluster_places_at_board_edge()
    {
        const string yaml = """
            sheets:
              soc:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
                  - ref: C1
                    symbol: Device:C
                    footprint: Capacitor_SMD:C_0805
                    pins: { 1: VCC, 2: GND }
                  - ref: C2
                    symbol: Device:C
                    footprint: Capacitor_SMD:C_0805
                    pins: { 1: VCC, 2: GND }
              io:
                components:
                  - ref: J1
                    symbol: Connector_USB:USB_C_Receptacle
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
                  - ref: J2
                    symbol: Connector_USB:USB_C_Receptacle
                    footprint: Package_TO:SOT-23
                    pins: { VIN: VBUS, GND: GND, VOUT: VCC }
            root:
              instantiate: [{ sheet: soc }, { sheet: io }]
            """;
        var doc = YamlLoader.LoadText(yaml);
        var libs = LibraryIndex.FromLibraries(new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();

        var j1 = pcb.Footprints.Single(f => f.Ref == "J1");
        var j2 = pcb.Footprints.Single(f => f.Ref == "J2");

        // Board centre is (150, 130) per PcbPlacer constants; edge connectors
        // should land far from centre — at least 80 mm in some axis.
        Math.Max(Math.Abs(j1.X - 150), Math.Abs(j1.Y - 130)).Should().BeGreaterThan(80,
            "J1 is in an edge cluster, expected to sit near a board edge");
        Math.Max(Math.Abs(j2.X - 150), Math.Abs(j2.Y - 130)).Should().BeGreaterThan(80,
            "J2 is in an edge cluster, expected to sit near a board edge");
    }

    /// Synthetic two-sheet regression for the Pro density wall: a sheet with
    /// many anchor-class components in its own cluster should not fail
    /// placement just because another sheet is also dense. The pre-cluster
    /// placer threw `could not find a non-overlapping position` when total
    /// component count exceeded the 12-hop budget around a single global
    /// anchor; this regression keeps that fixed.
    [Fact]
    public void Dense_two_sheet_placement_completes_without_throwing()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("sheets:");
        sb.AppendLine("  soc:");
        sb.AppendLine("    components:");
        sb.AppendLine("      - ref: U1");
        sb.AppendLine("        symbol: Device:Regulator");
        sb.AppendLine("        footprint: Package_TO:SOT-23");
        sb.AppendLine("        pins: { VIN: VBUS, GND: GND, VOUT: VCC }");
        for (int i = 0; i < 60; i++)
        {
            sb.AppendLine($"      - ref: C_S{i}");
            sb.AppendLine("        symbol: Device:C");
            sb.AppendLine("        footprint: Capacitor_SMD:C_0805");
            sb.AppendLine("        pins: { 1: VCC, 2: GND }");
        }
        sb.AppendLine("  power:");
        sb.AppendLine("    components:");
        sb.AppendLine("      - ref: U2");
        sb.AppendLine("        symbol: Device:Regulator");
        sb.AppendLine("        footprint: Package_TO:SOT-23");
        sb.AppendLine("        pins: { VIN: VBUS, GND: GND, VOUT: VCC }");
        for (int i = 0; i < 60; i++)
        {
            sb.AppendLine($"      - ref: C_P{i}");
            sb.AppendLine("        symbol: Device:C");
            sb.AppendLine("        footprint: Capacitor_SMD:C_0805");
            sb.AppendLine("        pins: { 1: VCC, 2: GND }");
        }
        sb.AppendLine("root:");
        sb.AppendLine("  instantiate: [{ sheet: soc }, { sheet: power }]");

        var doc = YamlLoader.LoadText(sb.ToString());
        var libs = LibraryIndex.FromLibraries(new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        var sch = new SchPlacer(doc, libs).Run();

        // No throw + every component placed.
        var pcb = new PcbPlacer(doc, libs, sch).Run();
        pcb.Footprints.Count.Should().Be(122);
    }

    private static double Distance(FootprintPlacement a, FootprintPlacement b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
