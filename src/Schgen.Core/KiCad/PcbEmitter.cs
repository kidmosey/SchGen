using System.Globalization;
using Schgen.Core.Placement;
using Schgen.Core.Yaml;

namespace Schgen.Core.KiCad;

/// Writes a minimal .kicad_pcb with placed footprints and a net list. No board
/// outline, no copper, no traces, no zones. Just enough that opening pcbnew
/// shows every part in roughly the right location with a ratsnest.
public sealed class PcbEmitter
{
    private const string KicadVersion = "20260206";   // KiCad 10 board format
    private const string Generator = "schgen";
    private const string GeneratorVersion = "10.0";

    private readonly CircuitDocument _doc;
    private readonly LibraryIndex _libs;
    private readonly PcbPlacement _placement;
    private readonly Dictionary<string, int> _netNumbers = new(StringComparer.Ordinal);

    public PcbEmitter(CircuitDocument doc, LibraryIndex libs, PcbPlacement placement)
    {
        _doc = doc;
        _libs = libs;
        _placement = placement;
        AssignNetNumbers();
    }

    public void Write(string outDir, string projectName)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"{projectName}.kicad_pcb");
        File.WriteAllText(path, Emit().Format());
    }

    private void AssignNetNumbers()
    {
        // Net 0 is reserved for "no net" in KiCad PCB files.
        var nets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fp in _placement.Footprints)
            foreach (var (_, net) in fp.PadToNet)
                if (net != "NC")
                    nets.Add(net);
        int n = 1;
        foreach (var net in nets)
            _netNumbers[net] = n++;
    }

    private SExpr Emit()
    {
        var items = new List<SExpr>
        {
            SAtom.Sym("kicad_pcb"),
            new SList(SAtom.Sym("version"), SAtom.Sym(KicadVersion)),
            new SList(SAtom.Sym("generator"), SAtom.Str(Generator)),
            new SList(SAtom.Sym("generator_version"), SAtom.Str(GeneratorVersion)),
            new SList(SAtom.Sym("general"),
                new SList(SAtom.Sym("thickness"), SAtom.Num(1.6)),
                new SList(SAtom.Sym("legacy_teardrops"), SAtom.Sym("no"))),
            new SList(SAtom.Sym("paper"), SAtom.Str(_doc.Config.PageSize)),
            EmitTitleBlock(),
            EmitLayers(),
            EmitSetup(),
        };

        items.Add(new SList(SAtom.Sym("net"), SAtom.Sym("0"), SAtom.Str("")));
        foreach (var (net, num) in _netNumbers.OrderBy(kv => kv.Value))
            items.Add(new SList(SAtom.Sym("net"), SAtom.Sym(num.ToString(CultureInfo.InvariantCulture)), SAtom.Str(net)));

        foreach (var fp in _placement.Footprints)
            items.Add(EmitFootprint(fp));

        return new SList(items);
    }

    private SList EmitTitleBlock()
    {
        return new SList(
            SAtom.Sym("title_block"),
            new SList(SAtom.Sym("title"), SAtom.Str(_doc.Config.Title)),
            new SList(SAtom.Sym("rev"), SAtom.Str(_doc.Config.Rev)),
            new SList(SAtom.Sym("company"), SAtom.Str(_doc.Config.Designer)));
    }

    private SList EmitLayers()
    {
        // Copper layer count derived from footprint density. Boards with a
        // BGA-class footprint (>=50 pads) need >=4 layers to route. Smaller
        // boards stay 2-layer. The threshold is conservative; users can
        // bump higher manually if a 4-layer routing still fails.
        int copperLayers = ComputeCopperLayerCount();
        var layers = new List<SExpr>
        {
            SAtom.Sym("layers"),
            new SList(SAtom.Sym("0"), SAtom.Str("F.Cu"), SAtom.Sym("signal")),
        };
        // Inner copper: In1.Cu .. In(N-2).Cu sit at IDs 1 .. N-2.
        for (int i = 1; i <= copperLayers - 2; i++)
            layers.Add(new SList(SAtom.Sym(i.ToString(CultureInfo.InvariantCulture)),
                                 SAtom.Str($"In{i}.Cu"), SAtom.Sym("signal")));
        layers.Add(new SList(SAtom.Sym("31"), SAtom.Str("B.Cu"), SAtom.Sym("signal")));
        layers.AddRange(LayerStackTail());
        return new SList(layers);
    }

    /// Pad count past which a footprint is "BGA-class" and triggers a 4-layer
    /// stackup. Below this, a 2-layer board is assumed sufficient. The value
    /// is a heuristic: typical fine-pitch QFNs reach the high 40s, BGAs start
    /// at 64. Setting to 50 catches the BGA case without flipping on QFNs.
    private const int BgaPadThreshold = 50;

    private int ComputeCopperLayerCount()
    {
        int maxPads = 0;
        foreach (var (_, sheet) in _doc.Sheets)
        {
            foreach (var comp in sheet.Components)
            {
                var fp = _libs.ResolveFootprint(comp.Footprint);
                int pads = fp?.PadsByName.Count ?? 0;
                if (pads > maxPads) maxPads = pads;
            }
        }
        return maxPads >= BgaPadThreshold ? 4 : 2;
    }

    private static IEnumerable<SExpr> LayerStackTail() => new SExpr[]
    {
        new SList(SAtom.Sym("32"), SAtom.Str("B.Adhes"),  SAtom.Sym("user"), SAtom.Str("B.Adhesive")),
        new SList(SAtom.Sym("33"), SAtom.Str("F.Adhes"),  SAtom.Sym("user"), SAtom.Str("F.Adhesive")),
        new SList(SAtom.Sym("34"), SAtom.Str("B.Paste"),  SAtom.Sym("user")),
        new SList(SAtom.Sym("35"), SAtom.Str("F.Paste"),  SAtom.Sym("user")),
        new SList(SAtom.Sym("36"), SAtom.Str("B.SilkS"),  SAtom.Sym("user"), SAtom.Str("B.Silkscreen")),
        new SList(SAtom.Sym("37"), SAtom.Str("F.SilkS"),  SAtom.Sym("user"), SAtom.Str("F.Silkscreen")),
        new SList(SAtom.Sym("38"), SAtom.Str("B.Mask"),   SAtom.Sym("user")),
        new SList(SAtom.Sym("39"), SAtom.Str("F.Mask"),   SAtom.Sym("user")),
        new SList(SAtom.Sym("40"), SAtom.Str("Dwgs.User"), SAtom.Sym("user"), SAtom.Str("User.Drawings")),
        new SList(SAtom.Sym("41"), SAtom.Str("Cmts.User"), SAtom.Sym("user"), SAtom.Str("User.Comments")),
        new SList(SAtom.Sym("42"), SAtom.Str("Eco1.User"), SAtom.Sym("user"), SAtom.Str("User.Eco1")),
        new SList(SAtom.Sym("43"), SAtom.Str("Eco2.User"), SAtom.Sym("user"), SAtom.Str("User.Eco2")),
        new SList(SAtom.Sym("44"), SAtom.Str("Edge.Cuts"), SAtom.Sym("user")),
        new SList(SAtom.Sym("45"), SAtom.Str("Margin"),    SAtom.Sym("user")),
        new SList(SAtom.Sym("46"), SAtom.Str("B.CrtYd"),   SAtom.Sym("user"), SAtom.Str("B.Courtyard")),
        new SList(SAtom.Sym("47"), SAtom.Str("F.CrtYd"),   SAtom.Sym("user"), SAtom.Str("F.Courtyard")),
        new SList(SAtom.Sym("48"), SAtom.Str("B.Fab"),     SAtom.Sym("user")),
        new SList(SAtom.Sym("49"), SAtom.Str("F.Fab"),     SAtom.Sym("user")),
    };

    private static SList EmitSetup()
    {
        return new SList(
            SAtom.Sym("setup"),
            new SList(SAtom.Sym("pad_to_mask_clearance"), SAtom.Num(0)),
            new SList(SAtom.Sym("allow_soldermask_bridges_in_footprints"), SAtom.Sym("no")));
    }

    private SList EmitFootprint(FootprintPlacement fp)
    {
        if (fp.Def is null)
        {
            // Footprint library miss - emit a marker block so the user can
            // see which refs are unresolved without crashing the PCB. The
            // Validator already issued a warning at build time.
            return EmitUnresolvedFootprint(fp);
        }

        // Strip the source .kicad_mod root of fields that belong to library
        // authoring metadata, not board-state. The (footprint "Name" ...)
        // wrapper is replaced too - we want "Lib:Name" at the top.
        var raw = fp.Def.RawNode;
        var output = new List<SExpr>
        {
            SAtom.Sym("footprint"),
            SAtom.Str(fp.FootprintLibId),
        };

        // Carry over layer / attr / descr / tags / model / fp_text / fp_line /
        // fp_arc / fp_rect / fp_circle / fp_poly / property (except authoring
        // Reference/Value, which we replace). Pads get the net injection.
        var existingPropertyKeys = new HashSet<string>(StringComparer.Ordinal);

        // Insert placement + uuid + per-instance properties up front so they
        // appear before geometry (matches pcbnew's own emit order).
        output.Add(new SList(
            SAtom.Sym("layer"),
            SAtom.Str(GetSourceLayer(raw) ?? "F.Cu")));
        output.Add(new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid("fp:" + fp.Ref))));
        output.Add(new SList(SAtom.Sym("at"), SAtom.Num(fp.X), SAtom.Num(fp.Y), SAtom.Num(fp.Rotation)));
        // Reference + Value both go on F.Fab (hidden by default in KiCad's
        // PCB view). KiCad still picks them up for assembly drawings and BOM,
        // but the on-screen render stays clean - schgen boards have hundreds
        // of refdes that would otherwise crowd F.SilkS into unreadable mush.
        output.Add(EmitFpProperty("Reference", fp.Ref, 0, -2, "F.Fab", "fpref:" + fp.Ref));
        output.Add(EmitFpProperty("Value", fp.Value ?? fp.FootprintLibId, 0, 2, "F.Fab", "fpval:" + fp.Ref));
        existingPropertyKeys.Add("Reference");
        existingPropertyKeys.Add("Value");

        // Stream children, filtering and rewriting.
        for (int i = 2; i < raw.Items.Count; i++)
        {
            if (raw.Items[i] is not SList child) continue;
            switch (child.Head)
            {
                // Skip: replaced above, or library-authoring noise.
                case "version":
                case "generator":
                case "generator_version":
                case "tedit":
                case "layer":
                case "at":
                case "uuid":
                    continue;

                case "property":
                {
                    // Skip "Reference" and "Value" (we emitted instance-specific
                    // ones above). Other properties (Footprint, Datasheet,
                    // Description, ki_keywords) flow through verbatim.
                    if (child.Items.Count >= 2
                        && child.Items[1] is SAtom k
                        && existingPropertyKeys.Contains(k.Value))
                        continue;
                    output.Add(CloneSExpr(child));
                    break;
                }

                case "pad":
                {
                    // Inject (net N "name") into every pad whose pad-name has
                    // a mapping in fp.PadToNet. Pads with no mapping stay
                    // net-less (unconnected = net 0, KiCad default).
                    var padNameAtom = child.Items.Count >= 2 ? child.Items[1] as SAtom : null;
                    var clonedPad = (SList)CloneSExpr(child);
                    if (padNameAtom is not null
                        && fp.PadToNet.TryGetValue(padNameAtom.Value, out var net)
                        && net != "NC")
                    {
                        int netNum = _netNumbers.TryGetValue(net, out var n) ? n : 0;
                        clonedPad = InjectNetIntoPad(clonedPad, netNum, net);
                    }
                    output.Add(clonedPad);
                    break;
                }

                case "fp_text":
                {
                    // Legacy KiCad footprints (pre-7) carry Reference / Value
                    // as `(fp_text reference REF**)` / `(fp_text value <name>)`.
                    // Skip those - we emit our own instance-specific ones
                    // above. Keep `fp_text user` (free-form labels).
                    var sub = child.Items.Count >= 2 ? child.Items[1] as SAtom : null;
                    if (sub is not null && (sub.Value == "reference" || sub.Value == "value"))
                        continue;
                    output.Add(CloneSExpr(child));
                    break;
                }

                case "fp_arc":
                    // Legacy KiCad 5 / 6 footprints encode arcs as
                    // `(start CENTER) (end ARC_START) (angle SWEEP)`. KiCad 10
                    // dropped that form; arcs must be `(start) (mid) (end)`
                    // with all three points ON the curve. Translate when we
                    // see the legacy shape; pass through unchanged otherwise.
                    output.Add(ConvertLegacyFpArc(child));
                    break;

                case "zone":
                    // Footprint-embedded zones (rule areas / keepouts) are
                    // emitted with BOARD-ABSOLUTE polygon coordinates, NOT
                    // footprint-local — this is what pcbnew writes when it
                    // places a library footprint that contains zones, and
                    // it's also how KiCad's render path expects them.
                    // (Compare to pads, which DO stay footprint-local and
                    // get the (at) transform at render time.)
                    // Walk the zone's (polygon (pts (xy …) …)) subtree and
                    // rewrite every (xy lx ly) leaf to (xy ax ay) by
                    // applying the footprint's placement transform.
                    output.Add(TransformZonePolygons(child, fp.X, fp.Y, fp.Rotation));
                    break;

                default:
                    // fp_line, fp_rect, fp_circle, fp_poly, model,
                    // attr, descr, tags, ... - copy verbatim.
                    output.Add(CloneSExpr(child));
                    break;
            }
        }

        return new SList(output);
    }

    /// Rewrite every `(xy lx ly)` leaf inside `zone` to its board-absolute
    /// equivalent under the footprint's `(at fpX fpY rotDeg)` placement.
    /// Other zone children (layer, uuid, hatch, connect_pads, min_thickness,
    /// keepout, placement, fill, …) pass through unchanged.
    ///
    /// Rotation follows KiCad's convention: positive `rotDeg` rotates CCW in
    /// the lib-local frame (screen Y points down, so the rotation matrix has
    /// `+sin` on the off-diagonal). Verified against the user-supplied
    /// `hardware/kicad/sample/sample/sample.kicad_pcb` at zero rotation,
    /// where `absX = lx + fpX` and `absY = ly + fpY` reproduce the sample
    /// polygon coords exactly.
    internal static SExpr TransformZonePolygons(SList zone, double fpX, double fpY, double rotDeg)
    {
        double rad  = rotDeg * Math.PI / 180.0;
        double cosT = Math.Cos(rad);
        double sinT = Math.Sin(rad);

        SExpr Transform(SExpr node)
        {
            if (node is SAtom a) return new SAtom(a.Value, a.Quoted);
            var list = (SList)node;
            if (list.Head == "xy"
                && list.Items.Count >= 3
                && list.Items[1] is SAtom xa
                && list.Items[2] is SAtom ya
                && double.TryParse(xa.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lx)
                && double.TryParse(ya.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ly))
            {
                double rx = lx * cosT - ly * sinT;
                double ry = lx * sinT + ly * cosT;
                return new SList(
                    SAtom.Sym("xy"),
                    SAtom.Num(rx + fpX),
                    SAtom.Num(ry + fpY));
            }
            return new SList(list.Items.Select(Transform).ToList());
        }
        return Transform(zone);
    }

    /// Drop a `(net N "name")` after the pad's `(layers ...)` block (canonical
    /// pcbnew ordering). If a stale (net ...) was already present in the
    /// source pad (shouldn't happen in .kicad_mod files but tolerate it), it
    /// is replaced.
    private static SList InjectNetIntoPad(SList pad, int netNum, string netName)
    {
        var children = new List<SExpr> { pad.Items[0] };          // "pad"
        bool inserted = false;
        for (int i = 1; i < pad.Items.Count; i++)
        {
            if (pad.Items[i] is SList l && l.Head == "net")
            {
                // Replace existing net entry.
                children.Add(new SList(
                    SAtom.Sym("net"),
                    SAtom.Sym(netNum.ToString(CultureInfo.InvariantCulture)),
                    SAtom.Str(netName)));
                inserted = true;
                continue;
            }
            children.Add(pad.Items[i]);
            if (!inserted && pad.Items[i] is SList layers && layers.Head == "layers")
            {
                children.Add(new SList(
                    SAtom.Sym("net"),
                    SAtom.Sym(netNum.ToString(CultureInfo.InvariantCulture)),
                    SAtom.Str(netName)));
                inserted = true;
            }
        }
        if (!inserted)
        {
            // No (layers ...) found - append at end. Shouldn't normally happen.
            children.Add(new SList(
                SAtom.Sym("net"),
                SAtom.Sym(netNum.ToString(CultureInfo.InvariantCulture)),
                SAtom.Str(netName)));
        }
        return new SList(children);
    }

    /// Translate a legacy `(fp_arc (start CENTER) (end ARC_START) (angle SWEEP))`
    /// into KiCad 10's `(fp_arc (start) (mid) (end))`. Other arc children
    /// (layer, stroke, width, ...) flow through unchanged. If the child is
    /// already in the new form (no `angle`), it's returned as-is.
    /// Internal for direct unit testing.
    internal static SExpr ConvertLegacyFpArc(SList arc)
    {
        var angleNode = arc.First("angle");
        if (angleNode is null) return CloneSExpr(arc);  // already new format
        var startNode = arc.First("start");
        var endNode = arc.First("end");
        if (startNode is null || endNode is null) return CloneSExpr(arc);
        if (!TryXY(startNode, out double cx, out double cy)) return CloneSExpr(arc);
        if (!TryXY(endNode, out double asx, out double asy)) return CloneSExpr(arc);
        if (angleNode.Items.Count < 2 || angleNode.Items[1] is not SAtom angAtom) return CloneSExpr(arc);
        if (!double.TryParse(angAtom.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double sweep))
            return CloneSExpr(arc);

        // Legacy semantics: start = center, end = arc start point, angle = signed sweep.
        // Compute arc midpoint and arc endpoint by rotating (end - center) by
        // sweep/2 and sweep respectively, around the center. Sign of sweep
        // matches KiCad's convention (positive = CW in screen coords); we
        // mirror it by negating the matrix's sin component when needed - but
        // since we're rebuilding the geometry as three points on the curve,
        // the same matrix used for both half-rotation and full-rotation keeps
        // the arc direction consistent.
        double dx = asx - cx, dy = asy - cy;
        double halfRad = sweep * 0.5 * Math.PI / 180.0;
        double fullRad = sweep * Math.PI / 180.0;
        double cosHalf = Math.Cos(halfRad), sinHalf = Math.Sin(halfRad);
        double cosFull = Math.Cos(fullRad), sinFull = Math.Sin(fullRad);
        double mx = cx + dx * cosHalf - dy * sinHalf;
        double my = cy + dx * sinHalf + dy * cosHalf;
        double ex = cx + dx * cosFull - dy * sinFull;
        double ey = cy + dx * sinFull + dy * cosFull;

        var newItems = new List<SExpr>
        {
            SAtom.Sym("fp_arc"),
            new SList(SAtom.Sym("start"), SAtom.Num(asx), SAtom.Num(asy)),
            new SList(SAtom.Sym("mid"),   SAtom.Num(mx),  SAtom.Num(my)),
            new SList(SAtom.Sym("end"),   SAtom.Num(ex),  SAtom.Num(ey)),
        };
        // Carry over everything except the three legacy keys we just rewrote.
        for (int i = 1; i < arc.Items.Count; i++)
        {
            if (arc.Items[i] is SList l && (l.Head == "start" || l.Head == "end" || l.Head == "angle"))
                continue;
            newItems.Add(CloneSExpr(arc.Items[i]));
        }
        return new SList(newItems);
    }

    private static bool TryXY(SList at, out double x, out double y)
    {
        x = y = 0;
        if (at.Items.Count < 3) return false;
        if (at.Items[1] is not SAtom xa || at.Items[2] is not SAtom ya) return false;
        return double.TryParse(xa.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(ya.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

    private static string? GetSourceLayer(SList raw) =>
        raw.First("layer") is { } l && l.Items.Count >= 2 && l.Items[1] is SAtom a ? a.Value : null;

    private SList EmitFpProperty(string key, string value, double x, double y, string layer, string uuidSeed)
    {
        return new SList(
            SAtom.Sym("property"),
            SAtom.Str(key),
            SAtom.Str(value),
            new SList(SAtom.Sym("at"), SAtom.Num(x), SAtom.Num(y), SAtom.Num(0)),
            new SList(SAtom.Sym("layer"), SAtom.Str(layer)),
            new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid(uuidSeed))),
            new SList(SAtom.Sym("effects"),
                new SList(SAtom.Sym("font"),
                    new SList(SAtom.Sym("size"), SAtom.Num(1.0), SAtom.Num(1.0)),
                    new SList(SAtom.Sym("thickness"), SAtom.Num(0.15)))));
    }

    private SList EmitUnresolvedFootprint(FootprintPlacement fp)
    {
        // A visible 1 mm x 1 mm placeholder on the F.Fab layer so the user
        // can see which refdes is missing a real footprint, with one pad per
        // declared net so connectivity still round-trips.
        var node = new List<SExpr>
        {
            SAtom.Sym("footprint"),
            SAtom.Str(fp.FootprintLibId.Length > 0 ? fp.FootprintLibId : "schgen:Unresolved"),
            new SList(SAtom.Sym("layer"), SAtom.Str("F.Cu")),
            new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid("fp:" + fp.Ref))),
            new SList(SAtom.Sym("at"), SAtom.Num(fp.X), SAtom.Num(fp.Y), SAtom.Num(fp.Rotation)),
            EmitFpProperty("Reference", fp.Ref, 0, -2, "F.SilkS", "fpref:" + fp.Ref),
            EmitFpProperty("Value", fp.Value ?? "(unresolved)", 0, 2, "F.Fab", "fpval:" + fp.Ref),
        };
        int slot = 0;
        foreach (var (padName, net) in fp.PadToNet.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (net == "NC") continue;
            int netNum = _netNumbers.TryGetValue(net, out var n) ? n : 0;
            node.Add(new SList(
                SAtom.Sym("pad"),
                SAtom.Str(padName),
                SAtom.Sym("smd"),
                SAtom.Sym("rect"),
                new SList(SAtom.Sym("at"), SAtom.Num(slot * 1.0 - 2), SAtom.Num(0)),
                new SList(SAtom.Sym("size"), SAtom.Num(0.5), SAtom.Num(0.5)),
                new SList(SAtom.Sym("layers"), SAtom.Str("F.Cu"), SAtom.Str("F.Paste"), SAtom.Str("F.Mask")),
                new SList(SAtom.Sym("net"), SAtom.Sym(netNum.ToString(CultureInfo.InvariantCulture)), SAtom.Str(net)),
                new SList(SAtom.Sym("uuid"), SAtom.Str(NewUuid($"pad:{fp.Ref}:{padName}")))));
            slot++;
        }
        return new SList(node);
    }

    private static SExpr CloneSExpr(SExpr node) => node switch
    {
        SAtom a => new SAtom(a.Value, a.Quoted),
        SList l => new SList(l.Items.Select(CloneSExpr).ToList()),
        _       => throw new InvalidOperationException($"unknown SExpr type {node.GetType()}"),
    };

    private static string NewUuid(string seed)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("schgen:" + seed));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return string.Format(CultureInfo.InvariantCulture,
            "{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
            bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
            bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);
    }
}
