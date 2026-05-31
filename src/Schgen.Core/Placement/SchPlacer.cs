using Schgen.Core.KiCad;
using Schgen.Core.Yaml;

namespace Schgen.Core.Placement;

/// Schematic placer - radial growth from each "UC" (chip), then global
/// shelf-pack of the resulting expanded rects.
///
/// Per-sheet algorithm:
///
/// 1. For every component, compute its rect (symbol unit bbox + every pin's
///    label endpoint extent). Build `_netConnections[netName]` = list of
///    `(component, pin)` endpoints. Multi-net pins contribute one entry per
///    declared net.
///
/// 2. For each UC (`!sym.Is2PinPassive`), grow an expanded rect outward:
///       expanded = UC.rect (placed at origin for now)
///       frontier = [UC]
///       loop until frontier empty:
///         for source in frontier:
///           for each pin on source whose net has EXACTLY one other endpoint:
///             rotate the connected component so its pin is colinear with
///               source's pin (axis aligned with source's outward direction)
///             RECOMPUTE the connected component's rect at rotation
///             slide along source-pin's outward ray until rect touches
///               expanded; add 2 mm
///             if it overlaps anything placed in THIS pass, shift
///               perpendicular to the source-pin axis by 2 mm until clear
///             union into expanded
///             next-frontier <- this placement
///         frontier <- next-frontier
///
/// 3. Standalone passives (caps where every connected net has multiple
///    other endpoints - typical for decoupling on shared rails) get their
///    own 1-component tile at origin.
///
/// 4. Shelf-pack: each UC's expanded rect (or standalone-passive rect) is
///    one tile. Tiles flow left-to-right, wrap rows at page width with a
///    2 mm gap between tiles and between rows. Final layout re-centred
///    around (0, 0). Tiles whose anchor uses `sch_at` are pinned and not
///    packed/re-centred.
/// Diagnostic tracer for the placer. Pass an instance to `SchPlacer.Tracer`
/// to capture every decision the cluster-builder makes about a host-anchored
/// passive: which net is iterated for each source pin, which endpoints make
/// it past the per-net filters, which caps get attached vs. fall through to
/// the standalone path. The default tracer (`NullPlacementTracer`) is a
/// no-op so production builds pay zero cost.
public interface IPlacementTracer
{
    /// One trace event. `category` groups events ("netconn", "iter-pin",
    /// "iter-net", "skip-X", "attach-X", "cross-sheet", "standalone").
    /// `message` is a one-line rendering of the relevant state.
    ///
    /// Performance note: callers pass an interpolated string, so the
    /// message is allocated even when the tracer is `NullPlacementTracer`.
    /// Acceptable here because `SchPlacer.Run()` runs once per build, not
    /// in a tight loop. If trace volume ever becomes a bottleneck, add an
    /// `IsEnabled` property and guard call sites.
    void Trace(string category, string message);
}

/// Default no-op tracer.
public sealed class NullPlacementTracer : IPlacementTracer
{
    public static readonly NullPlacementTracer Instance = new();
    public void Trace(string category, string message) { }
}

/// Collects every trace event in memory. Use for diagnostic test runs.
public sealed class CollectingPlacementTracer : IPlacementTracer
{
    public List<(string Category, string Message)> Events { get; } = new();
    public void Trace(string category, string message) => Events.Add((category, message));
}

/// Writes every trace event to a `TextWriter`. Use for CLI runs gated on an
/// env var or flag.
public sealed class TextWriterPlacementTracer : IPlacementTracer
{
    private readonly TextWriter _writer;
    public TextWriterPlacementTracer(TextWriter writer) { _writer = writer; }
    public void Trace(string category, string message)
        => _writer.WriteLine($"[{category}] {message}");
}

public sealed class SchPlacer
{
    private readonly CircuitDocument _doc;
    private readonly LibraryIndex _libs;

    /// Tracer for placement decisions. Default is no-op. Tests and the CLI's
    /// `--trace-placement` / `SCHGEN_PLACER_TRACE` env var swap in a real
    /// collector / writer.
    public IPlacementTracer Tracer { get; set; } = NullPlacementTracer.Instance;

    public SchPlacer(CircuitDocument doc, LibraryIndex libs)
    {
        _doc = doc;
        _libs = libs;
    }

    public SchPlacement Run()
    {
        // Pre-compute per-sheet `{ net -> LabelGeometry.Kind }` once.
        // FanoutGeometry uses this to pick the correct rendered label width
        // per net type (global / hierarchical = chevron + offset; regular =
        // plain text).
        _labelKindsBySheet = LabelClassifier.KindsBySheet(_doc, _libs);
        var result = new SchPlacement();
        foreach (var (sheetName, sheet) in _doc.Sheets)
        {
            var layout = new SheetLayout { SheetName = sheetName };
            result.Sheets[sheetName] = layout;
            PlaceSheet(sheet, layout);
        }
        return result;
    }

    private Dictionary<string, Dictionary<string, LabelGeometry.Kind>> _labelKindsBySheet = new();

    // ------------------------- Phase 1 + 2 + 3 -------------------------

    private void PlaceSheet(SheetDef sheet, SheetLayout layout)
    {
        PlaceSheetExpanded(sheet, layout);
    }

    /// Schematic placement via per-UC iterative radial expansion plus
    /// recursive collision search for each cap:
    ///
    ///   // setup
    ///   for each component:
    ///     fanoutRect[comp] = FanoutGeometry.Compute(comp at origin)
    ///     for each pin:
    ///       netConnections[net] += (comp, pin)
    ///
    ///   // placement
    ///   for each UC:
    ///     expansion = fanoutRect[UC]
    ///     frontier = [UC]
    ///     while frontier non-empty:
    ///       passExpansion = expansion (snapshot)
    ///       placedThisPass = []
    ///       for each source in frontier, for each pin, for each (eligible) net:
    ///         for each cap on net (excluding source, excluding placed):
    ///           capRot = colinear-rotation(capPin, source pin outward)
    ///           natPos = slide cap outward until clear of passExpansion
    ///           freeSlots = recursive search from natPos: at each collision,
    ///                       step past collider's far edge in (forward, perp+,
    ///                       perp-) and recurse; free slots are leaves
    ///           capPos = closest free slot to source pin
    ///           expansion = passExpansion ∪ cap.fanout (after pass)
    ///           placedThisPass += cap
    ///       frontier = placedThisPass
    ///
    /// Clusters are laid out along +X. Unanchored passives become singletons.
    /// `sch_at` on a UC pins its cluster at the user's coords and excludes
    /// it from the post-place shelf-pack.
    private void PlaceSheetExpanded(SheetDef sheet, SheetLayout layout)
    {
        var components = sheet.Components
            .Select(c => (Comp: c, Sym: _libs.ResolveSymbol(c.Symbol)))
            .Where(t => t.Sym is not null)
            .Select(t => (t.Comp, Sym: t.Sym!))
            .ToList();
        if (components.Count == 0) return;

        static string Key(ComponentDef c) => $"{c.Ref}#{c.Unit}";
        var compByKey = components.ToDictionary(t => Key(t.Comp), t => t.Comp, StringComparer.Ordinal);
        var symByKey  = components.ToDictionary(t => Key(t.Comp), t => t.Sym,  StringComparer.Ordinal);

        // ------------ Phase 1: per-component fanout @ origin, net index ------------

        var hiddenStubs = _doc.GeneratedStubNets;
        var labelKinds = _labelKindsBySheet.GetValueOrDefault(sheet.Name);

        var fanoutAtOrigin = new Dictionary<string, BBox>(StringComparer.Ordinal);
        var netConnections = new Dictionary<string, List<EndpointRef>>(StringComparer.Ordinal);
        foreach (var (comp, sym) in components)
        {
            var key = Key(comp);
            double rot = comp.SchRotate ?? 0;
            fanoutAtOrigin[key] = FanoutGeometry.Compute(sym, comp, rot, 0, 0, hiddenStubs, labelKinds);

            var unitPins = sym.PinsOfUnit(comp.Unit);
            foreach (var (pinId, netList) in comp.Pins)
            {
                var pin = unitPins.FirstOrDefault(p => p.Number == pinId)
                          ?? unitPins.FirstOrDefault(p => p.Name == pinId);
                if (pin is null) continue;
                foreach (var net in netList)
                {
                    if (net == "NC" || string.IsNullOrEmpty(net)) continue;
                    if (!netConnections.TryGetValue(net, out var list))
                        netConnections[net] = list = new List<EndpointRef>();
                    list.Add(new EndpointRef(key, pin));
                }
            }
        }

        // Cross-sheet nets: any net appearing on more than one sheet of the doc.
        var crossSheetNets = new HashSet<string>(StringComparer.Ordinal);
        var netSheetCounts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (sName, sDef) in _doc.Sheets)
        {
            foreach (var c in sDef.Components)
            {
                foreach (var (_, nets) in c.Pins)
                {
                    foreach (var n in nets)
                    {
                        if (n == "NC" || string.IsNullOrEmpty(n)) continue;
                        if (!netSheetCounts.TryGetValue(n, out var set))
                            netSheetCounts[n] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(sName);
                    }
                }
            }
        }
        foreach (var (n, set) in netSheetCounts) if (set.Count > 1) crossSheetNets.Add(n);

        // UC refs per net (distinct by Ref, so multi-unit chips count once).
        var ucRefsPerNet = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (net, ends) in netConnections)
        {
            var refs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in ends)
                if (!symByKey[e.CompKey].Is2PinPassive) refs.Add(compByKey[e.CompKey].Ref);
            ucRefsPerNet[net] = refs;
        }

        // ------------ Phase 2: per-UC clusters along +X ------------

        var placedGlobal = new HashSet<string>(StringComparer.Ordinal);
        double cursorX = 0;
        const double clusterGap = 5.0;

        // owner-key -> [member keys] for the post-place shelf packer.
        var tileMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var ucKeys = components
            .Where(t => !t.Sym.Is2PinPassive)
            .OrderByDescending(t => t.Sym.PinsOfUnit(t.Comp.Unit).Count)
            .ThenBy(t => Key(t.Comp), StringComparer.Ordinal)
            .Select(t => Key(t.Comp))
            .ToList();

        // Tracer dump: every key in netConnections, plus generated stub set
        // (covers C1, C2, C3 from the diagnosis plan).
        Tracer.Trace("netconn", $"sheet={sheet.Name} count={netConnections.Count} " +
            $"stubs=[{string.Join(",", _doc.GeneratedStubNets.OrderBy(s => s))}]");
        foreach (var (net, ends) in netConnections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var refs = string.Join(",", ends.Select(e => $"{compByKey[e.CompKey].Ref}.{e.Pin.Number}"));
            Tracer.Trace("netconn", $"  {net} -> [{refs}]");
        }
        Tracer.Trace("cross-sheet", $"sheet={sheet.Name} count={crossSheetNets.Count} " +
            $"[{string.Join(",", crossSheetNets.OrderBy(s => s))}]");
        foreach (var ucKey in ucKeys)
        {
            if (placedGlobal.Contains(ucKey)) continue;
            PlaceExpandedCluster(ucKey, layout, compByKey, symByKey,
                                 fanoutAtOrigin, netConnections,
                                 crossSheetNets, ucRefsPerNet,
                                 hiddenStubs, labelKinds,
                                 placedGlobal, tileMembers,
                                 ref cursorX, clusterGap, Tracer);
        }

        // Unattached passives - one per cluster, linear along X.
        foreach (var (comp, sym) in components)
        {
            var key = Key(comp);
            if (placedGlobal.Contains(key)) continue;
            var pinSummary = string.Join(",",
                comp.Pins.Select(kv => kv.Key + ":" + string.Join("|", kv.Value)));
            Tracer.Trace("standalone",
                $"sheet={sheet.Name} cap={comp.Ref}#{comp.Unit} " +
                $"host={comp.Host ?? "<none>"} pins=[{pinSummary}]");
            double rot = comp.SchRotate ?? 0;
            var fanZero = fanoutAtOrigin[key];
            double x = cursorX - fanZero.MinX;
            double y = -fanZero.MinY;
            var placed = FanoutGeometry.Compute(sym, comp, rot, x, y, hiddenStubs, labelKinds);
            PutPlacement(layout, key, comp, sym, x, y, rot, placed);
            placedGlobal.Add(key);
            tileMembers[key] = new List<string> { key };
            cursorX += (fanZero.MaxX - fanZero.MinX) + clusterGap;
        }

        // Re-pack the linear row of cluster tiles onto a ~square page.
        PackTilesForUnitAspect(layout, tileMembers);

        // Trim EffectiveBBoxes where two pin endpoints are coincident on a
        // shared net - the emitter dedups the rendered label there, so
        // claiming the outward label extent for BOTH components creates
        // phantom overlaps in downstream packing math + tests. This is the
        // companion to host-coincident attach: where a host cap's anchor
        // pin sits exactly on top of a chip's pin, both bodies share the
        // pin endpoint and the dedup'd label lands at that single point.
        RecomputeEffectiveBBoxesAfterDedup(layout, compByKey, symByKey, _doc.GeneratedStubNets, labelKinds);

        SnapPlacementCoords(layout);

        if (layout.Components.Count == 0)
        {
            layout.UsedArea = new BBox(0, 0, 0, 0);
            return;
        }
        double aminX = layout.Components.Values.Min(p => p.BoundingBox.MinX);
        double amaxX = layout.Components.Values.Max(p => p.BoundingBox.MaxX);
        double aminY = layout.Components.Values.Min(p => p.BoundingBox.MinY);
        double amaxY = layout.Components.Values.Max(p => p.BoundingBox.MaxY);
        layout.UsedArea = new BBox(aminX, aminY, amaxX, amaxY);
    }

    private static void PlaceExpandedCluster(
        string ucKey,
        SheetLayout layout,
        Dictionary<string, ComponentDef> compByKey,
        Dictionary<string, SymbolDef> symByKey,
        Dictionary<string, BBox> fanoutAtOrigin,
        Dictionary<string, List<EndpointRef>> netConnections,
        HashSet<string> crossSheetNets,
        Dictionary<string, HashSet<string>> ucRefsPerNet,
        IReadOnlySet<string>? hiddenStubs,
        IReadOnlyDictionary<string, LabelGeometry.Kind>? labelKinds,
        HashSet<string> placedGlobal,
        Dictionary<string, List<string>> tileMembers,
        ref double cursorX,
        double clusterGap,
        IPlacementTracer tracer)
    {
        tileMembers[ucKey] = new List<string>();
        const double slideMargin = 2.0;
        var ucComp = compByKey[ucKey];
        var ucSym  = symByKey[ucKey];
        double ucRot = ucComp.SchRotate ?? 0;
        string ucRef = ucComp.Ref;

        var local = new Dictionary<string, (double X, double Y, double Rot)>(StringComparer.Ordinal);
        local[ucKey] = (0, 0, ucRot);
        var expansion = fanoutAtOrigin[ucKey];

        var completedNets = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new List<string> { ucKey };

        // Per-cap directional spread via recursive collision search: each
        // cap starts at the natural outward slide position (clear of the
        // chip body), then if collisions with already-placed caps exist,
        // the Search closure below routes past each collider in 3 directions
        // (forward / perp+ / perp-) and picks the closest free slot. For
        // pins near a corner the perpendicular branch wins; for pins on the
        // middle of a chip edge the forward branch wins.

        while (frontier.Count > 0)
        {
            // Snapshot expansion at pass start. Every cap placed during this
            // pass slides against THIS snapshot, not a growing rect. Only at
            // the end of the pass does expansion absorb the new caps.
            var passExpansion = expansion;

            var placedThisPass = new List<(string Key, BBox Fanout)>();
            var nextFrontier = new List<string>();

            foreach (var sourceKey in frontier)
            {
                var srcComp = compByKey[sourceKey];
                var srcSym  = symByKey[sourceKey];
                var (sx, sy, srcRot) = local[sourceKey];

                foreach (var srcPin in srcSym.PinsOfUnit(srcComp.Unit))
                {
                    var srcNets = ListNetsForPin(srcComp, srcPin);
                    tracer.Trace("iter-pin",
                        $"uc={ucRef} pin={srcPin.Number}({srcPin.Name}) " +
                        $"rot={srcPin.Rotation} nets=[{string.Join(",", srcNets)}]");
                    if (!srcNets.Any()) continue;
                    foreach (var net in srcNets)
                    {
                        if (net == "NC" || string.IsNullOrEmpty(net)) continue;
                        if (!completedNets.Add(net))
                        {
                            tracer.Trace("skip-net",
                                $"uc={ucRef} pin={srcPin.Number} net={net} reason=already-completed");
                            continue;
                        }
                        if (crossSheetNets.Contains(net))
                        {
                            tracer.Trace("skip-net",
                                $"uc={ucRef} pin={srcPin.Number} net={net} reason=cross-sheet");
                            continue;
                        }
                        // Skip any net that touches a UC other than this cluster's owner.
                        var refsOnNet = ucRefsPerNet.GetValueOrDefault(net);
                        if (refsOnNet is not null && refsOnNet.Any(r => r != ucRef))
                        {
                            tracer.Trace("skip-net",
                                $"uc={ucRef} pin={srcPin.Number} net={net} " +
                                $"reason=other-uc-on-net refs=[{string.Join(",", refsOnNet)}]");
                            continue;
                        }

                        if (!netConnections.TryGetValue(net, out var ends))
                        {
                            tracer.Trace("skip-net",
                                $"uc={ucRef} pin={srcPin.Number} net={net} reason=no-endpoints");
                            continue;
                        }
                        tracer.Trace("iter-net",
                            $"uc={ucRef} pin={srcPin.Number} net={net} ends={ends.Count}");

                        // Source pin world coord (cluster frame).
                        double srcPinX = sx + RotateX(srcPin.X, srcPin.Y, srcRot);
                        double srcPinY = sy + RotateY(srcPin.X, srcPin.Y, srcRot);
                        double outDeg = (srcPin.Rotation + srcRot + 180.0) % 360.0;
                        double outRad = outDeg * Math.PI / 180.0;
                        double ox = Math.Cos(outRad), oy = Math.Sin(outRad);
                        double perpX = -oy, perpY = ox;

                        foreach (var ep in ends)
                        {
                            if (ep.CompKey == sourceKey) continue;
                            if (placedGlobal.Contains(ep.CompKey))
                            {
                                tracer.Trace("skip-ep",
                                    $"uc={ucRef} net={net} ep={ep.CompKey}.{ep.Pin.Number} reason=already-placed-global");
                                continue;
                            }
                            if (local.ContainsKey(ep.CompKey))
                            {
                                tracer.Trace("skip-ep",
                                    $"uc={ucRef} net={net} ep={ep.CompKey}.{ep.Pin.Number} reason=already-in-cluster");
                                continue;
                            }
                            // Only attach passives. UCs are their own clusters.
                            if (!symByKey[ep.CompKey].Is2PinPassive)
                            {
                                tracer.Trace("skip-ep",
                                    $"uc={ucRef} net={net} ep={ep.CompKey}.{ep.Pin.Number} reason=not-2pin-passive");
                                continue;
                            }

                            var capComp = compByKey[ep.CompKey];
                            var capSym  = symByKey[ep.CompKey];
                            var capPin  = ep.Pin;

                            // Host:-aware attach gating. A cap with `host: <ref>.<pin>`
                            // only attaches when the source pin IS `<ref>.<pin>`; iterating
                            // sibling pins on the same rail skips it so the cap is attached
                            // only at its declared host pin. Caps without `host:` always
                            // attach. Placement itself is uniform slide-and-touch below.
                            if (!string.IsNullOrEmpty(capComp.Host)
                                && Yaml.LibraryIndex.TryParseHostRef(capComp.Host,
                                       out var hRef, out var hPin)
                                && (hRef != ucRef || hPin != srcPin.Number))
                            {
                                tracer.Trace("skip-ep",
                                    $"uc={ucRef} via-pin={srcPin.Number} net={net} " +
                                    $"ep={ep.CompKey}.{ep.Pin.Number} " +
                                    $"reason=host-mismatch want={hRef}.{hPin}");
                                continue;
                            }

                            int capRot  = SolveColinearRotation(capPin, outDeg);
                            tracer.Trace("attach-attempt",
                                $"uc={ucRef} via-pin={srcPin.Number} net={net} " +
                                $"cap={capComp.Ref} anchor-pin={capPin.Number} " +
                                $"outDeg={outDeg} capRot={capRot}");
                            var capFanZero = FanoutGeometry.Compute(capSym, capComp, capRot, 0, 0, hiddenStubs, labelKinds);
                            double anchorLocalX = RotateX(capPin.X, capPin.Y, capRot);
                            double anchorLocalY = RotateY(capPin.X, capPin.Y, capRot);

                            // Natural straight-outward slide: clear the chip
                            // body via the closed-form formula. This is the
                            // search seed; the recursive routing below routes
                            // around already-placed caps if it collides.
                            double natOffset =
                                ProjMax(passExpansion, ox, oy)
                              - ProjMin(capFanZero, ox, oy)
                              - (srcPinX * ox + srcPinY * oy)
                              + (anchorLocalX * ox + anchorLocalY * oy)
                              + slideMargin;
                            if (natOffset < 0) natOffset = 0;
                            double natX = srcPinX + ox * natOffset - anchorLocalX;
                            double natY = srcPinY + oy * natOffset - anchorLocalY;

                            // Route the cap around passExpansion + already-
                            // placed-this-pass caps via the recursive
                            // collision search. Falls back to the natural
                            // position if no free slot exists within the
                            // depth limit (real boards shouldn't hit this).
                            var best = FindClosestFreeCapSlot(
                                capFanZero, passExpansion, placedThisPass,
                                natX, natY, ox, oy, perpX, perpY,
                                srcPinX, srcPinY, slideMargin);
                            double capX = best.X, capY = best.Y;
                            BBox capFanout = capFanZero.Offset(capX, capY);

                            local[ep.CompKey] = (capX, capY, capRot);
                            placedThisPass.Add((ep.CompKey, capFanout));
                            nextFrontier.Add(ep.CompKey);
                            tracer.Trace("attach-ok",
                                $"uc={ucRef} cap={capComp.Ref} pin={capPin.Number} " +
                                $"capX={capX:F2} capY={capY:F2} capRot={capRot}");
                        }
                    }
                }
            }

            // End of pass: NOW expand the running expansion rect by the
            // union of every cap placed in this pass.
            foreach (var (_, capFanout) in placedThisPass)
                expansion = UnionRect(expansion, capFanout);

            frontier = nextFrontier;
        }

        // Translate cluster. If the UC declared `sch_at`, center the cluster
        // on those coords (and skip advancing cursorX so it stays out of the
        // shelf-pack flow - PackExpandedRectsOntoSheet treats SchAt-bearing
        // tiles as pinned). Otherwise lay the cluster out left-to-right with
        // cursorX, bottom-aligned at Y=0.
        double dx, dy;
        if (ucComp.SchAt is { } anchor)
        {
            dx = anchor.X;  // UC is at (0, 0) in cluster coords, so dx/dy = anchor coords
            dy = anchor.Y;
        }
        else
        {
            dx = cursorX - expansion.MinX;
            dy = -expansion.MinY;
        }
        foreach (var (key, p) in local)
        {
            double tx = p.X + dx;
            double ty = p.Y + dy;
            var fan = FanoutGeometry.Compute(symByKey[key], compByKey[key], p.Rot, tx, ty, hiddenStubs, labelKinds);
            PutPlacement(layout, key, compByKey[key], symByKey[key], tx, ty, p.Rot, fan);
            placedGlobal.Add(key);
            tileMembers[ucKey].Add(key);
        }
        if (ucComp.SchAt is null)
            cursorX += (expansion.MaxX - expansion.MinX) + clusterGap;
    }

    /// Recursion depth cap for the cap-routing collision search. 12 hops
    /// covers any reasonable cluster of placed-this-pass caps without
    /// runaway expansion.
    private const int MaxCapSearchDepth = 12;

    /// Route a cap around `passExpansion` and `placedThisPass` via recursive
    /// collision search. At each step: check for the first collider; if free,
    /// the position is a leaf; if colliding, recurse along (outward, perp+,
    /// perp-), each step jumping just past the collider's far edge with
    /// `slideMargin`. Returns the free slot whose cap CENTER is closest to
    /// the host pin (Euclidean). Falls back to `(natX, natY)` (which may
    /// itself overlap) when the search exhausts its depth limit.
    private static (double X, double Y) FindClosestFreeCapSlot(
        BBox capFanZero,
        BBox passExpansion,
        List<(string Key, BBox Fanout)> placedThisPass,
        double natX, double natY,
        double ox, double oy,
        double perpX, double perpY,
        double srcPinX, double srcPinY,
        double slideMargin)
    {
        var freeSlots = new List<(double X, double Y)>();
        var visited = new HashSet<(long, long)>();

        void Search(double posX, double posY, int depth)
        {
            if (depth > MaxCapSearchDepth) return;
            long kx = (long)Math.Round(posX * 100);
            long ky = (long)Math.Round(posY * 100);
            if (!visited.Add((kx, ky))) return;
            var capRect = capFanZero.Offset(posX, posY);
            BBox? collider = null;
            if (capRect.Overlaps(passExpansion))
                collider = passExpansion;
            else
            {
                foreach (var t in placedThisPass)
                    if (t.Fanout.Overlaps(capRect)) { collider = t.Fanout; break; }
            }
            if (collider is null)
            {
                freeSlots.Add((posX, posY));
                return;
            }
            var c = collider.Value;
            double fwdStep = ProjMax(c, ox, oy)        - ProjMin(capRect, ox, oy)        + slideMargin;
            double upStep  = ProjMax(c, perpX, perpY)  - ProjMin(capRect, perpX, perpY)  + slideMargin;
            double dnStep  = ProjMax(c, -perpX, -perpY) - ProjMin(capRect, -perpX, -perpY) + slideMargin;
            Search(posX + ox * fwdStep,    posY + oy * fwdStep,    depth + 1);
            Search(posX + perpX * upStep,  posY + perpY * upStep,  depth + 1);
            Search(posX - perpX * dnStep,  posY - perpY * dnStep,  depth + 1);
        }
        Search(natX, natY, 0);

        if (freeSlots.Count == 0) return (natX, natY);

        var best = freeSlots[0];
        double bestDist = double.PositiveInfinity;
        foreach (var (sx, sy) in freeSlots)
        {
            double cxC = sx + (capFanZero.MinX + capFanZero.MaxX) * 0.5;
            double cyC = sy + (capFanZero.MinY + capFanZero.MaxY) * 0.5;
            double ddx = cxC - srcPinX, ddy = cyC - srcPinY;
            double d = ddx * ddx + ddy * ddy;
            if (d < bestDist) { bestDist = d; best = (sx, sy); }
        }
        return best;
    }

    // Project a BBox onto a unit direction (ox, oy). For axis-aligned rects:
    // min/max over the 4 corners.
    private static double ProjMin(BBox r, double ox, double oy) =>
        Math.Min(Math.Min(r.MinX * ox + r.MinY * oy, r.MaxX * ox + r.MinY * oy),
                 Math.Min(r.MinX * ox + r.MaxY * oy, r.MaxX * ox + r.MaxY * oy));
    private static double ProjMax(BBox r, double ox, double oy) =>
        Math.Max(Math.Max(r.MinX * ox + r.MinY * oy, r.MaxX * ox + r.MinY * oy),
                 Math.Max(r.MinX * ox + r.MaxY * oy, r.MaxX * ox + r.MaxY * oy));



    /// Choose a rotation (0/90/180/270) for the target component so that its
    /// pin's outward direction faces the source pin (i.e., the pins face
    /// each other along the same axis). source's outward direction is given.
    /// The target's pin outward direction will be (target.pinRot + targetRot + 180);
    /// we want this to equal source's outward + 180 (180deg opposite) so the
    /// pins meet on the same ray.
    internal static int SolveColinearRotation(PinDef targetPin, double sourceOutwardDeg)
    {
        // Solve: (target.PinRot + targetRot + 180) % 360 == (sourceOutwardDeg + 180) % 360
        //   ->  targetRot == sourceOutwardDeg - target.PinRot (mod 360)
        double rawDeg = sourceOutwardDeg - targetPin.Rotation;
        int snapped = ((int)Math.Round(rawDeg / 90.0) * 90) % 360;
        return (snapped % 360 + 360) % 360;
    }


    // ------------------------- Phase 3 (shelf pack) -------------------------

    /// Shelf-pack tiles onto a ~1:1 aspect-ratio area. Each tile is a cluster
    /// (UC + its attached passives, or a standalone passive). Sorts by
    /// descending height (stable shelf packing), targets `sqrt(total_area)`
    /// as the shelf-row width so the resulting bbox tends toward square.
    /// Translates each tile's component coords to their packed positions.
    private void PackTilesForUnitAspect(SheetLayout layout, Dictionary<string, List<string>> tileMembers)
    {
        // Tiles whose UC declared `sch_at` are pinned at user-specified coords;
        // they stay out of the shelf-pack and the post-pack re-centering so
        // the user's explicit placement survives.
        var pinnedRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, sheetDef) in _doc.Sheets)
            foreach (var c in sheetDef.Components)
                if (c.SchAt is not null) pinnedRefs.Add(c.Ref);

        var tiles = new List<(string OwnerKey, BBox Bbox, List<ComponentPlacement> Comps)>();
        foreach (var (ownerKey, memberKeys) in tileMembers)
        {
            var comps = memberKeys.Where(k => layout.Components.ContainsKey(k))
                                  .Select(k => layout.Components[k])
                                  .ToList();
            if (comps.Count == 0) continue;
            if (comps.Any(p => pinnedRefs.Contains(p.Ref))) continue;   // pinned: skip pack
            double minX = comps.Min(p => p.EffectiveBBox.MinX);
            double maxX = comps.Max(p => p.EffectiveBBox.MaxX);
            double minY = comps.Min(p => p.EffectiveBBox.MinY);
            double maxY = comps.Max(p => p.EffectiveBBox.MaxY);
            tiles.Add((ownerKey, new BBox(minX, minY, maxX, maxY), comps));
        }
        if (tiles.Count == 0) return;

        const double tileGap = 5.0;
        const double shelfGap = 5.0;
        // Target shelf width = sqrt(area) so the packed bbox is roughly
        // square. Include the inter-tile / inter-shelf gaps in the area
        // estimate so the target accounts for them.
        double totalArea = tiles.Sum(t => (t.Bbox.Width + tileGap) * (t.Bbox.Height + shelfGap));
        double targetWidth = Math.Sqrt(totalArea);

        tiles.Sort((a, b) =>
        {
            int cmp = b.Bbox.Height.CompareTo(a.Bbox.Height);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.OwnerKey, b.OwnerKey);
        });

        double cursorX = 0, cursorY = 0, shelfHeight = 0;
        foreach (var (_, bbox, comps) in tiles)
        {
            if (cursorX > 0 && cursorX + bbox.Width > targetWidth)
            {
                cursorY += shelfHeight + shelfGap;
                cursorX = 0;
                shelfHeight = 0;
            }
            double dx = cursorX - bbox.MinX;
            double dy = cursorY - bbox.MinY;
            foreach (var p in comps) TranslateComponent(p, dx, dy);
            cursorX += bbox.Width + tileGap;
            shelfHeight = Math.Max(shelfHeight, bbox.Height);
        }

        // Re-centre the packed set around (0, 0).
        var allComps = tiles.SelectMany(t => t.Comps).ToList();
        double mMinX = allComps.Min(p => p.BoundingBox.MinX);
        double mMaxX = allComps.Max(p => p.BoundingBox.MaxX);
        double mMinY = allComps.Min(p => p.BoundingBox.MinY);
        double mMaxY = allComps.Max(p => p.BoundingBox.MaxY);
        double shiftX = -(mMinX + mMaxX) * 0.5;
        double shiftY = -(mMinY + mMaxY) * 0.5;
        foreach (var p in allComps) TranslateComponent(p, shiftX, shiftY);
    }

    private void PackExpandedRectsOntoSheet(SheetLayout layout, Dictionary<string, List<string>> tileMembers)
    {
        // Build a "tile" record per owner key.
        var tiles = new List<(string OwnerKey, BBox Bbox, List<ComponentPlacement> Comps, bool Pinned)>();
        var fixedRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, sheetDef) in _doc.Sheets)
            foreach (var c in sheetDef.Components)
                if (c.SchAt is not null) fixedRefs.Add(c.Ref);

        foreach (var (ownerKey, memberKeys) in tileMembers)
        {
            var comps = memberKeys.Where(k => layout.Components.ContainsKey(k))
                                  .Select(k => layout.Components[k])
                                  .ToList();
            if (comps.Count == 0) continue;
            // Use EffectiveBBox (body + outward labels) so tiles get enough
            // breathing room - packing on BoundingBox alone would crowd labels.
            double minX = comps.Min(p => p.EffectiveBBox.MinX);
            double maxX = comps.Max(p => p.EffectiveBBox.MaxX);
            double minY = comps.Min(p => p.EffectiveBBox.MinY);
            double maxY = comps.Max(p => p.EffectiveBBox.MaxY);
            bool pinned = comps.Any(p => fixedRefs.Contains(p.Ref));
            tiles.Add((ownerKey, new BBox(minX, minY, maxX, maxY), comps, pinned));
        }

        // Pinned tiles stay where they are. Pack the rest by descending
        // height for stable shelf packing.
        var movable = tiles.Where(t => !t.Pinned).ToList();
        movable.Sort((a, b) =>
        {
            int cmp = b.Bbox.Height.CompareTo(a.Bbox.Height);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.OwnerKey, b.OwnerKey);
        });

        (double pageW, _) = PageDims(_doc.Config.PageSize);
        double targetWidth = Math.Max(pageW - 40.0, 400.0);
        const double tileGap  = 2.0;
        const double shelfGap = 2.0;
        double cursorX = 0, cursorY = 0, shelfHeight = 0;

        foreach (var (ownerKey, bbox, comps, _) in movable)
        {
            if (cursorX > 0 && cursorX + bbox.Width > targetWidth)
            {
                cursorY += shelfHeight + shelfGap;
                cursorX = 0;
                shelfHeight = 0;
            }
            double dx = cursorX - bbox.MinX;
            double dy = cursorY - bbox.MinY;
            foreach (var p in comps)
                TranslateComponent(p, dx, dy);

            cursorX += bbox.Width + tileGap;
            shelfHeight = Math.Max(shelfHeight, bbox.Height);
        }

        // Re-centre the MOVABLE set around (0, 0). Pinned tiles keep YAML coords.
        var movableComps = movable.SelectMany(t => t.Comps).ToList();
        if (movableComps.Count == 0) return;
        double mMinX = movableComps.Min(p => p.BoundingBox.MinX);
        double mMaxX = movableComps.Max(p => p.BoundingBox.MaxX);
        double mMinY = movableComps.Min(p => p.BoundingBox.MinY);
        double mMaxY = movableComps.Max(p => p.BoundingBox.MaxY);
        double shiftX = -(mMinX + mMaxX) * 0.5;
        double shiftY = -(mMinY + mMaxY) * 0.5;
        foreach (var p in movableComps)
            TranslateComponent(p, shiftX, shiftY);
    }

    private static void TranslateComponent(ComponentPlacement p, double dx, double dy)
    {
        p.X += dx;
        p.Y += dy;
        p.BoundingBox  = p.BoundingBox.Offset(dx, dy);
        p.EffectiveBBox = p.EffectiveBBox.Offset(dx, dy);
        p.ClusterBBox  = p.ClusterBBox.Offset(dx, dy);
    }

    /// Quantize final placement coords to 0.001 mm. Floating-point accumulation
    /// through the radial-growth + tile-pack passes leaves p.X/p.Y values like
    /// `402.06324999999998` instead of `402.06325`; the SchematicEmitter writes
    /// these to `.kicad_sch` with full ToString("R") precision, and KiCad's
    /// integer-nanometer ERC then sees the emit label off the pin by one ULP
    /// (3e-14 mm) and reports "label_dangling". Snapping to 0.001 mm at the
    /// placement boundary makes every downstream computation of pin emit
    /// position deterministic, so sym + lib_pin matches label exactly.
    private static void SnapPlacementCoords(SheetLayout layout)
    {
        foreach (var p in layout.Components.Values)
        {
            double snappedX = Math.Round(p.X, 3);
            double snappedY = Math.Round(p.Y, 3);
            double dx = snappedX - p.X, dy = snappedY - p.Y;
            if (dx != 0 || dy != 0) TranslateComponent(p, dx, dy);
        }
    }

    // ------------- Phase 3.5: trim EffectiveBBox at deduped pins -------------

    /// SchematicEmitter dedups labels at pin endpoints that coincide on the
    /// same net - only one label gets rendered. The placer's EffectiveBBox,
    /// computed per-component before placement, doesn't know about siblings
    /// and so includes the outward label extent on BOTH ends of a coincident
    /// connection. That over-claim shows up as "two components overlap"
    /// even though the labels visually overlap each other (deduped) rather
    /// than crossing into the other body.
    ///
    /// This pass walks every final placement on this sheet, finds pins
    /// whose world coords coincide with another component's pin on the
    /// same net, and recomputes EffectiveBBox without the outward label
    /// extent at those pins. Non-coincident pins keep their full label
    /// extent (KiCad does render those labels and they need clearance).
    private static void RecomputeEffectiveBBoxesAfterDedup(
        SheetLayout layout,
        Dictionary<string, ComponentDef> compByKey,
        Dictionary<string, SymbolDef> symByKey,
        IReadOnlySet<string>? hiddenStubs,
        IReadOnlyDictionary<string, LabelGeometry.Kind>? labelKinds)
    {
        // Build a (world-x, world-y, net) -> count of pin endpoints. Mirrors
        // SchematicEmitter.BuildCoincidentPinIndex: every pin endpoint at the
        // coord increments the count, whether or not it belongs to the same
        // component. When count > 1 the emitter suppresses the label there
        // (KiCad's standard "hide power pins" convention stacks many balls on
        // one screen coord, and we don't want N overlapping labels). The
        // placer's bbox must agree, otherwise it reserves space for a label
        // that the emitter never draws.
        var pinsAtNet = new Dictionary<(long, long, string), int>();
        foreach (var (key, p) in layout.Components)
        {
            if (!compByKey.TryGetValue(key, out var comp)) continue;
            if (!symByKey.TryGetValue(key, out var sym)) continue;
            foreach (var pin in sym.PinsOfUnit(comp.Unit))
            {
                double px = p.X + RotateX(pin.X, pin.Y, p.Rotation);
                double py = p.Y + RotateY(pin.X, pin.Y, p.Rotation);
                long kx = (long)Math.Round(px * 100);
                long ky = (long)Math.Round(py * 100);
                var nets = comp.Pins.TryGetValue(pin.Number, out var byNum) ? byNum
                           : comp.Pins.TryGetValue(pin.Name, out var byName) ? byName
                           : null;
                if (nets is null) continue;
                foreach (var net in nets)
                {
                    if (string.IsNullOrEmpty(net) || net == "NC") continue;
                    pinsAtNet[(kx, ky, net)] = pinsAtNet.GetValueOrDefault((kx, ky, net)) + 1;
                }
            }
        }

        // Recompute each placement's EffectiveBBox skipping outward labels
        // at pins that share their (world, net) with another endpoint (same
        // component or not).
        foreach (var (key, p) in layout.Components)
        {
            if (!compByKey.TryGetValue(key, out var comp)) continue;
            if (!symByKey.TryGetValue(key, out var sym)) continue;
            var trimmed = FanoutGeometry.Compute(
                sym, comp, p.Rotation, p.X, p.Y,
                hiddenStubNets: hiddenStubs,
                labelKindByNet: labelKinds,
                includeOutwardLabels: true,
                coincidentPinsAtNet: pinsAtNet);
            p.EffectiveBBox = trimmed;
            p.ClusterBBox = trimmed;
        }
    }


    // ------------------------- Helpers -------------------------

    private static void PutPlacement(
        SheetLayout layout, string key, ComponentDef comp, SymbolDef sym,
        double x, double y, double rotation, BBox effectiveRect)
    {
        // BoundingBox = the symbol's UNIT body bbox (no labels), translated
        // and rotated. EffectiveBBox = body + outward label extents.
        // Tests and the emitter distinguish between these.
        var ub = sym.UnitBBox(comp.Unit);
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var (bx, by) in new[] { (ub.MinX, ub.MinY), (ub.MaxX, ub.MinY), (ub.MinX, ub.MaxY), (ub.MaxX, ub.MaxY) })
        {
            double rx = x + RotateX(bx, by, rotation);
            double ry = y + RotateY(bx, by, rotation);
            if (rx < minX) minX = rx;
            if (rx > maxX) maxX = rx;
            if (ry < minY) minY = ry;
            if (ry > maxY) maxY = ry;
        }
        var bodyBox = new BBox(minX, minY, maxX, maxY);

        layout.Components[key] = new ComponentPlacement
        {
            Ref = comp.Ref,
            Unit = comp.Unit,
            X = x,
            Y = y,
            Rotation = rotation,
            BoundingBox = bodyBox,
            EffectiveBBox = effectiveRect,
            ClusterBBox = effectiveRect,
            Symbol = sym,
        };
    }


    private static IEnumerable<string> ListNetsForPin(ComponentDef comp, PinDef pin)
    {
        if (comp.Pins.TryGetValue(pin.Number, out var byNum)) return byNum;
        if (comp.Pins.TryGetValue(pin.Name, out var byName))  return byName;
        return Array.Empty<string>();
    }

    private static double RotateX(double x, double y, double rotationDeg)
    {
        double rad = rotationDeg * Math.PI / 180.0;
        return x * Math.Cos(rad) - y * Math.Sin(rad);
    }
    private static double RotateY(double x, double y, double rotationDeg)
    {
        double rad = rotationDeg * Math.PI / 180.0;
        return x * Math.Sin(rad) + y * Math.Cos(rad);
    }

    private static BBox UnionRect(BBox a, BBox b) => new(
        Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY),
        Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));

    private static bool RectOverlaps(BBox a, BBox b) =>
        a.MinX < b.MaxX && a.MaxX > b.MinX && a.MinY < b.MaxY && a.MaxY > b.MinY;

    /// Page dimensions in mm for KiCad's standard paper sizes. Used to
    /// pick a shelf-pack target width that matches the emitted page.
    private static (double width, double height) PageDims(string size) => size.ToUpperInvariant() switch
    {
        "A0" => (1189, 841),
        "A1" => (841, 594),
        "A2" => (594, 420),
        "A3" => (420, 297),
        "A4" => (297, 210),
        "A5" => (210, 148),
        "USLEDGER" => (432, 279),
        "USLEGAL"  => (356, 216),
        "USLETTER" => (279, 216),
        _    => (297, 210),
    };
}

/// One endpoint of a net: a placed component (by composite key) and the pin
/// inside it. Used by the placer's net->endpoints lookup.
internal readonly record struct EndpointRef(string CompKey, PinDef Pin);

internal sealed record AttachedPassive(
    string PassiveRef,
    int PassiveUnit,
    PinDef PassivePin,
    string HostRef,
    int HostUnit,
    PinDef HostPin)
{
    public string PassiveKey => $"{PassiveRef}#{PassiveUnit}";
    public string HostKey => $"{HostRef}#{HostUnit}";
}
