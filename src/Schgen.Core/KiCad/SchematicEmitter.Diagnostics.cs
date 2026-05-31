using Schgen.Core.Placement;
using Schgen.Core.Yaml;

namespace Schgen.Core.KiCad;

/// Diagnostic-rect emit helpers. Always compiled in (so the methods stay
/// fresh under refactors), but only called when SCHGEN_DEBUG_RECTS is
/// defined (see #if guards in the main partial). Splits out the ~200
/// lines of debug-only drawing code from the production emit path.
public sealed partial class SchematicEmitter
{
    /// Diagnostic: emit a thin polyline rectangle around every placement's
    /// fanout rect (placement.EffectiveBBox) so we can visually verify the
    /// rect the placer computed against what KiCad actually renders.
    ///   UCs (non-passive): blue (0, 0, 255).
    ///   Passives: green (0, 200, 0).
    private void EmitFanoutDiagnosticRects(List<SExpr> sheetItems, SheetLayout layout)
    {
        foreach (var p in layout.Components.Values)
        {
            var r = p.EffectiveBBox;
            double x1 = r.MinX + _pageCenterX;
            double x2 = r.MaxX + _pageCenterX;
            double y1 = -r.MaxY + _pageCenterY;
            double y2 = -r.MinY + _pageCenterY;
            (int red, int green, int blue) = p.Symbol.Is2PinPassive
                ? (0, 200, 0)
                : (0, 0, 255);
            sheetItems.Add(new SList(
                SAtom.Sym("polyline"),
                new SList(SAtom.Sym("pts"),
                    new SList(SAtom.Sym("xy"), SAtom.Num(x1), SAtom.Num(y1)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(x2), SAtom.Num(y1)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(x2), SAtom.Num(y2)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(x1), SAtom.Num(y2)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(x1), SAtom.Num(y1))),
                new SList(SAtom.Sym("stroke"),
                    new SList(SAtom.Sym("width"), SAtom.Num(0.1524)),
                    new SList(SAtom.Sym("type"), SAtom.Sym("solid")),
                    new SList(SAtom.Sym("color"), SAtom.Num(red), SAtom.Num(green), SAtom.Num(blue), SAtom.Num(1))),
                new SList(SAtom.Sym("fill"), new SList(SAtom.Sym("type"), SAtom.Sym("none"))),
                new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid($"diag:fanout:{p.Ref}#{p.Unit}")))));
        }
    }

    /// Diagnostic: emit two thin polyline rectangles per component showing
    /// EmitterTextPlacement's idea of where the Reference text and Value
    /// text rects are. Reference: teal (0,150,150). Value: brown (139,69,19).
    private void EmitRefValueDiagnosticRects(List<SExpr> sheetItems, SheetDef sheet, SheetLayout layout)
    {
        foreach (var comp in sheet.Components)
        {
            var placement = layout.FindByRef(comp.Ref, comp.Unit);
            if (placement is null) continue;
            var bodyBBox = Placement.EmitterTextPlacement.BodyBoxInPlacerCoords(
                placement.Symbol, comp.Unit, placement.Rotation, placement.X, placement.Y);
            var refRect = Placement.EmitterTextPlacement.ReferenceTextRect(comp, bodyBBox, placement.Rotation);
            var valRect = Placement.EmitterTextPlacement.ValueTextRect(comp, placement.Symbol, bodyBBox, placement.Rotation);
            EmitDiagRect(sheetItems, refRect, 0, 150, 150, $"diag:reftext:{comp.Ref}#{comp.Unit}");
            EmitDiagRect(sheetItems, valRect, 139, 69, 19, $"diag:valtext:{comp.Ref}#{comp.Unit}");
        }
    }

    /// Emit a single rect outline as a polyline. Placer-frame rect → emitted
    /// Y-down coords via the same +pageCenterX / -y+pageCenterY transform
    /// EmitFanoutDiagnosticRects uses.
    private void EmitDiagRect(List<SExpr> sheetItems, BBox r, int red, int green, int blue, string uuidSeed)
    {
        double x1 = r.MinX + _pageCenterX;
        double x2 = r.MaxX + _pageCenterX;
        double y1 = -r.MaxY + _pageCenterY;
        double y2 = -r.MinY + _pageCenterY;
        sheetItems.Add(new SList(
            SAtom.Sym("polyline"),
            new SList(SAtom.Sym("pts"),
                new SList(SAtom.Sym("xy"), SAtom.Num(x1), SAtom.Num(y1)),
                new SList(SAtom.Sym("xy"), SAtom.Num(x2), SAtom.Num(y1)),
                new SList(SAtom.Sym("xy"), SAtom.Num(x2), SAtom.Num(y2)),
                new SList(SAtom.Sym("xy"), SAtom.Num(x1), SAtom.Num(y2)),
                new SList(SAtom.Sym("xy"), SAtom.Num(x1), SAtom.Num(y1))),
            new SList(SAtom.Sym("stroke"),
                new SList(SAtom.Sym("width"), SAtom.Num(0.0762)),
                new SList(SAtom.Sym("type"), SAtom.Sym("solid")),
                new SList(SAtom.Sym("color"), SAtom.Num(red), SAtom.Num(green), SAtom.Num(blue), SAtom.Num(1))),
            new SList(SAtom.Sym("fill"), new SList(SAtom.Sym("type"), SAtom.Sym("none"))),
            new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid(uuidSeed)))));
    }

    /// Debug-only: emit four polylines per chevron-bearing label so we can
    /// see schgen's model of each component:
    ///   - text glyph rect              (RED)
    ///   - text + vertical padding rect (ORANGE)
    ///   - pin-side chevron rect        (CYAN)
    ///   - text-side chevron rect       (MAGENTA)
    /// All rects are oriented along the label's reading axis.
    private void EmitLabelDebugRects(List<SExpr> sheetItems, double px, double py,
        int labelRot, string net, Placement.LabelGeometry.Kind kind)
    {
        double textWidth = NewstrokeFont.TextWidth(net);
        double fontSize = NewstrokeFont.FontSize;
        bool hasChevron = kind != Placement.LabelGeometry.Kind.Regular;
        double chevronWidth = hasChevron ? Placement.LabelGeometry.ChevronHalfSize : 0;
        double halfThicknessOuter = fontSize / 2 + (hasChevron ? Placement.LabelGeometry.LabelMargin : 0);
        double halfThicknessText = fontSize / 2;

        // Axial direction in emit coords. KiCad emit Y points DOWN, so a
        // labelRot of 0 = text reads +X, 90 = text reads -Y (up on screen),
        // 180 = text reads -X, 270 = +Y (down).
        double rad = labelRot * Math.PI / 180.0;
        double axX = Math.Cos(rad);
        double axY = -Math.Sin(rad);
        double perpX = -axY;
        double perpY = axX;

        void Rect(double frontDist, double backDist, double halfPerp, int r, int g, int b, string tag)
        {
            (double, double) P(double ax, double ap)
                => (px + ax * axX + ap * perpX, py + ax * axY + ap * perpY);
            var c1 = P(frontDist, -halfPerp);
            var c2 = P(backDist,  -halfPerp);
            var c3 = P(backDist,  +halfPerp);
            var c4 = P(frontDist, +halfPerp);
            sheetItems.Add(new SList(
                SAtom.Sym("polyline"),
                new SList(SAtom.Sym("pts"),
                    new SList(SAtom.Sym("xy"), SAtom.Num(c1.Item1), SAtom.Num(c1.Item2)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(c2.Item1), SAtom.Num(c2.Item2)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(c3.Item1), SAtom.Num(c3.Item2)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(c4.Item1), SAtom.Num(c4.Item2)),
                    new SList(SAtom.Sym("xy"), SAtom.Num(c1.Item1), SAtom.Num(c1.Item2))),
                new SList(SAtom.Sym("stroke"),
                    new SList(SAtom.Sym("width"), SAtom.Num(0.0762)),
                    new SList(SAtom.Sym("type"), SAtom.Sym("solid")),
                    new SList(SAtom.Sym("color"), SAtom.Num(r), SAtom.Num(g), SAtom.Num(b), SAtom.Num(1))),
                new SList(SAtom.Sym("fill"), new SList(SAtom.Sym("type"), SAtom.Sym("none"))),
                new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid($"diag:lbl:{tag}:{net}:{px}:{py}")))));
        }

        // Asymmetric padding (pin-side < outside). Independent of chevron
        // presence - padding is a property of the text-in-container, not of
        // the polygon shape.
        double leftPad = Placement.LabelGeometry.LeftPad;
        double rightPad = Placement.LabelGeometry.RightPad;
        // Axial layout:
        //   [0 .. chevronWidth]                     pin-side chevron (CYAN)
        //   [chevronWidth .. polyEnd - chevronWidth] text+padding region (ORANGE)
        //   [polyEnd - chevronWidth .. polyEnd]     text-side chevron (MAGENTA)
        // The text glyph rect (RED) sits inside ORANGE: offset by leftPad
        // from ORANGE's left edge and ending rightPad before ORANGE's right.
        double padStart = chevronWidth;
        double padEnd   = padStart + leftPad + textWidth + rightPad;
        double polyEnd  = padEnd + chevronWidth;
        double textStart = padStart + leftPad;
        double textEnd   = textStart + textWidth;

        if (hasChevron) Rect(0,        chevronWidth, halfThicknessOuter, 0, 200, 200, "chevF");
        Rect(padStart,   padEnd,                     halfThicknessOuter, 255, 165, 0, "pad");
        if (hasChevron) Rect(padEnd,   polyEnd,      halfThicknessOuter, 200, 0, 200, "chevB");
        Rect(textStart,  textEnd,                    halfThicknessText,  255, 0, 0, "text");
    }
}
