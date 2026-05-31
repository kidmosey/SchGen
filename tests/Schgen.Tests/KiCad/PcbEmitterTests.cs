using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;
using Xunit;

namespace Schgen.Tests.KiCad;

public class PcbEmitterTests
{
    private const string DeviceSymLib = """
        (kicad_symbol_lib (version 20231120) (generator schgen_test)
          (symbol "C"
            (property "Reference" "C" (at 0 0 0))
            (property "Value" "C" (at 0 0 0))
            (symbol "C_1_1"
              (pin passive line (at 0  2.5 270) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
              (pin passive line (at 0 -2.5  90) (length 1.5)
                (name "~" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27))))))))
        """;

    private const string Cap0805Footprint = """
        (footprint "C_0805"
          (version 20240108)
          (generator "schgen_test")
          (layer "F.Cu")
          (descr "Capacitor SMD 0805")
          (attr smd)
          (property "Reference" "REF**" (at 0 -1.65 0) (layer "F.SilkS") (uuid "00000000-0000-0000-0000-000000000001") (effects (font (size 1 1))))
          (property "Value" "C_0805" (at 0 1.65 0) (layer "F.Fab") (uuid "00000000-0000-0000-0000-000000000002") (effects (font (size 1 1))))
          (fp_line (start -1.55 -0.735) (end 1.55 -0.735) (stroke (width 0.05) (type solid)) (layer "F.CrtYd"))
          (pad "1" smd roundrect (at -0.95 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25))
          (pad "2" smd roundrect (at  0.95 0) (size 1.025 1.4) (layers "F.Cu" "F.Paste" "F.Mask") (roundrect_rratio 0.25)))
        """;

    [Fact]
    public void Embeds_real_pad_geometry_with_injected_nets()
    {
        const string Yaml = """
            sheets:
              s:
                components:
                  - ref: C1
                    symbol: Device:C
                    footprint: Device:C_0805
                    pins: { 1: VBUS_5V, 2: GND }
            root:
              instantiate: [{ sheet: s }]
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var libs = LibraryIndex.FromLibraries(
            symbolLibs: new[] { SymbolLibrary.FromText(DeviceSymLib, "Device") },
            footprintLibs: new[] { MakeFpLib("Device", FootprintDef.FromText(Cap0805Footprint, "C_0805")!) });

        var schPlacement = new SchPlacer(doc, libs).Run();
        var pcbPlacement = new PcbPlacer(doc, libs, schPlacement).Run();
        var emitter = new PcbEmitter(doc, libs, pcbPlacement);

        var tmp = Directory.CreateTempSubdirectory("schgen_pcb_").FullName;
        try
        {
            emitter.Write(tmp, "min");
            var root = (SList)SExpr.Parse(File.ReadAllText(Path.Combine(tmp, "min.kicad_pcb")));

            // One footprint instance.
            var footprints = root.All("footprint").ToList();
            footprints.Should().HaveCount(1);
            var fp = footprints[0];
            ((SAtom)fp.Items[1]).Value.Should().Be("Device:C_0805");

            // Real pad geometry survived: at (-0.95, 0) and (0.95, 0).
            var pads = fp.All("pad").ToList();
            pads.Should().HaveCount(2);
            var pad1 = pads.Single(p => ((SAtom)p.Items[1]).Value == "1");
            var pad1At = pad1.First("at")!;
            ((SAtom)pad1At.Items[1]).Value.Should().Be("-0.95");
            ((SAtom)pad1At.Items[2]).Value.Should().Be("0");

            // Nets injected per pad.
            var pad1Net = pad1.First("net")!;
            pad1Net.Items.Count.Should().BeGreaterThanOrEqualTo(3);
            ((SAtom)pad1Net.Items[2]).Value.Should().Be("VBUS_5V");

            var pad2Net = pads.Single(p => ((SAtom)p.Items[1]).Value == "2").First("net")!;
            ((SAtom)pad2Net.Items[2]).Value.Should().Be("GND");

            // Library-authoring fields stripped.
            fp.First("version").Should().BeNull();
            fp.First("generator").Should().BeNull();

            // Silkscreen / courtyard preserved.
            fp.All("fp_line").Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Pin_name_to_pad_number_translation_works()
    {
        // YAML uses pin NAMES (VBUS, GND) but footprint pads are numbered (1, 2).
        const string SymLib = """
            (kicad_symbol_lib (version 20231120) (generator schgen_test)
              (symbol "C_named"
                (property "Reference" "C" (at 0 0 0))
                (property "Value" "C_named" (at 0 0 0))
                (symbol "C_named_1_1"
                  (pin passive line (at 0 2.5 270) (length 1.5)
                    (name "VBUS" (effects (font (size 1.27 1.27)))) (number "1" (effects (font (size 1.27 1.27)))))
                  (pin passive line (at 0 -2.5 90) (length 1.5)
                    (name "GND" (effects (font (size 1.27 1.27)))) (number "2" (effects (font (size 1.27 1.27))))))))
            """;
        const string Yaml = """
            sheets:
              s:
                components:
                  - ref: C1
                    symbol: Device:C_named
                    footprint: Device:C_0805
                    pins: { VBUS: V5, GND: GND }
            root:
              instantiate: [{ sheet: s }]
            """;
        var doc = YamlLoader.LoadText(Yaml);
        var libs = LibraryIndex.FromLibraries(
            symbolLibs: new[] { SymbolLibrary.FromText(SymLib, "Device") },
            footprintLibs: new[] { MakeFpLib("Device", FootprintDef.FromText(Cap0805Footprint, "C_0805")!) });
        var sch = new SchPlacer(doc, libs).Run();
        var pcb = new PcbPlacer(doc, libs, sch).Run();
        var c1 = pcb.Footprints.Single();
        c1.PadToNet["1"].Should().Be("V5");          // VBUS -> pin number 1
        c1.PadToNet["2"].Should().Be("GND");         // GND -> pin number 2
    }

    private static FootprintLibrary MakeFpLib(string nick, params FootprintDef[] defs)
    {
        // Write the .kicad_mod files into a temp dir whose name (after .pretty
        // strip) is the desired nickname, then load via the normal loader.
        var dir = Path.Combine(Path.GetTempPath(), $"schgen_fp_{Guid.NewGuid():N}", $"{nick}.pretty");
        Directory.CreateDirectory(dir);
        foreach (var def in defs)
        {
            File.WriteAllText(Path.Combine(dir, def.Name + ".kicad_mod"), def.RawNode.Format());
        }
        return FootprintLibrary.LoadDir(dir);
    }

    /// Legacy KiCad v5/v6 fp_arc form: `(start CENTER) (end ARC_START) (angle SWEEP)`.
    /// KiCad 10 requires `(start) (mid) (end)` with three points on the curve.
    /// Converter must recover the curve geometry from center + start + sweep.
    [Theory]
    // Center at (0,0), arc starts at (10,0), sweeps 180° CCW -> ends at (-10, 0),
    // mid at (0, 10).
    [InlineData("(fp_arc (start 0 0) (end 10 0) (angle 180) (layer F.SilkS) (width 0.25))",
                10.0,  0.0,        // start (= legacy end)
                 0.0, 10.0,        // mid
               -10.0,  0.0)]       // end (= rotate start by 180)
    // 90° CCW sweep: start (1,0) -> mid (cos 45°, sin 45°) -> end (0,1).
    [InlineData("(fp_arc (start 0 0) (end 1 0) (angle 90) (layer F.SilkS) (width 0.25))",
                1.0, 0.0,
                0.7071067811865476, 0.7071067811865475,
                0.0, 1.0)]
    // Near-full-circle (the WQFN-40 pin-1 marker case): center (-2.5, 3.2),
    // start at (-2.5, 3.05) (0.15 below center), sweep 359.03° -> mid at the
    // diametrically opposite point (approximately (-2.5, 3.35)).
    [InlineData("(fp_arc (start -2.5 3.2) (end -2.5 3.05) (angle 359.03) (layer F.SilkS) (width 0.30))",
                -2.5, 3.05,
                -2.4987302881325455, 3.349994626009647,
                -2.502538895690299, 3.050021495576351)]
    public void ConvertLegacyFpArc_translates_center_form_to_three_point_form(
        string legacyExpr,
        double expStartX, double expStartY,
        double expMidX,   double expMidY,
        double expEndX,   double expEndY)
    {
        var arc = (SList)SExpr.Parse(legacyExpr);
        var converted = (SList)PcbEmitter.ConvertLegacyFpArc(arc);

        converted.Head.Should().Be("fp_arc");
        var start = converted.First("start")!;
        var mid   = converted.First("mid")!;
        var end   = converted.First("end")!;
        ReadXY(start).x.Should().BeApproximately(expStartX, 1e-6);
        ReadXY(start).y.Should().BeApproximately(expStartY, 1e-6);
        ReadXY(mid).x.Should().BeApproximately(expMidX, 1e-6);
        ReadXY(mid).y.Should().BeApproximately(expMidY, 1e-6);
        ReadXY(end).x.Should().BeApproximately(expEndX, 1e-6);
        ReadXY(end).y.Should().BeApproximately(expEndY, 1e-6);

        // No (angle) child in the converted form.
        converted.First("angle").Should().BeNull(
            because: "the legacy angle field is consumed by the conversion");

        // Pass-through children (layer, width, stroke, ...) preserved.
        converted.First("layer").Should().NotBeNull();
        converted.First("width").Should().NotBeNull();
    }

    [Fact]
    public void ConvertLegacyFpArc_passes_through_new_form_unchanged()
    {
        var newForm = (SList)SExpr.Parse(
            "(fp_arc (start 0 0) (mid 1 1) (end 2 0) (layer F.SilkS) (width 0.25))");
        var converted = (SList)PcbEmitter.ConvertLegacyFpArc(newForm);

        converted.Head.Should().Be("fp_arc");
        converted.First("mid").Should().NotBeNull(because: "input had no (angle); pass through");
        converted.First("angle").Should().BeNull();
    }

    private static (double x, double y) ReadXY(SList node)
    {
        double x = double.Parse(((SAtom)node.Items[1]).Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        double y = double.Parse(((SAtom)node.Items[2]).Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        return (x, y);
    }
}
