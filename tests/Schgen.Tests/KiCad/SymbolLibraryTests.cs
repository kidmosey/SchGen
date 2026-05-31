using FluentAssertions;
using Schgen.Core.KiCad;
using Xunit;

namespace Schgen.Tests.KiCad;

public class SymbolLibraryTests
{
    private const string SimpleCapacitor = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (pin_numbers hide) (pin_names (offset 0.254) hide)
            (in_bom yes) (on_board yes)
            (property "Reference" "C" (at 0.635 2.54 0))
            (property "Value"     "C" (at 0.635 -2.54 0))
            (symbol "C_1_1"
              (polyline (pts (xy -2.032 -0.762) (xy 2.032 -0.762)))
              (polyline (pts (xy -2.032 0.762)  (xy 2.032 0.762)))
              (pin passive line (at 0 3.81 270) (length 2.794)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -3.81 90) (length 2.794)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "2" (effects (font (size 1.27 1.27))))))))
        """;

    private const string IcWithPowerPins = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "U1"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "U1" (at 0 0 0))
            (symbol "U1_1_1"
              (rectangle (start -5.08 -5.08) (end 5.08 5.08) (stroke (width 0)) (fill (type background)))
              (pin power_in line (at -7.62 2.54 0) (length 2.54)
                (name "VCC" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin power_in line (at -7.62 -2.54 0) (length 2.54)
                (name "GND" (effects (font (size 1.27 1.27))))
                (number "2" (effects (font (size 1.27 1.27)))))
              (pin output line (at 7.62 0 180) (length 2.54)
                (name "OUT" (effects (font (size 1.27 1.27))))
                (number "3" (effects (font (size 1.27 1.27))))))))
        """;

    [Fact]
    public void Parses_passive_2pin_symbol_and_marks_it_passive()
    {
        var lib = SymbolLibrary.FromText(SimpleCapacitor, "Device");
        var c = lib.TryGet("C");
        c.Should().NotBeNull();
        c!.Pins.Should().HaveCount(2);
        c.ReferencePrefix.Should().Be("C");
        c.Is2PinPassive.Should().BeTrue();
    }

    [Fact]
    public void Pin_coordinates_extracted_correctly()
    {
        var lib = SymbolLibrary.FromText(SimpleCapacitor, "Device");
        var pins = lib.TryGet("C")!.Pins;
        var p1 = pins.Single(p => p.Number == "1");
        p1.Y.Should().BeApproximately(3.81, 1e-6);
        p1.Length.Should().BeApproximately(2.794, 1e-6);
    }

    [Fact]
    public void IC_with_power_pins_is_not_passive()
    {
        var lib = SymbolLibrary.FromText(IcWithPowerPins, "Device");
        var u = lib.TryGet("U1")!;
        u.Pins.Should().HaveCount(3);
        u.Is2PinPassive.Should().BeFalse();
        u.Pins.Single(p => p.Name == "VCC").Type.Should().Be(PinType.PowerIn);
        u.Pins.Single(p => p.Name == "OUT").Type.Should().Be(PinType.Output);
    }

    [Fact]
    public void BBox_includes_rectangle_geometry()
    {
        var lib = SymbolLibrary.FromText(IcWithPowerPins, "Device");
        var u = lib.TryGet("U1")!;
        u.BoundingBox.MinX.Should().BeLessThanOrEqualTo(-5.08);
        u.BoundingBox.MaxX.Should().BeGreaterThanOrEqualTo(5.08);
    }

    [Fact]
    public void Throws_on_invalid_root()
    {
        Action act = () => SymbolLibrary.FromText("(not_a_lib)", "x");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void BBox_offset_translates_correctly()
    {
        var b = new BBox(-5, -5, 5, 5).Offset(10, 20);
        b.MinX.Should().Be(5);
        b.MinY.Should().Be(15);
        b.MaxX.Should().Be(15);
        b.MaxY.Should().Be(25);
    }

    [Fact]
    public void BBox_overlap_detection()
    {
        var a = new BBox(0, 0, 10, 10);
        var b = new BBox(5, 5, 15, 15);
        var c = new BBox(20, 20, 30, 30);
        a.Overlaps(b).Should().BeTrue();
        a.Overlaps(c).Should().BeFalse();
    }

    // A 3-unit chip exercised by SymbolLibrary's per-unit parsing. The
    // sub-symbol name encodes (unitNumber, bodyStyle): U_MULTI_1_1,
    // U_MULTI_2_1, U_MULTI_3_1.
    private const string MultiUnitLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "U_MULTI"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "U_MULTI" (at 0 0 0))
            (symbol "U_MULTI_1_1"
              (rectangle (start -3 -3) (end 3 3))
              (pin power_in line (at -5.08 2.54 0) (length 2.54)
                (name "VCC" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27))))))
            (symbol "U_MULTI_2_1"
              (rectangle (start -4 -4) (end 4 4))
              (pin bidirectional line (at -6 2 0) (length 2)
                (name "IO0" (effects (font (size 1.27 1.27))))
                (number "10" (effects (font (size 1.27 1.27))))))
            (symbol "U_MULTI_3_1"
              (rectangle (start -2 -2) (end 2 2))
              (pin input line (at -4 0 0) (length 2)
                (name "CLK" (effects (font (size 1.27 1.27))))
                (number "20" (effects (font (size 1.27 1.27))))))))
        """;

    [Fact]
    public void Multi_unit_symbol_reports_correct_unit_count()
    {
        var lib = SymbolLibrary.FromText(MultiUnitLib, "Device");
        var u = lib.TryGet("U_MULTI")!;
        u.IsMultiUnit.Should().BeTrue();
        u.UnitCount.Should().Be(3);
        u.PinsByUnit.Keys.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void PinsOfUnit_returns_only_that_units_pins()
    {
        var lib = SymbolLibrary.FromText(MultiUnitLib, "Device");
        var u = lib.TryGet("U_MULTI")!;
        u.PinsOfUnit(1).Should().ContainSingle(p => p.Number == "1" && p.Name == "VCC");
        u.PinsOfUnit(2).Should().ContainSingle(p => p.Number == "10" && p.Name == "IO0");
        u.PinsOfUnit(3).Should().ContainSingle(p => p.Number == "20" && p.Name == "CLK");
        u.PinsOfUnit(99).Should().BeEmpty();
    }

    [Fact]
    public void Per_unit_bbox_isolates_each_units_geometry()
    {
        var lib = SymbolLibrary.FromText(MultiUnitLib, "Device");
        var u = lib.TryGet("U_MULTI")!;
        // Unit 1 has a 6x6 rectangle, unit 2 has 8x8, unit 3 has 4x4.
        u.UnitBBox(1).Width.Should().BeApproximately(6 + 2.54, 0.5);   // +pin length
        u.UnitBBox(2).Width.Should().BeApproximately(8 + 2, 0.5);
        u.UnitBBox(3).Width.Should().BeApproximately(4 + 2, 0.5);
    }

    // Symbol with both `_0_1` (shared/common) and `_1_1` (unit 1). KiCad's
    // rendering convention is that `_0_1` pins overlay every other unit's
    // drawing, so PinsOfUnit(1) returns unit-1's own pins PLUS unit-0's
    // shared pins. Unit-0 is also accessible on its own under key 0 for
    // YAML that declares `unit: 0`.
    private const string UnitZeroSpatialLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "BGA"
            (property "Reference" "U" (at 0 0 0))
            (property "Value" "BGA" (at 0 0 0))
            (symbol "BGA_0_1"
              (pin power_in line (at -2.54 45.72 270) (length 2.54)
                (name "VDD" (effects (font (size 1.27 1.27))))
                (number "K2" (effects (font (size 1.27 1.27))))))
            (symbol "BGA_1_1"
              (rectangle (start -5 -5) (end 5 5))
              (pin input line (at -7 0 0) (length 2)
                (name "DQ0" (effects (font (size 1.27 1.27))))
                (number "A2" (effects (font (size 1.27 1.27))))))))
        """;

    [Fact]
    public void Unit_zero_shared_pins_overlay_other_units()
    {
        var lib = SymbolLibrary.FromText(UnitZeroSpatialLib, "Device");
        var u = lib.TryGet("BGA")!;
        // Unit 1 sees its own A2 plus the shared K2 from unit 0.
        u.PinsOfUnit(1).Should().Contain(p => p.Number == "A2");
        u.PinsOfUnit(1).Should().Contain(p => p.Number == "K2");
        // Unit 0 standalone sees only its own pins (no double-count).
        u.PinsOfUnit(0).Should().ContainSingle(p => p.Number == "K2");
        u.PinsOfUnit(0).Should().NotContain(p => p.Number == "A2");
    }

    [Fact]
    public void Unit_zero_is_separately_addressable()
    {
        // Symbols that put their pinout in `_0_1` must be reachable by
        // declaring `unit: 0` in YAML - PinsByUnit must surface a 0 key.
        var lib = SymbolLibrary.FromText(UnitZeroSpatialLib, "Device");
        var u = lib.TryGet("BGA")!;
        u.PinsByUnit.Keys.Should().Contain(0);
        u.PinsByUnit.Keys.Should().Contain(1);
    }

    // Symbol whose ONLY sub-symbol is `_0_1` (typical of single-unit
    // symbols where the body-style index is `_1` but the unit index is
    // `_0`). Conventional YAML written with `unit: 1` must still resolve.
    private const string OnlyUnitZeroLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "R"
            (property "Reference" "R" (at 0 0 0))
            (property "Value" "R" (at 0 0 0))
            (symbol "R_0_1"
              (pin passive line (at 0 2.5 270) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -2.5 90) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27))))
                (number "2" (effects (font (size 1.27 1.27))))))))
        """;

    [Fact]
    public void Symbol_with_only_unit_zero_subsymbol_is_addressable_as_unit_one()
    {
        var lib = SymbolLibrary.FromText(OnlyUnitZeroLib, "Device");
        var r = lib.TryGet("R")!;
        r.PinsOfUnit(1).Should().HaveCount(2);
        r.PinsOfUnit(1).Select(p => p.Number).Should().BeEquivalentTo(new[] { "1", "2" });
    }

    [Fact]
    public void Loads_kicad10_symdir_directory_as_one_library()
    {
        // KiCad 10 ships stock symbols as `.kicad_symdir` directories with
        // one `.kicad_sym` per symbol inside. SymbolLibrary.Load must merge
        // them into a single library keyed by the directory's basename.
        var tmp = Directory.CreateTempSubdirectory("schgen_symdir_").FullName + ".kicad_symdir";
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "C.kicad_sym"), """
                (kicad_symbol_lib (version 20231120) (generator schgen_test)
                  (symbol "C"
                    (property "Reference" "C" (at 0 0 0))
                    (property "Value" "C" (at 0 0 0))
                    (symbol "C_1_1"
                      (pin passive line (at 0 2.5 270) (length 1.5)
                        (name "~" (effects (font (size 1.27 1.27))))
                        (number "1" (effects (font (size 1.27 1.27))))))))
                """);
            File.WriteAllText(Path.Combine(tmp, "R.kicad_sym"), """
                (kicad_symbol_lib (version 20231120) (generator schgen_test)
                  (symbol "R"
                    (property "Reference" "R" (at 0 0 0))
                    (property "Value" "R" (at 0 0 0))
                    (symbol "R_1_1"
                      (pin passive line (at 0 3 270) (length 1)
                        (name "~" (effects (font (size 1.27 1.27))))
                        (number "1" (effects (font (size 1.27 1.27))))))))
                """);
            var lib = SymbolLibrary.Load(tmp);
            lib.LibraryNickname.Should().Be(Path.GetFileNameWithoutExtension(tmp));
            lib.Symbols.Keys.Should().BeEquivalentTo(new[] { "C", "R" });
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
