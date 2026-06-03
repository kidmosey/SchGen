using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.Placement;

/// End-to-end coverage of multi-unit symbol support:
///   - YAML round-trip when multiple ComponentDefs share a Ref but differ by Unit
///   - SchPlacer emits one ComponentPlacement per (Ref, Unit) - each unit
///     placed independently because each occupies its own page region
///   - PcbPlacer emits exactly ONE FootprintPlacement for the shared Ref
///     (a multi-unit symbol still corresponds to a single physical part)
public class MultiUnitTests
{
    private const string MultiUnitLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "DualOpAmp"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "DualOpAmp" (at 0 0 0))
            (symbol "DualOpAmp_1_1"
              (rectangle (start -3 -3) (end 3 3))
              (pin input  line (at -5  2 0)   (length 2)
                (name "IN+_A" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin input  line (at -5 -2 0)   (length 2)
                (name "IN-_A" (effects (font (size 1.27 1.27))))
                (number "2" (effects (font (size 1.27 1.27)))))
              (pin output line (at  5  0 180) (length 2)
                (name "OUT_A" (effects (font (size 1.27 1.27))))
                (number "3" (effects (font (size 1.27 1.27))))))
            (symbol "DualOpAmp_2_1"
              (rectangle (start -3 -3) (end 3 3))
              (pin input  line (at -5  2 0)   (length 2)
                (name "IN+_B" (effects (font (size 1.27 1.27))))
                (number "5" (effects (font (size 1.27 1.27)))))
              (pin input  line (at -5 -2 0)   (length 2)
                (name "IN-_B" (effects (font (size 1.27 1.27))))
                (number "6" (effects (font (size 1.27 1.27)))))
              (pin output line (at  5  0 180) (length 2)
                (name "OUT_B" (effects (font (size 1.27 1.27))))
                (number "7" (effects (font (size 1.27 1.27))))))))
        """;

    private const string TwoSheetsTwoUnits = """
        sheets:
          stage_a:
            components:
              - ref: U1
                symbol: Device:DualOpAmp
                unit: 1
                footprint: Package_SO:SOIC-8
                pins: { 1: IN_A, 2: GND, 3: MID }
          stage_b:
            components:
              - ref: U1
                symbol: Device:DualOpAmp
                unit: 2
                footprint: Package_SO:SOIC-8
                pins: { 5: MID, 6: GND, 7: OUT }
        root:
          instantiate:
            - sheet: stage_a
            - sheet: stage_b
        """;

    private static (CircuitDocument doc, LibraryIndex libs) LoadDoc()
    {
        var doc = YamlLoader.LoadText(TwoSheetsTwoUnits);
        var libs = LibraryIndex.FromLibraries(
            new[] { SymbolLibrary.FromText(MultiUnitLib, "Device") });
        return (doc, libs);
    }

    [Fact]
    public void Yaml_round_trip_preserves_unit_per_componentdef()
    {
        var (doc, _) = LoadDoc();
        doc.Sheets["stage_a"].Components.Single().Unit.Should().Be(1);
        doc.Sheets["stage_b"].Components.Single().Unit.Should().Be(2);
        // Both ComponentDefs share the same Ref - that's the multi-unit contract.
        doc.Sheets["stage_a"].Components.Single().Ref.Should().Be("U1");
        doc.Sheets["stage_b"].Components.Single().Ref.Should().Be("U1");
    }

    [Fact]
    public void Sch_placer_emits_one_placement_per_unit()
    {
        var (doc, libs) = LoadDoc();
        var sch = new SchPlacer(doc, libs).Run();
        var stageA = sch.Sheets["stage_a"];
        var stageB = sch.Sheets["stage_b"];
        stageA.FindByRef("U1", unit: 1).Should().NotBeNull();
        stageA.FindByRef("U1", unit: 2).Should().BeNull(
            because: "unit 2 belongs to stage_b, not stage_a");
        stageB.FindByRef("U1", unit: 2).Should().NotBeNull();
        stageB.FindByRef("U1", unit: 1).Should().BeNull();
    }

    [Fact]
    public void Pcb_placer_emits_exactly_one_footprint_for_multiunit_ref()
    {
        var (doc, libs) = LoadDoc();
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();
        pcb.Footprints.Should().ContainSingle(f => f.Ref == "U1",
            because: "a multi-unit symbol is one physical part with one footprint");
    }

    [Fact]
    public void Pcb_placer_merges_padtonets_from_every_unit()
    {
        // U1 has pads from both unit 1 (1,2,3) and unit 2 (5,6,7). The
        // merged map on the single FootprintPlacement must contain entries
        // from BOTH units.
        var (doc, libs) = LoadDoc();
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();
        var u1 = pcb.Footprints.Single(f => f.Ref == "U1");
        u1.PadToNet.Should().ContainKey("1");   // unit 1
        u1.PadToNet.Should().ContainKey("5");   // unit 2
        u1.PadToNet["1"].Should().Be("IN_A");
        u1.PadToNet["5"].Should().Be("MID");
    }
}
