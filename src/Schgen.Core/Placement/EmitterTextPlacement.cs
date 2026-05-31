using Schgen.Core.KiCad;
using Schgen.Core.Yaml;

namespace Schgen.Core.Placement;

/// Shared Reference / Value text placement logic. Called by:
///   - `SchematicEmitter` when emitting the Property nodes at write time.
///   - `FanoutGeometry` when measuring the rendered footprint.
///
/// Keeping these in one helper guarantees the placer's rect and the emitter's
/// rendered position can't disagree.
///
/// All rects are in PLACER frame (pre Y-flip). The emitter applies the page
/// offset and Y-flip itself.
public static class EmitterTextPlacement
{
    /// The gap KiCad's `SCH_SYMBOL::AutoplaceFields` leaves between a body
    /// edge and the auto-placed Reference / Value text. KiCad's default
    /// is `1 × font_size` (one text-cell of breathing room). Used by BOTH
    /// the placer (when computing the fanout rect that includes ref/value
    /// text) and the emitter (when writing the (at) coord of those Property
    /// nodes) - keep them in sync via this single constant.
    public const double TextGap = NewstrokeFont.FontSize;

    /// The body bbox the emitter uses for property auto-placement = the
    /// symbol's per-unit DRAWING bbox (rectangles + polylines + arcs,
    /// excluding pin endpoints), rotated and translated to the placement's
    /// world coords. Pin endpoints are intentionally excluded so the
    /// reference/value text sits adjacent to the body rather than at the
    /// far tip of the pin (where the net label connects).
    public static BBox BodyBoxInPlacerCoords(SymbolDef sym, int unit, double rotation, double worldX, double worldY)
    {
        var lib = sym.UnitDrawingBBox(unit);
        double rad = rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (lx, ly) in new[] { (lib.MinX, lib.MinY), (lib.MaxX, lib.MinY),
                                         (lib.MinX, lib.MaxY), (lib.MaxX, lib.MaxY) })
        {
            double wx = worldX + lx * cos - ly * sin;
            double wy = worldY + lx * sin + ly * cos;
            if (wx < minX) minX = wx;
            if (wx > maxX) maxX = wx;
            if (wy < minY) minY = wy;
            if (wy > maxY) maxY = wy;
        }
        return new BBox(minX, minY, maxX, maxY);
    }

    /// Reference text rect. Emitter places anchor at body-center, TextGap
    /// above body top, with a compensating property rotation so the rendered
    /// text glyph is always horizontal (parent_symbol_rotation +
    /// property_rotation = 0° net). The rendered text is centered on the
    /// anchor and extends horizontally.
    public static BBox ReferenceTextRect(ComponentDef comp, BBox body, double rotation)
    {
        double width = NewstrokeFont.TextWidth(comp.Ref ?? "");
        double halfH = NewstrokeFont.FontSize / 2;
        double anchorX = (body.MinX + body.MaxX) * 0.5;
        double anchorY = body.MaxY + TextGap;
        // Net text glyph rotation is 0° (horizontal), so the bbox is axis-
        // aligned regardless of parent symbol rotation.
        return CenteredRotatedRect(anchorX, anchorY, width, halfH, 0);
    }

    /// Value text rect. Anchor at body-center, TextGap below body bottom.
    /// Same horizontal-text semantics as ReferenceTextRect.
    public static BBox ValueTextRect(ComponentDef comp, SymbolDef sym, BBox body, double rotation)
    {
        double width = NewstrokeFont.TextWidth(comp.Value ?? "");
        double halfH = NewstrokeFont.FontSize / 2;
        double anchorX = (body.MinX + body.MaxX) * 0.5;
        double anchorY = body.MinY - TextGap;
        return CenteredRotatedRect(anchorX, anchorY, width, halfH, 0);
    }

    /// Axis-aligned bbox of a text rect of total length `width` and half-
    /// height `halfH`, centered on `(anchorX, anchorY)`, with its long axis
    /// rotated by `rotation` degrees from horizontal. Returns a bbox in the
    /// SAME frame as the inputs (placer Y-up).
    private static BBox CenteredRotatedRect(
        double anchorX, double anchorY, double width, double halfH, double rotation)
    {
        double rad = rotation * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        double halfW = width / 2;
        // Corners of the text rect in its own (long-axis × short-axis)
        // local frame, then rotated to placer frame.
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (lx, ly) in new[] { (-halfW, -halfH), (halfW, -halfH), (-halfW, halfH), (halfW, halfH) })
        {
            double rx = anchorX + lx * c - ly * s;
            double ry = anchorY + lx * s + ly * c;
            if (rx < minX) minX = rx;
            if (rx > maxX) maxX = rx;
            if (ry < minY) minY = ry;
            if (ry > maxY) maxY = ry;
        }
        return new BBox(minX, minY, maxX, maxY);
    }

    /// Does this unit have a pin whose endpoint sits at or below the unit's
    /// bbox bottom edge (within a small tolerance)? Mirrors the emitter's
    /// HasPinAtBottom logic for picking Value alignment.
    internal static bool HasPinAtBottom(SymbolDef sym, int unit)
    {
        var b = sym.UnitBBox(unit);
        const double tol = 0.1;
        foreach (var pin in sym.PinsOfUnit(unit))
            if (Math.Abs(pin.Y - b.MinY) < tol) return true;
        return false;
    }
}
