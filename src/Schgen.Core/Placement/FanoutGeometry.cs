using System.Globalization;
using Schgen.Core.KiCad;
using Schgen.Core.Yaml;

namespace Schgen.Core.Placement;

/// Fanout rect computation: the bbox of everything PHYSICALLY drawn for a
/// component's placement. Walks the same primitives the emitter renders, so
/// the rect is the rendered footprint by construction.
///
/// Includes: lib body shapes (rectangle/polyline/arc/circle), pin stubs, pin
/// endpoints, inward pin-name text, outward net labels per (pin, net),
/// Reference and Value text rects (auto-placed by the emitter).
///
/// All coords are in PLACER frame (pre Y-flip). The emitter applies
/// `symY = -y + pageCenter` when writing the schematic.
public static class FanoutGeometry
{
    // FontHeight (text vertical extent) shares NewstrokeFont's FontSize.
    // Per-character widths come from NewstrokeFont.TextWidth - no per-char
    // constant in this file.
    private const double FontHeight = NewstrokeFont.FontSize;

    public static BBox Compute(SymbolDef sym, ComponentDef comp, double rotation, double worldX, double worldY,
        IReadOnlySet<string>? hiddenStubNets = null,
        IReadOnlyDictionary<string, LabelGeometry.Kind>? labelKindByNet = null,
        bool includeOutwardLabels = true,
        IReadOnlyDictionary<(long, long, string), int>? coincidentPinsAtNet = null)
    {
        var acc = new Acc();

        // 1. Body shapes (rectangle/polyline/arc/circle) - lib coords -> world.
        foreach (var (lx, ly) in EnumerateBodyPoints(sym.RawNode, comp.Unit))
        {
            var (wx, wy) = TransformPoint(lx, ly, rotation, worldX, worldY);
            acc.Include(wx, wy);
        }

        // Pre-pass: count THIS component's own pin endpoints at each (coord,
        // net). KiCad's standard "hide power pins" convention stacks many
        // logical balls (e.g., 17 VDD balls) at one screen coord; the emitter
        // suppresses labels there to avoid overlapping renders. The bbox must
        // do the same, regardless of whether the caller passed a cross-
        // component dedup index (which only exists at the final
        // RecomputeEffectiveBBoxesAfterDedup pass). External
        // `coincidentPinsAtNet`, when supplied, is merged with this self-count
        // so cross-component coincidence still gets caught.
        var selfPinsAtNet = new Dictionary<(long, long, string), int>();
        foreach (var pin in sym.PinsOfUnit(comp.Unit))
        {
            var nets = ListNetsForPin(comp, pin);
            if (nets is null) continue;
            var (px, py) = TransformPoint(pin.X, pin.Y, rotation, worldX, worldY);
            long kx = (long)Math.Round(px * 100);
            long ky = (long)Math.Round(py * 100);
            foreach (var net in nets)
            {
                if (string.IsNullOrEmpty(net) || net == "NC") continue;
                selfPinsAtNet[(kx, ky, net)] = selfPinsAtNet.GetValueOrDefault((kx, ky, net)) + 1;
            }
        }

        // 2. Pin-related geometry.
        foreach (var pin in sym.PinsOfUnit(comp.Unit))
        {
            // Pin endpoint.
            var (px, py) = TransformPoint(pin.X, pin.Y, rotation, worldX, worldY);
            acc.Include(px, py);

            // Pin stub: extends from endpoint INWARD (toward body) by pin.Length.
            // Per placer convention: outward = pin.Rotation + rotation + 180,
            // so inward = pin.Rotation + rotation.
            double inDeg = ((pin.Rotation + rotation) % 360.0 + 360.0) % 360.0;
            double inRad = inDeg * Math.PI / 180.0;
            double inCos = Math.Cos(inRad), inSin = Math.Sin(inRad);
            double stubEndX = px + inCos * pin.Length;
            double stubEndY = py + inSin * pin.Length;
            acc.Include(stubEndX, stubEndY);

            // Inward pin-name text: rendered from the stub-end position
            // continuing in the inward direction. Width = NewstrokeFont
            // per-glyph table; perpendicular half-extent = FontHeight/2.
            if (!string.IsNullOrEmpty(pin.Name) && pin.Name != "~")
            {
                double nameLen = NewstrokeFont.TextWidth(pin.Name);
                double nx2 = stubEndX + inCos * nameLen;
                double ny2 = stubEndY + inSin * nameLen;
                IncludeTextRect(acc, stubEndX, stubEndY, nx2, ny2, FontHeight / 2);
            }

            if (!includeOutwardLabels) continue;

            // Outward labels: one rect per (pin, net) emitted at the
            // pin endpoint, extending outward by LabelGeometry.Extent.
            // Hidden labels (multi-net pin + stub) render at 0.01 mm and
            // contribute zero. Label kind (Regular / Global / Hierarchical)
            // is supplied by `labelKindByNet`; absent entries default to
            // Regular so single-sheet stubs aren't over-counted.
            var nets = ListNetsForPin(comp, pin);
            if (nets is null) continue;
            double outDeg = (inDeg + 180.0) % 360.0;
            double outRad = outDeg * Math.PI / 180.0;
            double outCos = Math.Cos(outRad), outSin = Math.Sin(outRad);
            // Quantized pin coords for coincident-label dedup lookup.
            long kx = (long)Math.Round(px * 100);
            long ky = (long)Math.Round(py * 100);
            foreach (var net in nets)
            {
                if (string.IsNullOrEmpty(net) || net == "NC") continue;
                if (hiddenStubNets is not null && nets.Count >= 2 && hiddenStubNets.Contains(net)) continue;
                // Coincident-label skip: if more than one pin endpoint sits
                // at the same world coord on the same net (same component or
                // not), the emitter suppresses the label there to avoid
                // overlapping renders. Matches SchematicEmitter's coincident-
                // pin dedup: count by pin endpoint, suppress when count > 1.
                int selfCount = selfPinsAtNet.GetValueOrDefault((kx, ky, net));
                int externCount = coincidentPinsAtNet?.GetValueOrDefault((kx, ky, net)) ?? 0;
                if (Math.Max(selfCount, externCount) > 1) continue;
                var kind = labelKindByNet is not null && labelKindByNet.TryGetValue(net, out var k)
                    ? k : LabelGeometry.Kind.Regular;
                double labelLen = LabelGeometry.Extent(net, kind);
                double labEndX = px + outCos * labelLen;
                double labEndY = py + outSin * labelLen;
                // Polygon perpendicular half-extent depends on label kind.
                // Regular = just the text glyph half-height. Global / Hierarchical
                // have the chevron polygon which extends LabelMargin beyond the
                // text on the perpendicular axis.
                double halfPerp = kind == LabelGeometry.Kind.Regular
                    ? FontHeight / 2
                    : LabelGeometry.ChevronHalfSize;
                IncludeTextRect(acc, px, py, labEndX, labEndY, halfPerp);
            }
        }

        // 3. Reference and Value text - emitter auto-places them relative to
        // the body bbox (UnitBBox rotated to world). Share the math via
        // EmitterTextPlacement so the rect = the rendered rect by construction.
        var bodyBBox = EmitterTextPlacement.BodyBoxInPlacerCoords(sym, comp.Unit, rotation, worldX, worldY);
        var refRect = EmitterTextPlacement.ReferenceTextRect(comp, bodyBBox, rotation);
        acc.Include(refRect.MinX, refRect.MinY); acc.Include(refRect.MaxX, refRect.MaxY);
        var valRect = EmitterTextPlacement.ValueTextRect(comp, sym, bodyBBox, rotation);
        acc.Include(valRect.MinX, valRect.MinY); acc.Include(valRect.MaxX, valRect.MaxY);

        return acc.ToBBox(fallback: sym.UnitBBox(comp.Unit).Offset(worldX, worldY));
    }

    // -------------------- body-shape enumeration --------------------

    /// Yield every point that bounds a drawn shape on the given unit.
    /// Recurses into sub-symbols. For arcs, includes start/mid/end (the
    /// true bbox of an arc is curve-aware but those three points are a
    /// conservative bound for typical schematic arcs). For circles, includes
    /// the bounding square (center ± radius).
    private static IEnumerable<(double X, double Y)> EnumerateBodyPoints(SList symNode, int unit)
    {
        foreach (var sub in symNode.All("symbol"))
        {
            if (sub.Items.Count < 2 || sub.Items[1] is not SAtom n) continue;
            int subUnit = SubSymbolName.TryParseUnit(n.Value) ?? 1;
            if (subUnit != unit && subUnit != 0) continue;
            foreach (var pt in EnumerateShapesIn(sub)) yield return pt;
        }
    }

    private static IEnumerable<(double, double)> EnumerateShapesIn(SList n)
    {
        foreach (var rect in n.All("rectangle"))
        {
            var s = rect.First("start");
            var e = rect.First("end");
            if (s is not null && TryXY(s, out var sx, out var sy)) yield return (sx, sy);
            if (e is not null && TryXY(e, out var ex, out var ey)) yield return (ex, ey);
        }
        foreach (var poly in n.All("polyline"))
        {
            var pts = poly.First("pts");
            if (pts is null) continue;
            foreach (var xy in pts.All("xy"))
                if (TryXY(xy, out var x, out var y)) yield return (x, y);
        }
        foreach (var arc in n.All("arc"))
        {
            foreach (var key in new[] { "start", "mid", "end" })
            {
                var p = arc.First(key);
                if (p is not null && TryXY(p, out var x, out var y)) yield return (x, y);
            }
        }
        foreach (var circle in n.All("circle"))
        {
            var ctr = circle.First("center");
            var rad = circle.First("radius");
            if (ctr is null || rad is null) continue;
            if (!TryXY(ctr, out var cx, out var cy)) continue;
            if (rad.Items.Count < 2 || rad.Items[1] is not SAtom ra) continue;
            if (!double.TryParse(ra.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) continue;
            yield return (cx - r, cy - r);
            yield return (cx + r, cy + r);
        }
    }

    private static bool TryXY(SList at, out double x, out double y)
    {
        x = y = 0;
        if (at.Items.Count < 3) return false;
        if (at.Items[1] is not SAtom xa || at.Items[2] is not SAtom ya) return false;
        return double.TryParse(xa.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(ya.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    // -------------------- text-rect helper --------------------

    /// Include a text rect that runs from (x1, y1) to (x2, y2) along its
    /// reading axis, with a perpendicular half-extent of `halfPerp`.
    private static void IncludeTextRect(Acc acc, double x1, double y1, double x2, double y2, double halfPerp)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            acc.Include(x1 - halfPerp, y1 - halfPerp);
            acc.Include(x1 + halfPerp, y1 + halfPerp);
            return;
        }
        // Perpendicular unit vector (rotate (dx,dy) by 90 deg).
        double px = -dy / len, py = dx / len;
        acc.Include(x1 + px * halfPerp, y1 + py * halfPerp);
        acc.Include(x1 - px * halfPerp, y1 - py * halfPerp);
        acc.Include(x2 + px * halfPerp, y2 + py * halfPerp);
        acc.Include(x2 - px * halfPerp, y2 - py * halfPerp);
    }

    // -------------------- nets helper --------------------

    private static IReadOnlyList<string>? ListNetsForPin(ComponentDef comp, PinDef pin)
    {
        if (comp.Pins.TryGetValue(pin.Number, out var byNum)) return byNum;
        if (!string.IsNullOrEmpty(pin.Name) && comp.Pins.TryGetValue(pin.Name, out var byName)) return byName;
        return null;
    }

    // -------------------- coord transform --------------------

    private static (double, double) TransformPoint(double lx, double ly, double rotDeg, double wx, double wy)
    {
        double rad = rotDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        return (wx + lx * cos - ly * sin, wy + lx * sin + ly * cos);
    }

    // -------------------- accumulator --------------------

    private sealed class Acc
    {
        public double MinX = double.PositiveInfinity;
        public double MinY = double.PositiveInfinity;
        public double MaxX = double.NegativeInfinity;
        public double MaxY = double.NegativeInfinity;

        public void Include(double x, double y)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }

        public BBox ToBBox(BBox fallback) =>
            double.IsPositiveInfinity(MinX) ? fallback : new BBox(MinX, MinY, MaxX, MaxY);
    }
}
