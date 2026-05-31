using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.Placement;

public class SchPlacerTests
{
    // Minimal lib: a 3-pin regulator (U) + 2-pin cap (C) + 2-pin resistor (R)
    private const string DeviceLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (property "Reference" "C" (at 0 0 0))
            (property "Value" "C" (at 0 0 0))
            (symbol "C_1_1"
              (polyline (pts (xy -1 -0.5) (xy 1 -0.5)))
              (polyline (pts (xy -1  0.5) (xy 1  0.5)))
              (pin passive line (at 0  2.5 270) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -2.5  90) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27)))))))
          (symbol "R"
            (property "Reference" "R" (at 0 0 0))
            (property "Value" "R" (at 0 0 0))
            (symbol "R_1_1"
              (rectangle (start -1 -2) (end 1 2))
              (pin passive line (at 0  3 270) (length 1)
                (name "~" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -3  90) (length 1)
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

    private static (CircuitDocument doc, LibraryIndex libs) Setup(string yaml)
    {
        var doc = YamlLoader.LoadText(yaml);
        var libs = LibraryIndex.FromLibraries(
            new[] { SymbolLibrary.FromText(DeviceLib, "Device") });
        return (doc, libs);
    }

    [Fact]
    public void Single_component_placed_at_origin()
    {
        var (doc, libs) = Setup("""
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: VCC_3V3 }
            root:
              instantiate: [{ sheet: s }]
            """);
        var placement = new SchPlacer(doc, libs).Run();
        var u1 = placement.Sheets["s"].FindByRef("U1")!;
        u1.BoundingBox.CenterX.Should().BeApproximately(0, 0.001);
        u1.BoundingBox.CenterY.Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void Two_connected_anchors_dont_overlap()
    {
        // Two regulators sharing a net via VOUT->VIN.
        var (doc, libs) = Setup("""
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V, 2: GND, 3: MID }
                  - ref: U2
                    symbol: Device:Regulator
                    pins: { 1: MID, 2: GND, 3: VCC_3V3 }
            root:
              instantiate: [{ sheet: s }]
            """);
        var placement = new SchPlacer(doc, libs).Run();
        var u1 = placement.Sheets["s"].FindByRef("U1")!;
        var u2 = placement.Sheets["s"].FindByRef("U2")!;
        u1.BoundingBox.Overlaps(u2.BoundingBox).Should().BeFalse();
    }

    [Fact]
    public void Cap_on_2endpoint_net_is_placed_adjacent_to_host_pin()
    {
        var (doc, libs) = Setup("""
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V_U1, 2: GND, 3: VCC_3V3 }
                  - ref: C1
                    symbol: Device:C
                    pins: { 1: VBUS_5V_U1, 2: GND }
            root:
              instantiate: [{ sheet: s }]
            """);
        var placement = new SchPlacer(doc, libs).Run();
        var u1 = placement.Sheets["s"].FindByRef("U1")!;
        var c1 = placement.Sheets["s"].FindByRef("C1")!;
        // U1.VIN is at relative (-7.62, 2.54); after placement, its absolute coord:
        var vinX = u1.X + (-7.62);
        var vinY = u1.Y + 2.54;
        var distance = Math.Sqrt(
            Math.Pow(c1.BoundingBox.CenterX - vinX, 2) +
            Math.Pow(c1.BoundingBox.CenterY - vinY, 2));
        // Cap-proximity puts the cap in the same fan-out lane as the host
        // pin, past the host's label. The spacing depends on label width
        // and clearance, but the cap should still be much closer than a
        // generic BFS placement (which would be many tens of mm away).
        distance.Should().BeLessThan(40);
    }

    [Fact]
    public void Multiple_caps_on_same_host_pin_fan_out_outward()
    {
        // Three caps all attach to U1.VIN via the shared net VIN_U1_1. The
        // placer's attach rule fires for each cap because:
        //   - cap is a 2-pin passive
        //   - the cap pin's net has exactly one non-passive endpoint (U1.VIN)
        // So all three caps are attached to U1.VIN and fan out in a strip
        // along the outward direction of that pin. Their bboxes must not
        // overlap U1 OR each other.
        var (doc, libs) = Setup("""
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VIN_U1_1, 2: GND, 3: VCC_3V3 }
                  - ref: C1
                    symbol: Device:C
                    pins: { 1: VIN_U1_1, 2: GND }
                  - ref: C2
                    symbol: Device:C
                    pins: { 1: VIN_U1_1, 2: GND }
                  - ref: C3
                    symbol: Device:C
                    pins: { 1: VIN_U1_1, 2: GND }
            root:
              instantiate: [{ sheet: s }]
            """);
        var placement = new SchPlacer(doc, libs).Run();
        var u1 = placement.Sheets["s"].FindByRef("U1")!;
        var c1 = placement.Sheets["s"].FindByRef("C1")!;
        var c2 = placement.Sheets["s"].FindByRef("C2")!;
        var c3 = placement.Sheets["s"].FindByRef("C3")!;
        u1.BoundingBox.Overlaps(c1.BoundingBox).Should().BeFalse();
        u1.BoundingBox.Overlaps(c2.BoundingBox).Should().BeFalse();
        u1.BoundingBox.Overlaps(c3.BoundingBox).Should().BeFalse();
        // Caps fan out as a STRIP: no two caps may overlap each other.
        c1.BoundingBox.Overlaps(c2.BoundingBox).Should().BeFalse();
        c1.BoundingBox.Overlaps(c3.BoundingBox).Should().BeFalse();
        c2.BoundingBox.Overlaps(c3.BoundingBox).Should().BeFalse();
        // All three caps must sit on the OUTWARD side of U1.VIN - i.e., the
        // negative X half (VIN's outward direction is -X in the lib).
        var vinX = u1.X + (-7.62);
        c1.BoundingBox.CenterX.Should().BeLessThan(vinX + 1);
        c2.BoundingBox.CenterX.Should().BeLessThan(vinX + 1);
        c3.BoundingBox.CenterX.Should().BeLessThan(vinX + 1);
    }

    [Fact]
    public void Deterministic_across_runs()
    {
        var yaml = """
            sheets:
              s:
                components:
                  - ref: U1
                    symbol: Device:Regulator
                    pins: { 1: VBUS_5V_U1, 2: GND, 3: VCC_3V3 }
                  - ref: U2
                    symbol: Device:Regulator
                    pins: { 1: VCC_3V3, 2: GND, 3: VOUT2 }
                  - ref: C1
                    symbol: Device:C
                    pins: { 1: VBUS_5V_U1, 2: GND }
            root:
              instantiate: [{ sheet: s }]
            """;
        var (doc1, libs1) = Setup(yaml);
        var (doc2, libs2) = Setup(yaml);
        var r1 = new SchPlacer(doc1, libs1).Run();
        var r2 = new SchPlacer(doc2, libs2).Run();
        foreach (var (k, v1) in r1.Sheets["s"].Components)
        {
            var v2 = r2.Sheets["s"].Components[k];
            v1.X.Should().BeApproximately(v2.X, 1e-6, because: $"{k}.X");
            v1.Y.Should().BeApproximately(v2.Y, 1e-6, because: $"{k}.Y");
        }
    }
}
