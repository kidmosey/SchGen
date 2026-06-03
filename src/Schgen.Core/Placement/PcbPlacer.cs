using System.Globalization;
using System.Text.RegularExpressions;
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

/// PCB placer organized around per-sheet clusters.
///
/// Each YAML sheet becomes one placement cluster: a local anchor (chip with
/// the most pads scoped to the sheet) sits at the cluster origin (0, 0) and
/// every other component in the sheet is placed just outside the anchor's
/// bbox, in the direction of the centroid of the anchor pads it shares nets
/// with. Multi-unit chips that span sheets are assigned to one "owner" sheet
/// (the one where they have the most pad connections). Manual `pcb_at`
/// overrides always bypass the auto-placer.
///
/// Clusters are then arranged on the board: the largest-area cluster anchors
/// at the board centre and the rest are placed around it using the same
/// collision-avoiding hop walk that handles per-component placement, but at
/// cluster granularity. Edge-class clusters (those dominated by board-edge
/// connectors — USB, microSD, HDMI, barrel jack, RJ45) are placed against
/// the nearest free board edge instead of radially.
public sealed class PcbPlacer
{
    private const double PcbCenterX = 150.0;
    private const double PcbCenterY = 130.0;
    private const double BoardHalfWidth  = 125.0; // ±125 mm from centre → 250 mm wide
    private const double BoardHalfHeight = 100.0; // ±100 mm from centre → 200 mm tall

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

        var clusters = BuildClusters();
        if (clusters.Count == 0) return result;

        ArrangeClusters(clusters);

        // Translate each cluster's local-frame footprint coords to board
        // frame. Refs in `AbsoluteRefs` keep their stored coords verbatim —
        // those are user-supplied `pcb_at` values, which the contract guarantees
        // are absolute board-frame.
        foreach (var cluster in clusters)
            foreach (var fp in cluster.Local.Footprints)
            {
                bool isAbsolute = cluster.AbsoluteRefs.Contains(fp.Ref);
                result.Footprints.Add(new FootprintPlacement
                {
                    Ref            = fp.Ref,
                    FootprintLibId = fp.FootprintLibId,
                    X              = isAbsolute ? fp.X : cluster.GlobalX + fp.X,
                    Y              = isAbsolute ? fp.Y : cluster.GlobalY + fp.Y,
                    Rotation       = fp.Rotation,
                    Value          = fp.Value,
                    PadToNet       = fp.PadToNet,
                    Def            = fp.Def,
                });
            }

        return result;
    }

    /// One sheet's placement state. Footprints in `Local.Footprints` carry
    /// CLUSTER-LOCAL coordinates (origin at the local anchor's intended
    /// centre). `GlobalX/Y` is the cluster origin's position in the board
    /// frame, assigned by `ArrangeClusters`.
    private sealed class PcbCluster
    {
        public required string SheetName { get; init; }
        public PcbPlacement Local { get; } = new();
        public BBox LocalBBox { get; set; }
        public double GlobalX { get; set; }
        public double GlobalY { get; set; }
        public bool IsEdgeCluster { get; init; }
        /// Set of all nets present in this cluster (canonical electrical nets
        /// only — excludes "NC"). Used for cluster-cluster connectivity scoring.
        public required HashSet<string> Nets { get; init; }
        /// Owned refs (assigned to this sheet as their home). Used during
        /// cluster-cluster scoring without having to walk Local.Footprints.
        public required HashSet<string> Refs { get; init; }
        /// Refs whose pcb_at override pins them to absolute board-frame
        /// coordinates. They appear in Local.Footprints at the absolute coord
        /// directly; emission skips the GlobalX/Y translation for these. This
        /// keeps the user-facing `pcb_at` semantics unchanged from the
        /// pre-cluster design.
        public HashSet<string> AbsoluteRefs { get; } = new(StringComparer.Ordinal);
        /// When the cluster's anchor itself has a pcb_at, the entire cluster
        /// gets pinned: ArrangeClusters sets GlobalX/Y so the anchor (which
        /// lives at local origin (0, 0)) lands at this absolute coordinate.
        public (double X, double Y)? PinTarget { get; init; }
    }

    /// Aggregated state for one sheet's placement pass. Same shape as the
    /// single-anchor PcbContext that older revisions used board-wide, but
    /// scoped to one sheet's owned components.
    private sealed class PcbContext
    {
        public required string SheetName { get; init; }
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

    // ── Cluster build ──────────────────────────────────────────────────

    private List<PcbCluster> BuildClusters()
    {
        // Cross-sheet index: every Ref -> list of (sheet, ComponentDef).
        // Multi-unit chips collect all their units here.
        var byRefAll = _doc.Sheets
            .SelectMany(kv => kv.Value.Components.Select(c => (Sheet: kv.Key, Comp: c)))
            .GroupBy(t => t.Comp.Ref, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (byRefAll.Count == 0) return new List<PcbCluster>();

        // Merged padToNet map per ref (across all units & sheets). The
        // electrical net topology is the same across sheets; merging gives the
        // full connectivity picture for both anchor selection and direction
        // computation.
        var padToNetByRef = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (refDes, entries) in byRefAll)
            padToNetByRef[refDes] = BuildMergedPadMap(refDes, entries);

        // Resolve each ref's footprint once.
        var fpByRef = new Dictionary<string, FootprintDef?>(StringComparer.Ordinal);
        foreach (var (refDes, entries) in byRefAll)
            fpByRef[refDes] = _libs.ResolveFootprint(entries.First().Comp.Footprint);

        // Assign each ref to its OWNER SHEET (the sheet where it has the most
        // pad assignments). For single-sheet refs this is trivial; for
        // multi-unit chips spanning sheets the rule picks whichever sheet
        // contributes the most pad-net wiring. Tiebreak by document order
        // (the YAML's sheets are enumerated in insertion order).
        var sheetOrder = _doc.Sheets.Select((kv, idx) => (kv.Key, idx))
            .ToDictionary(t => t.Key, t => t.idx, StringComparer.Ordinal);
        var ownerByRef = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (refDes, entries) in byRefAll)
        {
            ownerByRef[refDes] = entries
                .GroupBy(t => t.Sheet, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(t => t.Comp.Pins.Count))
                .ThenBy(g => sheetOrder[g.Key])
                .First().Key;
        }

        // Build one cluster per sheet that owns ≥1 ref. Sheets that don't own
        // anything (every ref appearing on them is owned elsewhere) get
        // skipped — their components are placed inside their owner's cluster.
        var clusters = new List<PcbCluster>();
        foreach (var (sheetName, _) in _doc.Sheets)
        {
            var ownedRefs = ownerByRef
                .Where(kv => kv.Value == sheetName)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (ownedRefs.Count == 0) continue;

            var cluster = BuildOneCluster(sheetName, ownedRefs, byRefAll, padToNetByRef, fpByRef);
            if (cluster is not null) clusters.Add(cluster);
        }

        return clusters;
    }

    private PcbCluster? BuildOneCluster(
        string sheetName,
        HashSet<string> ownedRefs,
        Dictionary<string, List<(string Sheet, ComponentDef Comp)>> byRefAll,
        Dictionary<string, Dictionary<string, string>> padToNetByRef,
        Dictionary<string, FootprintDef?> fpByRef)
    {
        // Scope byRef and the resolved maps to just the refs this sheet owns.
        var byRef = ownedRefs.ToDictionary(r => r, r => byRefAll[r], StringComparer.Ordinal);
        var padToNetByRefLocal = ownedRefs.ToDictionary(r => r, r => padToNetByRef[r], StringComparer.Ordinal);
        var fpByRefLocal = ownedRefs.ToDictionary(r => r, r => fpByRef[r], StringComparer.Ordinal);

        // Anchor selection inside this sheet: largest resolved-footprint
        // pad count; fall back to declared pad-net count if footprints don't
        // resolve. Tiebreak by ref name (ordinal) for deterministic output.
        string? anchorRef = ownedRefs
            .OrderByDescending(r => fpByRefLocal[r]?.PadsByName.Count ?? padToNetByRefLocal[r].Count)
            .ThenBy(r => r, StringComparer.Ordinal)
            .FirstOrDefault();
        if (anchorRef is null) return null;

        var anchorComp = byRef[anchorRef].OrderBy(t => t.Comp.Unit).First().Comp;
        var anchorFp = fpByRefLocal[anchorRef];
        // Anchor always sits at (0, 0) cluster-local. A pcb_at on the anchor
        // PINS the cluster's global origin to that absolute board coordinate
        // (handled in ArrangeClusters); a pcb_at on a peripheral marks that
        // peripheral as absolute-positioned (handled in PlacePeripheral). In
        // both cases the local layout math stays unchanged.
        double anchorRot = anchorComp.PcbRotate ?? 0;
        double anchorHalfX = anchorFp is null ? 5.0 : anchorFp.BoundingBox.Width * 0.5;
        double anchorHalfY = anchorFp is null ? 5.0 : anchorFp.BoundingBox.Height * 0.5;

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

        var ctx = new PcbContext
        {
            SheetName = sheetName,
            ByRef = byRef,
            PadToNetByRef = padToNetByRefLocal,
            FpByRef = fpByRefLocal,
            AnchorRef = anchorRef,
            AnchorComp = anchorComp,
            AnchorFp = anchorFp,
            AnchorX = 0.0,
            AnchorY = 0.0,
            AnchorRot = anchorRot,
            AnchorHalfX = anchorHalfX,
            AnchorHalfY = anchorHalfY,
            AnchorPadPos = anchorPadPos,
        };

        var local = new PcbPlacement();
        var absoluteRefs = new HashSet<string>(StringComparer.Ordinal);
        PlaceAnchor(local, ctx);
        // Anchor with pcb_at PINS the cluster (handled by ArrangeClusters via
        // PinTarget); the anchor itself stays at local (0, 0).
        foreach (var refDes in SortPeripheralsByConnectivity(ctx))
            PlacePeripheral(local, ctx, refDes, absoluteRefs);

        var bbox = ComputeBBox(local, absoluteRefs);

        var nets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in ownedRefs)
            foreach (var net in padToNetByRef[r].Values)
                if (net != "NC") nets.Add(net);

        (double X, double Y)? pinTarget = anchorComp.PcbAt is { } pa ? (pa.X, pa.Y) : null;
        var cluster = new PcbCluster
        {
            SheetName = sheetName,
            LocalBBox = bbox,
            IsEdgeCluster = DetectEdgeCluster(ownedRefs, byRefAll),
            Nets = nets,
            Refs = ownedRefs,
            PinTarget = pinTarget,
        };
        cluster.Local.Footprints.AddRange(local.Footprints);
        foreach (var r in absoluteRefs) cluster.AbsoluteRefs.Add(r);
        return cluster;
    }

    // ── Cluster arrangement ────────────────────────────────────────────

    /// Position every cluster's `GlobalX/Y` so its origin lands at the
    /// cluster's chosen board-frame coordinate. Algorithm:
    ///   1. Pick the largest-area cluster as the main anchor cluster.
    ///   2. Place it at the board centre.
    ///   3. Sort the remaining clusters: edge clusters last (they prefer
    ///      board edges), non-edge by descending shared-net count with the
    ///      main cluster.
    ///   4. For each, walk the existing collision-avoiding hop search at
    ///      cluster granularity. Edge clusters get a board-edge bias.
    private void ArrangeClusters(List<PcbCluster> clusters)
    {
        if (clusters.Count == 0) return;

        // Pinned clusters (anchor has pcb_at) land at their absolute target
        // first — they're the user's manual placement override at cluster
        // granularity. Everything else routes around them.
        var pinned = clusters.Where(c => c.PinTarget is not null).ToList();
        var unpinned = clusters.Where(c => c.PinTarget is null).ToList();

        foreach (var p in pinned)
        {
            var t = p.PinTarget!.Value;
            p.GlobalX = t.X;
            p.GlobalY = t.Y;
        }

        if (unpinned.Count == 0) return;

        // 1. Main anchor cluster among unpinned: largest local bbox area,
        //    tiebreak by ref count then sheet name. Edge-class clusters never
        //    win this even if their area is largest.
        var nonEdge = unpinned.Where(c => !c.IsEdgeCluster).ToList();
        var pool = nonEdge.Count > 0 ? nonEdge : unpinned;
        var main = pool
            .OrderByDescending(c => c.LocalBBox.Width * c.LocalBBox.Height)
            .ThenByDescending(c => c.Refs.Count)
            .ThenBy(c => c.SheetName, StringComparer.Ordinal)
            .First();

        main.GlobalX = PcbCenterX - main.LocalBBox.CenterX;
        main.GlobalY = PcbCenterY - main.LocalBBox.CenterY;

        // 2. Place the rest. Non-edge clusters by shared-net count (most
        //    coupled first), then edge clusters last. Pinned clusters
        //    participate in collision tracking but aren't re-placed.
        var rest = unpinned.Where(c => c != main).ToList();
        var ordered = rest
            .OrderBy(c => c.IsEdgeCluster ? 1 : 0)
            .ThenByDescending(c => CountSharedClusterNets(c, main))
            .ThenBy(c => c.SheetName, StringComparer.Ordinal)
            .ToList();

        var placedAsObstacles = new List<PcbCluster>();
        placedAsObstacles.AddRange(pinned);
        placedAsObstacles.Add(main);
        foreach (var cluster in ordered)
        {
            if (cluster.IsEdgeCluster)
                PlaceEdgeCluster(cluster, placedAsObstacles);
            else
                PlaceRadialCluster(cluster, main, placedAsObstacles);
            placedAsObstacles.Add(cluster);
        }
    }

    /// Place a non-edge cluster radially out from `main`'s centroid, using
    /// the same collision-avoiding hop search the per-component placer uses
    /// but at cluster bbox granularity.
    private void PlaceRadialCluster(PcbCluster cluster, PcbCluster main, List<PcbCluster> placed)
    {
        var (dirX, dirY) = DirectionAwayFromMain(cluster, main, placed);

        double mainHalfX = main.LocalBBox.Width  * 0.5;
        double mainHalfY = main.LocalBBox.Height * 0.5;
        double pHalfX = cluster.LocalBBox.Width  * 0.5;
        double pHalfY = cluster.LocalBBox.Height * 0.5;

        double mainExtent = Math.Abs(dirX) * mainHalfX + Math.Abs(dirY) * mainHalfY;
        double pExtent    = Math.Abs(dirX) * pHalfX    + Math.Abs(dirY) * pHalfY;
        const double clusterSeparation = 4.0;

        double mainCenterX = main.GlobalX + main.LocalBBox.CenterX;
        double mainCenterY = main.GlobalY + main.LocalBBox.CenterY;
        double natX = mainCenterX + dirX * (mainExtent + pExtent + clusterSeparation);
        double natY = mainCenterY + dirY * (mainExtent + pExtent + clusterSeparation);

        var slot = FindFreeClusterSlot(cluster.LocalBBox, natX, natY, dirX, dirY, placed);
        // If no slot found within the search depth, drop the cluster at its
        // natural position anyway. This still produces a usable (if cramped)
        // PCB; old behaviour was to throw, which gives the user nothing to
        // work with.
        double cx = slot?.X ?? natX;
        double cy = slot?.Y ?? natY;

        cluster.GlobalX = cx - cluster.LocalBBox.CenterX;
        cluster.GlobalY = cy - cluster.LocalBBox.CenterY;
    }

    /// Place an edge cluster against the nearest free board edge. We pick
    /// the edge (top, bottom, left, right) whose midpoint is furthest from
    /// any already-placed cluster's centroid; ties broken by edge order
    /// (top, bottom, left, right).
    private void PlaceEdgeCluster(PcbCluster cluster, List<PcbCluster> placed)
    {
        double pHalfX = cluster.LocalBBox.Width  * 0.5;
        double pHalfY = cluster.LocalBBox.Height * 0.5;
        const double edgeMargin = 5.0;

        // Candidate centroid positions on each board edge.
        var candidates = new (string Edge, double CX, double CY)[]
        {
            ("top",    PcbCenterX,                          PcbCenterY - BoardHalfHeight + pHalfY + edgeMargin),
            ("bottom", PcbCenterX,                          PcbCenterY + BoardHalfHeight - pHalfY - edgeMargin),
            ("left",   PcbCenterX - BoardHalfWidth + pHalfX + edgeMargin, PcbCenterY),
            ("right",  PcbCenterX + BoardHalfWidth - pHalfX - edgeMargin, PcbCenterY),
        };

        // Pick the candidate furthest from any placed cluster centroid that
        // is collision-free. Fall back to the furthest candidate regardless
        // if all collide.
        (double CX, double CY)? best = null;
        double bestScore = double.NegativeInfinity;
        (double CX, double CY) bestAny = (candidates[0].CX, candidates[0].CY);
        double bestAnyScore = double.NegativeInfinity;
        foreach (var (_, cx, cy) in candidates)
        {
            double score = double.PositiveInfinity;
            foreach (var p in placed)
            {
                double dx = (p.GlobalX + p.LocalBBox.CenterX) - cx;
                double dy = (p.GlobalY + p.LocalBBox.CenterY) - cy;
                double d2 = dx * dx + dy * dy;
                if (d2 < score) score = d2;
            }
            if (score > bestAnyScore) { bestAnyScore = score; bestAny = (cx, cy); }

            if (!ClusterCollides(cluster.LocalBBox, cx, cy, placed) && score > bestScore)
            {
                bestScore = score;
                best = (cx, cy);
            }
        }

        var (bx, by) = best ?? bestAny;
        cluster.GlobalX = bx - cluster.LocalBBox.CenterX;
        cluster.GlobalY = by - cluster.LocalBBox.CenterY;
    }

    /// Direction unit vector from `main`'s centroid toward where `cluster`
    /// should naturally go. For now: away from the placed-clusters centroid
    /// of mass. Falls back to (+X) when no other clusters are placed.
    private static (double X, double Y) DirectionAwayFromMain(
        PcbCluster cluster, PcbCluster main, List<PcbCluster> placed)
    {
        if (placed.Count == 1)
        {
            // Hash the sheet name to a pseudo-random ray so the first few
            // clusters fan out instead of stacking on the +X side.
            int h = 0;
            foreach (var c in cluster.SheetName) h = h * 31 + c;
            double angle = (Math.Abs(h) % 360) * Math.PI / 180.0;
            return (Math.Cos(angle), Math.Sin(angle));
        }

        double mainCx = main.GlobalX + main.LocalBBox.CenterX;
        double mainCy = main.GlobalY + main.LocalBBox.CenterY;
        double sumX = 0, sumY = 0;
        int n = 0;
        foreach (var p in placed)
        {
            if (p == main) continue;
            sumX += (p.GlobalX + p.LocalBBox.CenterX) - mainCx;
            sumY += (p.GlobalY + p.LocalBBox.CenterY) - mainCy;
            n++;
        }
        if (n == 0) return (1, 0);

        // Walk opposite to the current centre-of-mass.
        double cx = -sumX / n, cy = -sumY / n;
        double mag = Math.Sqrt(cx * cx + cy * cy);
        return mag < 0.01 ? (1, 0) : (cx / mag, cy / mag);
    }

    private static int CountSharedClusterNets(PcbCluster a, PcbCluster b)
    {
        int c = 0;
        foreach (var n in a.Nets)
            if (b.Nets.Contains(n)) c++;
        return c;
    }

    private static bool ClusterCollides(BBox localBBox, double centerX, double centerY, List<PcbCluster> placed)
    {
        double minX = centerX + localBBox.MinX - localBBox.CenterX;
        double maxX = centerX + localBBox.MaxX - localBBox.CenterX;
        double minY = centerY + localBBox.MinY - localBBox.CenterY;
        double maxY = centerY + localBBox.MaxY - localBBox.CenterY;
        const double pad = 2.0;
        foreach (var p in placed)
        {
            double oMinX = p.GlobalX + p.LocalBBox.MinX, oMaxX = p.GlobalX + p.LocalBBox.MaxX;
            double oMinY = p.GlobalY + p.LocalBBox.MinY, oMaxY = p.GlobalY + p.LocalBBox.MaxY;
            if (minX - pad < oMaxX && maxX + pad > oMinX
                && minY - pad < oMaxY && maxY + pad > oMinY)
                return true;
        }
        return false;
    }

    /// Cluster-bbox version of FindClosestFreeSlot: walk forward / perp+ /
    /// perp- past any colliding cluster until a free spot is found. Bounded
    /// recursion. Returns null if no free spot inside the depth limit, in
    /// which case the caller drops the cluster at its natural position.
    private static (double X, double Y)? FindFreeClusterSlot(
        BBox localBBox, double natX, double natY,
        double dirX, double dirY, List<PcbCluster> placed)
    {
        double perpX = -dirY, perpY = dirX;
        var found = new List<(double X, double Y)>();
        var visited = new HashSet<(long, long)>();
        const int maxDepth = 14;
        const double clusterClearance = 4.0;

        void Search(double cx, double cy, int depth)
        {
            if (depth > maxDepth) return;
            long kx = (long)Math.Round(cx * 10);
            long ky = (long)Math.Round(cy * 10);
            if (!visited.Add((kx, ky))) return;
            var collider = FindClusterCollider(localBBox, cx, cy, placed);
            if (collider is null) { found.Add((cx, cy)); return; }
            var c = collider.Value;
            double pMinX = cx + localBBox.MinX - localBBox.CenterX;
            double pMaxX = cx + localBBox.MaxX - localBBox.CenterX;
            double pMinY = cy + localBBox.MinY - localBBox.CenterY;
            double pMaxY = cy + localBBox.MaxY - localBBox.CenterY;
            var capRect = new BBox(pMinX, pMinY, pMaxX, pMaxY);
            double fwdStep = ProjMax(c, dirX, dirY)   - ProjMin(capRect, dirX, dirY)   + clusterClearance;
            double upStep  = ProjMax(c, perpX, perpY) - ProjMin(capRect, perpX, perpY) + clusterClearance;
            double dnStep  = ProjMax(c, -perpX, -perpY) - ProjMin(capRect, -perpX, -perpY) + clusterClearance;
            Search(cx + dirX * fwdStep, cy + dirY * fwdStep, depth + 1);
            Search(cx + perpX * upStep, cy + perpY * upStep, depth + 1);
            Search(cx - perpX * dnStep, cy - perpY * dnStep, depth + 1);
        }
        Search(natX, natY, 0);

        if (found.Count == 0) return null;
        var best = found[0];
        double bestDist = double.PositiveInfinity;
        foreach (var (sx, sy) in found)
        {
            double ddx = sx - natX, ddy = sy - natY;
            double d = ddx * ddx + ddy * ddy;
            if (d < bestDist) { bestDist = d; best = (sx, sy); }
        }
        return best;
    }

    private static BBox? FindClusterCollider(BBox localBBox, double centerX, double centerY, List<PcbCluster> placed)
    {
        double minX = centerX + localBBox.MinX - localBBox.CenterX;
        double maxX = centerX + localBBox.MaxX - localBBox.CenterX;
        double minY = centerY + localBBox.MinY - localBBox.CenterY;
        double maxY = centerY + localBBox.MaxY - localBBox.CenterY;
        const double pad = 2.0;
        foreach (var p in placed)
        {
            double oMinX = p.GlobalX + p.LocalBBox.MinX, oMaxX = p.GlobalX + p.LocalBBox.MaxX;
            double oMinY = p.GlobalY + p.LocalBBox.MinY, oMaxY = p.GlobalY + p.LocalBBox.MaxY;
            if (minX - pad < oMaxX && maxX + pad > oMinX
                && minY - pad < oMaxY && maxY + pad > oMinY)
                return new BBox(oMinX, oMinY, oMaxX, oMaxY);
        }
        return null;
    }

    // ── Edge-cluster detection ─────────────────────────────────────────

    /// True when ≥ 50% of the cluster's owned refs are board-edge connectors
    /// (USB, microSD, HDMI, barrel jack, RJ45, audio jack). Detection is by
    /// symbol-library prefix only — no YAML annotation required, no
    /// resolved-footprint lookup needed.
    private static readonly Regex EdgeConnectorPattern = new(
        @"^(Connector:(USB|HDMI|DisplayPort|DVI|VGA|RJ|Audio|BarrelJack|DIN|PowerJack|Jack)|" +
        @"Connector_USB:|Connector_HDMI:|Connector_DisplayPort:|Connector_DVI:|Connector_VGA:|" +
        @"Connector_RJ:|Connector_Audio:|Connector_BarrelJack:|Connector_Card:microSD|" +
        @"Connector_Card:SD|Connector_Coaxial:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool DetectEdgeCluster(
        HashSet<string> ownedRefs,
        Dictionary<string, List<(string Sheet, ComponentDef Comp)>> byRefAll)
    {
        if (ownedRefs.Count == 0) return false;
        int edge = 0;
        foreach (var r in ownedRefs)
            if (EdgeConnectorPattern.IsMatch(byRefAll[r].First().Comp.Symbol)) edge++;
        // Require BOTH a majority and at least one match. Pure-passive sheets
        // (R/C/L only) never get tagged as edge clusters even though they have
        // 100% "non-edge" — guard with `edge > 0`.
        return edge > 0 && edge * 2 >= ownedRefs.Count;
    }

    // ── Per-cluster anchor + peripheral placement ──────────────────────

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

    private static void PlacePeripheral(PcbPlacement result, PcbContext ctx, string refDes, HashSet<string> absoluteRefs)
    {
        var first = ctx.ByRef[refDes].OrderBy(t => t.Comp.Unit).First().Comp;
        var fp = ctx.FpByRef[refDes];

        double x, y, rot;
        if (first.PcbAt is { } at)
        {
            x = at.X;
            y = at.Y;
            rot = first.PcbRotate ?? 0;
            absoluteRefs.Add(refDes);
        }
        else
        {
            var (dirX, dirY) = DirectionToAnchorCentroid(ctx, refDes);

            double pHalfX = fp is null ? 2.5 : fp.BoundingBox.Width  * 0.5;
            double pHalfY = fp is null ? 2.5 : fp.BoundingBox.Height * 0.5;

            double anchorExtentInDir = Math.Abs(dirX) * ctx.AnchorHalfX + Math.Abs(dirY) * ctx.AnchorHalfY;
            double pExtentInDir      = Math.Abs(dirX) * pHalfX          + Math.Abs(dirY) * pHalfY;
            const double seedClearance = 1.5;
            double natX = ctx.AnchorX + dirX * (anchorExtentInDir + pExtentInDir + seedClearance);
            double natY = ctx.AnchorY + dirY * (anchorExtentInDir + pExtentInDir + seedClearance);

            BBox candidateBB = fp?.BoundingBox ?? SyntheticBBox(pHalfX, pHalfY);
            var bestPos = FindClosestFreeSlot(result, candidateBB, natX, natY, dirX, dirY);
            // No free slot inside the per-cluster depth limit: take the
            // natural position anyway. Cluster placement absorbs the spill
            // (every cluster's bbox already accounts for these stragglers).
            x = bestPos?.X ?? natX;
            y = bestPos?.Y ?? natY;
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

    private const int MaxSearchDepth = 12;
    private const double PcbClearance = 1.5;

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

    private static BBox ComputeBBox(PcbPlacement local, HashSet<string> excludeRefs)
    {
        if (local.Footprints.Count == 0) return new BBox(0, 0, 0, 0);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        int included = 0;
        foreach (var fp in local.Footprints)
        {
            if (excludeRefs.Contains(fp.Ref)) continue;
            var bb = fp.Def?.BoundingBox ?? SyntheticBBox(2.5, 2.5);
            double fpMinX = fp.X + bb.MinX, fpMaxX = fp.X + bb.MaxX;
            double fpMinY = fp.Y + bb.MinY, fpMaxY = fp.Y + bb.MaxY;
            if (fpMinX < minX) minX = fpMinX;
            if (fpMinY < minY) minY = fpMinY;
            if (fpMaxX > maxX) maxX = fpMaxX;
            if (fpMaxY > maxY) maxY = fpMaxY;
            included++;
        }
        return included == 0 ? new BBox(0, 0, 0, 0) : new BBox(minX, minY, maxX, maxY);
    }

    private static bool TryD(SExpr e, out double v)
    {
        v = 0;
        return e is SAtom a
            && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}

