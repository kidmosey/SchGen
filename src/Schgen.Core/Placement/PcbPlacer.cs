using System.Globalization;
using Schgen.Core.KiCad;
using Schgen.Core.Yaml;

namespace Schgen.Core.Placement;

public sealed class PcbPlacement
{
    public List<FootprintPlacement> Footprints { get; } = new();
}

public sealed class FootprintPlacement
{
    public string Ref { get; init; } = "";
    public string FootprintLibId { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Rotation { get; init; }
    public string? Value { get; init; }
    public string Net { get; init; } = "";
    public Dictionary<string, string> PadToNet { get; init; } = new();
    public FootprintDef? Def { get; init; }
}

/// PCB placer organized around the largest IC on the board.
///
/// The anchor (chip with the most pads) sits at the board centre. Every
/// other component is placed just outside the anchor's bbox, in the
/// direction of the centroid of the anchor pads it shares nets with.
/// Multi-unit chips emit exactly one footprint with merged pin->net data
/// from every unit declared on every sheet. Manual `pcb_at` overrides
/// always bypass the auto-placer.
public sealed class PcbPlacer
{
    private readonly CircuitDocument _doc;
    private readonly LibraryIndex _libs;
    private readonly SchPlacement _schPlacement;

    public PcbPlacer(CircuitDocument doc, LibraryIndex libs, SchPlacement sch)
    {
        _doc = doc;
        _libs = libs;
        _schPlacement = sch;
    }

    public PcbPlacement Run()
    {
        var result = new PcbPlacement();
        var ctx = BuildContext();
        if (ctx is null) return result;

        PlaceAnchor(result, ctx);

        foreach (var refDes in SortPeripheralsByConnectivity(ctx))
            PlacePeripheral(result, ctx, refDes);

        return result;
    }

    /// Aggregated state for one Run(): grouped components, pad-to-net maps
    /// per ref, resolved footprints, anchor selection, anchor coords +
    /// half-extents, anchor pad positions (lib-frame).
    private sealed class PcbContext
    {
        public required Dictionary<string, List<(string Sheet, ComponentDef Comp)>> ByRef { get; init; }
        public required Dictionary<string, Dictionary<string, string>> PadToNetByRef { get; init; }
        public required Dictionary<string, FootprintDef?> FpByRef { get; init; }
        public required string AnchorRef { get; init; }
        public required ComponentDef AnchorComp { get; init; }
        public required FootprintDef? AnchorFp { get; init; }
        public required double AnchorX { get; init; }
        public required double AnchorY { get; init; }
        public required double AnchorRot { get; init; }
        public required double AnchorHalfX { get; init; }
        public required double AnchorHalfY { get; init; }
        public required Dictionary<string, (double X, double Y)> AnchorPadPos { get; init; }
    }

    private PcbContext? BuildContext()
    {
        const double pcbCenterX = 150.0;
        const double pcbCenterY = 130.0;

        // Cross-sheet index: every Ref -> list of (sheet, ComponentDef).
        // Multi-unit chips collect all their units here.
        var byRef = _doc.Sheets
            .SelectMany(kv => kv.Value.Components.Select(c => (Sheet: kv.Key, Comp: c)))
            .GroupBy(t => t.Comp.Ref, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (byRef.Count == 0) return null;

        // Merged padToNet map per ref (across all units).
        var padToNetByRef = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (refDes, entries) in byRef)
            padToNetByRef[refDes] = BuildMergedPadMap(refDes, entries);

        // Resolve each ref's footprint once.
        var fpByRef = new Dictionary<string, FootprintDef?>(StringComparer.Ordinal);
        foreach (var (refDes, entries) in byRef)
            fpByRef[refDes] = _libs.ResolveFootprint(entries.First().Comp.Footprint);

        // Anchor selection: the chip with the most pads. Tie-break by name.
        // If no chip has a resolved footprint, fall back to padToNet count.
        string? anchorRef = byRef.Keys
            .OrderByDescending(r => fpByRef[r]?.PadsByName.Count ?? padToNetByRef[r].Count)
            .ThenBy(r => r, StringComparer.Ordinal)
            .FirstOrDefault();
        if (anchorRef is null) return null;

        var anchorComp = byRef[anchorRef].OrderBy(t => t.Comp.Unit).First().Comp;
        var anchorFp = fpByRef[anchorRef];
        double anchorX = anchorComp.PcbAt?.X ?? pcbCenterX;
        double anchorY = anchorComp.PcbAt?.Y ?? pcbCenterY;
        double anchorRot = anchorComp.PcbRotate ?? 0;
        double anchorHalfX = anchorFp is null ? 5.0 : anchorFp.BoundingBox.Width * 0.5;
        double anchorHalfY = anchorFp is null ? 5.0 : anchorFp.BoundingBox.Height * 0.5;

        // Pre-compute anchor pad positions (lib coords relative to anchor centre).
        var anchorPadPos = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        if (anchorFp is not null)
        {
            foreach (var (padName, padNode) in anchorFp.PadsByName)
            {
                var at = padNode.First("at");
                if (at is null || at.Items.Count < 3) continue;
                if (!TryD(at.Items[1], out var px)) continue;
                if (!TryD(at.Items[2], out var py)) continue;
                anchorPadPos[padName] = (px, py);
            }
        }

        return new PcbContext
        {
            ByRef = byRef,
            PadToNetByRef = padToNetByRef,
            FpByRef = fpByRef,
            AnchorRef = anchorRef,
            AnchorComp = anchorComp,
            AnchorFp = anchorFp,
            AnchorX = anchorX,
            AnchorY = anchorY,
            AnchorRot = anchorRot,
            AnchorHalfX = anchorHalfX,
            AnchorHalfY = anchorHalfY,
            AnchorPadPos = anchorPadPos,
        };
    }

    private static void PlaceAnchor(PcbPlacement result, PcbContext ctx)
    {
        result.Footprints.Add(new FootprintPlacement
        {
            Ref            = ctx.AnchorComp.Ref,
            FootprintLibId = ctx.AnchorComp.Footprint,
            X              = ctx.AnchorX,
            Y              = ctx.AnchorY,
            Rotation       = ctx.AnchorRot,
            Value          = ctx.AnchorComp.Value,
            PadToNet       = ctx.PadToNetByRef[ctx.AnchorRef],
            Def            = ctx.AnchorFp,
        });
    }

    /// Order peripherals by how strongly they connect to the anchor (most
    /// shared nets first). Strongly-connected parts are placed first, so they
    /// land closest to the anchor.
    private static IEnumerable<string> SortPeripheralsByConnectivity(PcbContext ctx)
    {
        var anchorPadToNet = ctx.PadToNetByRef[ctx.AnchorRef];
        return ctx.ByRef.Keys
            .Where(r => r != ctx.AnchorRef)
            .Select(r => (Ref: r, Shared: CountSharedNets(ctx.PadToNetByRef[r], anchorPadToNet)))
            .OrderByDescending(t => t.Shared)
            .ThenBy(t => t.Ref, StringComparer.Ordinal)
            .Select(t => t.Ref);
    }

    private void PlacePeripheral(PcbPlacement result, PcbContext ctx, string refDes)
    {
        var first = ctx.ByRef[refDes].OrderBy(t => t.Comp.Unit).First().Comp;
        var fp = ctx.FpByRef[refDes];

        double x, y, rot;
        if (first.PcbAt is { } at)
        {
            x = at.X;
            y = at.Y;
            rot = first.PcbRotate ?? 0;
        }
        else
        {
            var (dirX, dirY) = DirectionToAnchorCentroid(ctx, refDes);

            // Synthetic half-extent for unresolved footprints keeps the
            // collision check meaningful.
            double pHalfX = fp is null ? 2.5 : fp.BoundingBox.Width  * 0.5;
            double pHalfY = fp is null ? 2.5 : fp.BoundingBox.Height * 0.5;

            // Project both bbox extents in the placement direction so the
            // peripheral sits just outside the anchor's bbox along that ray.
            double anchorExtentInDir = Math.Abs(dirX) * ctx.AnchorHalfX + Math.Abs(dirY) * ctx.AnchorHalfY;
            double pExtentInDir      = Math.Abs(dirX) * pHalfX          + Math.Abs(dirY) * pHalfY;
            const double seedClearance = 1.5;
            double natX = ctx.AnchorX + dirX * (anchorExtentInDir + pExtentInDir + seedClearance);
            double natY = ctx.AnchorY + dirY * (anchorExtentInDir + pExtentInDir + seedClearance);

            // Route around already-placed footprints.
            BBox candidateBB = fp?.BoundingBox ?? SyntheticBBox(pHalfX, pHalfY);
            var bestPos = FindClosestFreeSlot(result, candidateBB, natX, natY, dirX, dirY);
            if (bestPos is null)
                throw new InvalidOperationException(
                    $"PcbPlacer: could not find a non-overlapping position for '{refDes}' " +
                    $"within {MaxSearchDepth} routing hops of anchor '{ctx.AnchorRef}'. " +
                    $"Add a 'pcb_at' override for this component or reduce board complexity.");
            x = bestPos.Value.X;
            y = bestPos.Value.Y;
            rot = first.PcbRotate ?? 0;
        }

        result.Footprints.Add(new FootprintPlacement
        {
            Ref            = first.Ref,
            FootprintLibId = first.Footprint,
            X              = x,
            Y              = y,
            Rotation       = rot,
            Value          = first.Value,
            PadToNet       = ctx.PadToNetByRef[refDes],
            Def            = fp,
        });
    }

    /// Unit-vector direction from the anchor toward the centroid of anchor
    /// pads that share a net with `refDes`. Iterates UNIQUE nets so a
    /// peripheral with many pads on the same net doesn't bias the centroid.
    /// Falls back to (+X) when no shared nets exist.
    private static (double X, double Y) DirectionToAnchorCentroid(PcbContext ctx, string refDes)
    {
        var anchorPadToNet = ctx.PadToNetByRef[ctx.AnchorRef];
        var uniqueNets = new HashSet<string>(ctx.PadToNetByRef[refDes].Values, StringComparer.Ordinal);
        double sumX = 0, sumY = 0;
        int count = 0;
        foreach (var net in uniqueNets)
            foreach (var (anchorPadName, anchorNet) in anchorPadToNet)
            {
                if (anchorNet != net) continue;
                if (!ctx.AnchorPadPos.TryGetValue(anchorPadName, out var p)) continue;
                sumX += p.X; sumY += p.Y; count++;
            }
        if (count == 0) return (1, 0);
        double cx = sumX / count, cy = sumY / count;
        double mag = Math.Sqrt(cx * cx + cy * cy);
        return mag < 0.01 ? (1, 0) : (cx / mag, cy / mag);
    }

    private Dictionary<string, string> BuildMergedPadMap(
        string refDes, List<(string Sheet, ComponentDef Comp)> entries)
    {
        var padToNet = new Dictionary<string, string>(StringComparer.Ordinal);
        var sym = _libs.ResolveSymbol(entries.First().Comp.Symbol);
        // For multi-net pins (e.g. "+3V3" + "U1_VCC_DECAP") the FIRST net
        // is the canonical electrical net used on the PCB. Tag nets are
        // schematic-only.
        foreach (var (_, comp) in entries)
        {
            if (sym is not null)
            {
                var unitPins = sym.PinsOfUnit(comp.Unit);
                foreach (var (pinId, netList) in comp.Pins)
                {
                    var canonical = netList.FirstOrDefault(n => n != "NC");
                    if (string.IsNullOrEmpty(canonical)) continue;
                    var pin = unitPins.FirstOrDefault(p => p.Number == pinId)
                              ?? unitPins.FirstOrDefault(p => p.Name == pinId);
                    if (pin is null) continue;
                    padToNet[pin.Number] = canonical;
                }
            }
            else
            {
                foreach (var (k, netList) in comp.Pins)
                {
                    var canonical = netList.FirstOrDefault(n => n != "NC");
                    if (!string.IsNullOrEmpty(canonical)) padToNet[k] = canonical;
                }
            }
        }
        return padToNet;
    }

    private static int CountSharedNets(
        Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var aNets = new HashSet<string>(a.Values, StringComparer.Ordinal);
        int c = 0;
        foreach (var n in b.Values)
            if (aNets.Contains(n)) c++;
        return c;
    }

    /// Recursion depth cap for the collision-routing search. 12 hops is
    /// enough to dodge around any reasonable cluster of placed footprints;
    /// dense boards (e.g. RK3566 with 20+ proximity-attached decoupling
    /// caps) need a few hops but not many.
    private const int MaxSearchDepth = 12;

    /// Minimum mm of empty space between a candidate footprint and any
    /// previously-placed footprint after the routing search resolves a
    /// collision. Slightly larger than the schematic's slideMargin since
    /// PCB pads need pad-to-pad copper spacing.
    private const double PcbClearance = 1.5;

    /// Find the free slot closest to (natX, natY) by walking past any
    /// colliding footprint's edge in three directions (forward / perp+ /
    /// perp-) and recursing from each. Free slots are leaves: when a branch
    /// finds a non-colliding position it's added to the list and the branch
    /// stops; only colliding branches keep expanding. Bounded depth keeps
    /// the search finite; dedup'd by quantized coord. Returns null when no
    /// free slot exists within the depth limit (real boards shouldn't hit
    /// this without a manual pcb_at override).
    private static (double X, double Y)? FindClosestFreeSlot(
        PcbPlacement placed, BBox candidateBB,
        double natX, double natY,
        double dirX, double dirY)
    {
        double perpX = -dirY, perpY = dirX;
        var freeSlots = new List<(double X, double Y)>();
        var visited = new HashSet<(long, long)>();

        void Search(double posX, double posY, int depth)
        {
            if (depth > MaxSearchDepth) return;
            long kx = (long)Math.Round(posX * 100);
            long ky = (long)Math.Round(posY * 100);
            if (!visited.Add((kx, ky))) return;
            var collider = FindCollider(placed, candidateBB, posX, posY);
            if (collider is null)
            {
                freeSlots.Add((posX, posY));
                return;
            }
            var c = collider.Value;
            var capRect = candidateBB.Offset(posX, posY);
            double fwdStep = ProjMax(c, dirX, dirY)   - ProjMin(capRect, dirX, dirY)   + PcbClearance;
            double upStep  = ProjMax(c, perpX, perpY) - ProjMin(capRect, perpX, perpY) + PcbClearance;
            double dnStep  = ProjMax(c, -perpX, -perpY) - ProjMin(capRect, -perpX, -perpY) + PcbClearance;
            Search(posX + dirX * fwdStep,    posY + dirY * fwdStep,    depth + 1);
            Search(posX + perpX * upStep,    posY + perpY * upStep,    depth + 1);
            Search(posX - perpX * dnStep,    posY - perpY * dnStep,    depth + 1);
        }
        Search(natX, natY, 0);

        if (freeSlots.Count == 0) return null;

        // Pick the free slot closest (Euclidean) to the natural position.
        var best = freeSlots[0];
        double bestDist = double.PositiveInfinity;
        foreach (var (sx, sy) in freeSlots)
        {
            double ddx = sx - natX, ddy = sy - natY;
            double d = ddx * ddx + ddy * ddy;
            if (d < bestDist) { bestDist = d; best = (sx, sy); }
        }
        return best;
    }

    private static bool OverlapsExisting(PcbPlacement result, BBox candidateBB, double x, double y)
        => FindCollider(result, candidateBB, x, y) is not null;

    /// First placed footprint whose world-frame bbox overlaps the candidate.
    /// Returns the placed footprint's world-frame bbox (translated by its
    /// X/Y) so callers can compute slide-past distances against it.
    private static BBox? FindCollider(PcbPlacement result, BBox candidateBB, double x, double y)
    {
        double minX = x + candidateBB.MinX, maxX = x + candidateBB.MaxX;
        double minY = y + candidateBB.MinY, maxY = y + candidateBB.MaxY;
        const double pad = 0.5;
        foreach (var p in result.Footprints)
        {
            var peerBB = p.Def?.BoundingBox ?? SyntheticBBox(2.5, 2.5);
            double oMinX = p.X + peerBB.MinX, oMaxX = p.X + peerBB.MaxX;
            double oMinY = p.Y + peerBB.MinY, oMaxY = p.Y + peerBB.MaxY;
            if (minX - pad < oMaxX && maxX + pad > oMinX
                && minY - pad < oMaxY && maxY + pad > oMinY)
                return new BBox(oMinX, oMinY, oMaxX, oMaxY);
        }
        return null;
    }

    private static double ProjMin(BBox r, double ox, double oy) =>
        Math.Min(Math.Min(r.MinX * ox + r.MinY * oy, r.MaxX * ox + r.MinY * oy),
                 Math.Min(r.MinX * ox + r.MaxY * oy, r.MaxX * ox + r.MaxY * oy));
    private static double ProjMax(BBox r, double ox, double oy) =>
        Math.Max(Math.Max(r.MinX * ox + r.MinY * oy, r.MaxX * ox + r.MinY * oy),
                 Math.Max(r.MinX * ox + r.MaxY * oy, r.MaxX * ox + r.MaxY * oy));

    private static BBox SyntheticBBox(double halfX, double halfY) =>
        new(-halfX, -halfY, halfX, halfY);

    private static bool TryD(SExpr e, out double v)
    {
        v = 0;
        return e is SAtom a
            && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
