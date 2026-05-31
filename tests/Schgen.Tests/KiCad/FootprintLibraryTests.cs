using FluentAssertions;
using Schgen.Core.KiCad;
using Xunit;

namespace Schgen.Tests.KiCad;

public class FootprintLibraryTests
{
    private const string Cap0402 = """
        (footprint "C_0402_1005Metric"
          (version 20221018)
          (generator pcbnew)
          (layer "F.Cu")
          (descr "Capacitor SMD 0402")
          (attr smd)
          (fp_line (start -0.5 -0.25) (end 0.5 -0.25) (stroke (width 0.05) (type solid)) (layer "F.SilkS"))
          (fp_line (start -0.5  0.25) (end 0.5  0.25) (stroke (width 0.05) (type solid)) (layer "F.SilkS"))
          (pad "1" smd roundrect (at -0.485 0) (size 0.6 0.55) (layers "F.Cu" "F.Paste" "F.Mask"))
          (pad "2" smd roundrect (at  0.485 0) (size 0.6 0.55) (layers "F.Cu" "F.Paste" "F.Mask")))
        """;

    [Fact]
    public void Parses_footprint_bbox_from_pads_and_silk()
    {
        var fp = FootprintDef.FromText(Cap0402, "C_0402_1005Metric")!;
        fp.Name.Should().Be("C_0402_1005Metric");
        // pads at +/-0.485 with size 0.6 -> extents [-0.785, 0.785], silk lines +/-0.5
        // Width = 1.57, Height = 0.55
        fp.BoundingBox.Width.Should().BeApproximately(1.57, 0.05);
        fp.BoundingBox.Height.Should().BeApproximately(0.55, 0.05);
    }

    [Fact]
    public void Returns_null_for_non_footprint_root()
    {
        var fp = FootprintDef.FromText("(not_a_footprint)", "x");
        fp.Should().BeNull();
    }
}
