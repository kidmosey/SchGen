using System.Globalization;
using System.Text.RegularExpressions;

namespace Schgen.Core.KiCad;

/// KiCad sub-symbol naming convention: `<base>_<unit>_<bodyStyle>`. Unit 0
/// means "shared across all units" (the parent body). Centralised here so
/// FanoutGeometry and SymbolLibrary use the same parser.
internal static class SubSymbolName
{
    private static readonly Regex Pattern = new(@"_(\d+)_(\d+)$", RegexOptions.Compiled);

    /// Returns the unit number, or null if the name doesn't match the
    /// `_N_M$` convention.
    public static int? TryParseUnit(string subName)
    {
        var m = Pattern.Match(subName);
        return m.Success
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }
}

/// Parsed view of a .kicad_sym file.
public sealed class SymbolLibrary
{
    public string LibraryNickname { get; }
    public IReadOnlyDictionary<string, SymbolDef> Symbols { get; }

    private SymbolLibrary(string nickname, IReadOnlyDictionary<string, SymbolDef> symbols)
    {
        LibraryNickname = nickname;
        Symbols = symbols;
    }

    /// Load either a single-file `.kicad_sym` (legacy + project libs) or
    /// KiCad 10's new `.kicad_symdir` directory format (one `.kicad_sym`
    /// per symbol). The library nickname is the file-or-directory name
    /// minus its extension.
    public static SymbolLibrary Load(string path)
    {
        if (Directory.Exists(path))
            return LoadDirectory(path);
        var text = File.ReadAllText(path);
        var nickname = Path.GetFileNameWithoutExtension(path);
        return FromText(text, nickname);
    }

    /// Load a KiCad 10 `.kicad_symdir` directory. Each `.kicad_sym` file
    /// inside is a single-symbol library; we merge them into one
    /// SymbolLibrary keyed by the symbol name.
    public static SymbolLibrary LoadDirectory(string dirPath)
    {
        var nickname = Path.GetFileNameWithoutExtension(dirPath);
        var symbols = new Dictionary<string, SymbolDef>(StringComparer.Ordinal);
        foreach (var symFile in Directory.EnumerateFiles(dirPath, "*.kicad_sym"))
        {
            var fileLib = FromText(File.ReadAllText(symFile), nickname);
            foreach (var (name, def) in fileLib.Symbols)
                symbols[name] = def;
        }
        ResolveExtends(symbols);
        return new SymbolLibrary(nickname, symbols);
    }

    public static SymbolLibrary FromText(string text, string nickname)
    {
        var root = (SList)SExpr.Parse(text);
        if (root.Head != "kicad_symbol_lib")
            throw new FormatException($"expected 'kicad_symbol_lib' root, got '{root.Head}'");

        var symbols = new Dictionary<string, SymbolDef>(StringComparer.Ordinal);
        foreach (var sym in root.All("symbol"))
        {
            var def = SymbolDef.FromSExpr(sym);
            if (def is null) continue;
            symbols[def.Name] = def;
        }
        ResolveExtends(symbols);
        return new SymbolLibrary(nickname, symbols);
    }

    /// KiCad's symbol inheritance: a derived symbol can declare
    /// `(extends "Parent")` and inherit pins + drawings from the parent,
    /// overriding only specific properties (e.g. Value, Description). For
    /// every symbol with Extends != null and no pins of its own, copy the
    /// parent's pin layout. Single-pass: KiCad permits one level of
    /// extension only.
    private static void ResolveExtends(Dictionary<string, SymbolDef> symbols)
    {
        foreach (var (name, def) in symbols.ToList())
        {
            if (def.Extends is null) continue;
            if (def.Pins.Count > 0) continue;
            if (!symbols.TryGetValue(def.Extends, out var parent)) continue;
            // Re-emit `def` with parent's pin/bbox data; keep its own Name
            // + Extends pointer + RawNode (the latter so SchematicEmitter
            // still writes the derived symbol's own properties).
            symbols[name] = new SymbolDef(
                def.Name,
                parent.Pins,
                parent.BoundingBox,
                def.ReferencePrefix ?? parent.ReferencePrefix,
                def.Extends,
                def.RawNode,
                parent.PinsByUnit,
                parent.UnitBoundingBoxes,
                parent.UnitDrawingBoxes);
        }
    }

    public SymbolDef? TryGet(string name) =>
        Symbols.TryGetValue(name, out var s) ? s : null;
}

public sealed class SymbolDef
{
    public string Name { get; }
    public IReadOnlyList<PinDef> Pins { get; }
    public BBox BoundingBox { get; }
    public string? ReferencePrefix { get; }
    public bool ExtendsAnother => Extends is not null;
    public string? Extends { get; }
    public SList RawNode { get; }
    /// Per-unit pin lookups. Key = unit number (1..N). Includes any pins
    /// marked unit 0 (shared) replicated into every unit's list.
    public IReadOnlyDictionary<int, IReadOnlyList<PinDef>> PinsByUnit { get; }
    /// Per-unit bounding box. Each unit has its own geometry / pin layout
    /// and gets placed independently on the schematic.
    public IReadOnlyDictionary<int, BBox> UnitBoundingBoxes { get; }
    /// Per-unit DRAWINGS-only bbox (rectangles + polylines + arcs, NO pin
    /// endpoints). Used by the placer for slide-and-touch so that pins
    /// extend into the gap between bodies rather than being baked into
    /// the body extent - pin-to-pin attachments end up with pins
    /// coincident and their labels dedup naturally.
    public IReadOnlyDictionary<int, BBox> UnitDrawingBoxes { get; }
    public int UnitCount => UnitBoundingBoxes.Count;
    public bool IsMultiUnit => UnitCount > 1;

    /// A 2-pin passive is identified structurally: exactly two pins.
    /// Refdes-prefix conventions (C, R, L, D, Y, F, FB ...) are fragile -
    /// custom symbols, project-specific naming, or non-Latin prefixes all
    /// fail an enumerated whitelist. Pin count is the actual structural
    /// invariant that matters for radial-growth (one pin towards the chip,
    /// the other pin off into the rail / GND).
    public bool Is2PinPassive => Pins.Count == 2;

    internal SymbolDef(
        string name,
        IReadOnlyList<PinDef> pins,
        BBox bbox,
        string? referencePrefix,
        string? extends,
        SList rawNode,
        IReadOnlyDictionary<int, IReadOnlyList<PinDef>> pinsByUnit,
        IReadOnlyDictionary<int, BBox> unitBBoxes,
        IReadOnlyDictionary<int, BBox> unitDrawingBoxes)
    {
        Name = name;
        Pins = pins;
        BoundingBox = bbox;
        ReferencePrefix = referencePrefix;
        Extends = extends;
        RawNode = rawNode;
        PinsByUnit = pinsByUnit;
        UnitBoundingBoxes = unitBBoxes;
        UnitDrawingBoxes = unitDrawingBoxes;
    }

    /// Pins that belong to the given unit (plus any shared pins from unit 0).
    public IReadOnlyList<PinDef> PinsOfUnit(int unit)
    {
        return PinsByUnit.TryGetValue(unit, out var list) ? list : Array.Empty<PinDef>();
    }

    /// BBox of the given unit's geometry. Falls back to the symbol's
    /// combined bbox if the unit isn't found.
    public BBox UnitBBox(int unit) =>
        UnitBoundingBoxes.TryGetValue(unit, out var b) ? b : BoundingBox;

    /// Drawings-only bbox for the given unit (no pin endpoints). Used by
    /// the placer's slide-and-touch so two attached components leave the
    /// natural pin stick-out as the body-to-body gap, with pin endpoints
    /// coincident at the meeting point. Falls back to UnitBBox if a unit
    /// has no drawings (e.g., the symbol only has pins).
    public BBox UnitDrawingBBox(int unit) =>
        UnitDrawingBoxes.TryGetValue(unit, out var b) ? b : UnitBBox(unit);

    public static SymbolDef? FromSExpr(SList symNode)
    {
        if (symNode.Items.Count < 2 || symNode.Items[1] is not SAtom nameAtom)
            return null;
        var name = nameAtom.Value;

        string? extends = symNode.First("extends")?.StringValue;

        string? refPrefix = null;
        foreach (var prop in symNode.All("property"))
        {
            if (prop.Items.Count >= 3
                && prop.Items[1] is SAtom k
                && prop.Items[2] is SAtom v
                && k.Value == "Reference")
            {
                refPrefix = v.Value;
                break;
            }
        }

        // Walk sub-symbols. Sub-symbol name convention is "<base>_<unit>_<bodyStyle>".
        // Pins inside each sub-symbol belong to that unit. Unit 0 means
        // "shared across all units" (rare in modern symbols).
        var pinsPerUnit = new Dictionary<int, List<PinDef>>();
        var bboxPerUnit = new Dictionary<int, BBox>();
        var drawBoxPerUnit = new Dictionary<int, BBox>();
        int maxUnit = 0;

        foreach (var sub in symNode.All("symbol"))
        {
            if (sub.Items.Count < 2 || sub.Items[1] is not SAtom subNameAtom) continue;
            if (SubSymbolName.TryParseUnit(subNameAtom.Value) is not { } unit) continue;
            maxUnit = Math.Max(maxUnit, unit);

            var unitPins = new List<PinDef>();
            foreach (var pinNode in sub.All("pin"))
            {
                var pd = PinDef.FromSExpr(pinNode, unit);
                if (pd is not null) unitPins.Add(pd);
            }
            if (!pinsPerUnit.TryGetValue(unit, out var list))
            {
                list = new List<PinDef>();
                pinsPerUnit[unit] = list;
            }
            list.AddRange(unitPins);

            // A single unit can have MULTIPLE sub-symbol entries
            // (e.g. `<base>_<unit>_0` holds the body rectangle and
            // `<base>_<unit>_1` holds the pins). Earlier this loop
            // overwrote bboxPerUnit on each iteration, dropping
            // whichever sub came first - so units defined with body
            // and pins in separate sub-symbols ended up with only the
            // pin-endpoint bbox, missing the actual body rectangle.
            var subBox = ComputeUnitBBox(sub, unitPins);
            bboxPerUnit[unit] = bboxPerUnit.TryGetValue(unit, out var existing)
                ? new BBox(
                    Math.Min(existing.MinX, subBox.MinX),
                    Math.Min(existing.MinY, subBox.MinY),
                    Math.Max(existing.MaxX, subBox.MaxX),
                    Math.Max(existing.MaxY, subBox.MaxY))
                : subBox;

            // Drawings-only bbox: same walk but pin endpoints excluded.
            var drawBox = ComputeDrawingsBBox(sub);
            if (drawBox is { } db)
            {
                drawBoxPerUnit[unit] = drawBoxPerUnit.TryGetValue(unit, out var dEx)
                    ? new BBox(
                        Math.Min(dEx.MinX, db.MinX),
                        Math.Min(dEx.MinY, db.MinY),
                        Math.Max(dEx.MaxX, db.MaxX),
                        Math.Max(dEx.MaxY, db.MaxY))
                    : db;
            }
        }

        // Pins under the OUTER symbol (not in any sub-symbol) - rare, but treat
        // them as unit 0 (shared) for completeness.
        var outerPins = new List<PinDef>();
        foreach (var pinNode in symNode.All("pin"))
        {
            var pd = PinDef.FromSExpr(pinNode, 0);
            if (pd is not null) outerPins.Add(pd);
        }
        if (outerPins.Count > 0)
        {
            if (!pinsPerUnit.TryGetValue(0, out var list))
            {
                list = new List<PinDef>();
                pinsPerUnit[0] = list;
            }
            list.AddRange(outerPins);
        }

        // For each non-zero unit, append unit-0 (shared) pins so lookups
        // return everything visible on that unit. This matches KiCad's
        // rendering convention: `<base>_0_1` is the "common" sub-symbol
        // whose pins overlay every other unit's drawing.
        //
        // We ALSO expose unit-0 as its own key in PinsByUnit, so symbols
        // whose pinout lives entirely in `_0_1` (a single-unit symbol that
        // happens to be authored with the unit index `0`) can be addressed
        // by YAML declaring `unit: 0`. Validator accepts comp.Unit >= 0.
        var pinsByUnit = new Dictionary<int, IReadOnlyList<PinDef>>();
        var shared = pinsPerUnit.TryGetValue(0, out var sharedList) ? sharedList : new List<PinDef>();
        if (pinsPerUnit.Count == 0)
        {
            // No sub-symbols and no outer pins. Empty single-unit symbol.
            pinsByUnit[1] = Array.Empty<PinDef>();
            bboxPerUnit[1] = new BBox(0, 0, 2.54, 2.54);
            maxUnit = 1;
        }
        foreach (var (unit, list) in pinsPerUnit)
        {
            if (unit == 0)
            {
                // Standalone unit-0 lookup. `list` is already the shared
                // list; do NOT double-add it.
                pinsByUnit[0] = DedupeByNumberPreferVisible(list);
                continue;
            }
            var combined = new List<PinDef>(list);
            combined.AddRange(shared);
            pinsByUnit[unit] = DedupeByNumberPreferVisible(combined);
        }
        if (maxUnit == 0 && pinsPerUnit.ContainsKey(0))
        {
            // Degenerate: only unit-0 pins exist. Treat as single-unit.
            pinsByUnit[1] = pinsPerUnit[0];
            bboxPerUnit[1] = ComputeUnitBBox(symNode, pinsPerUnit[0]);
            maxUnit = 1;
        }

        // Merge unit-0 (shared) DRAWING bbox into every non-zero unit's
        // drawing bbox. KiCad's `<base>_0_1` sub-symbol holds geometry
        // common to all units (e.g. Device:C puts the two plate-line
        // polylines in unit 0, then pins in unit 1). Without this merge,
        // `UnitDrawingBBox(N)` for the per-instance unit returns just
        // that unit's own drawings (often empty), missing the shared
        // body geometry. Mirrors the pins-merge a few lines above.
        if (drawBoxPerUnit.TryGetValue(0, out var unit0Draw))
        {
            foreach (var unit in bboxPerUnit.Keys.ToList())
            {
                if (unit == 0) continue;
                drawBoxPerUnit[unit] = drawBoxPerUnit.TryGetValue(unit, out var ex)
                    ? new BBox(
                        Math.Min(ex.MinX, unit0Draw.MinX),
                        Math.Min(ex.MinY, unit0Draw.MinY),
                        Math.Max(ex.MaxX, unit0Draw.MaxX),
                        Math.Max(ex.MaxY, unit0Draw.MaxY))
                    : unit0Draw;
            }
        }

        // Combined pins (all units).
        var allPins = pinsByUnit.Values.SelectMany(p => p).Distinct().ToList();
        // BBox covering every unit's geometry.
        var combinedBbox = bboxPerUnit.Values.Aggregate(
            new BBox(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity),
            (acc, b) => new BBox(
                Math.Min(acc.MinX, b.MinX), Math.Min(acc.MinY, b.MinY),
                Math.Max(acc.MaxX, b.MaxX), Math.Max(acc.MaxY, b.MaxY)));
        if (double.IsPositiveInfinity(combinedBbox.MinX))
            combinedBbox = new BBox(0, 0, 2.54, 2.54);

        return new SymbolDef(name, allPins, combinedBbox, refPrefix, extends, symNode,
            pinsByUnit, bboxPerUnit, drawBoxPerUnit);
    }

    /// Collapse multiple pin entries that share a number into a single
    /// canonical pin. easyeda2kicad output sometimes emits the same ball as
    /// BOTH a hidden stacked-power pin AND a visible signal pin — the
    /// visible entry is always the one the user wants on the schematic.
    /// Without this dedup, the YAML's pin attribution gets a label emitted
    /// at the hidden coord too, which collides with co-located pins on
    /// different nets and shorts power rails to ground. Preserves order so
    /// downstream sub-symbol → first-occurrence interplay still works.
    private static List<PinDef> DedupeByNumberPreferVisible(IReadOnlyList<PinDef> pins)
    {
        var byNumber = new Dictionary<string, PinDef>(StringComparer.Ordinal);
        foreach (var p in pins)
        {
            if (string.IsNullOrEmpty(p.Number)) continue;
            if (byNumber.TryGetValue(p.Number, out var existing))
            {
                // Prefer the visible entry. If both visible (or both hidden),
                // keep the first to preserve declaration order.
                if (existing.Hidden && !p.Hidden) byNumber[p.Number] = p;
                continue;
            }
            byNumber[p.Number] = p;
        }
        // Re-emit pins in their original order, dropping the entries that
        // weren't selected by the dedup above.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PinDef>(pins.Count);
        foreach (var p in pins)
        {
            if (string.IsNullOrEmpty(p.Number))
            {
                result.Add(p);
                continue;
            }
            if (!seen.Add(p.Number)) continue;
            result.Add(byNumber[p.Number]);
        }
        return result;
    }

    /// BBox of a single sub-symbol's geometry + its pin endpoints.
    private static BBox ComputeUnitBBox(SList node, IReadOnlyList<PinDef> pins) =>
        ComputeBBox(node, pins);

    /// BBox of a single sub-symbol's drawings (rectangles, polylines, arcs).
    /// Returns null if the sub-symbol has no drawings.
    private static BBox? ComputeDrawingsBBox(SList node)
    {
        var b = ComputeBBox(node, Array.Empty<PinDef>());
        // ComputeBBox returns a 2.54x2.54 fallback when nothing was found.
        // Detect that - for a sub-symbol with no rectangles/polylines/arcs
        // we want to return null rather than a phantom 2.54x2.54 box.
        if (b.MinX == 0 && b.MinY == 0 && b.MaxX == 2.54 && b.MaxY == 2.54) return null;
        return b;
    }

    private static BBox ComputeBBox(SList node, IReadOnlyList<PinDef> pins)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        // Use rectangles, polylines, arcs, and pin endpoints. Conservative.
        void Update(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        void Walk(SList n)
        {
            foreach (var rect in n.All("rectangle"))
            {
                var s = rect.First("start"); var e = rect.First("end");
                if (s is not null && e is not null
                    && ParseXY(s, out var sx, out var sy)
                    && ParseXY(e, out var ex, out var ey))
                {
                    Update(sx, sy); Update(ex, ey);
                }
            }
            foreach (var poly in n.All("polyline"))
            {
                var pts = poly.First("pts");
                if (pts is null) continue;
                foreach (var xy in pts.All("xy"))
                {
                    if (xy.Items.Count >= 3
                        && double.TryParse(((SAtom)xy.Items[1]).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                        && double.TryParse(((SAtom)xy.Items[2]).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    {
                        Update(x, y);
                    }
                }
            }
            foreach (var sub in n.All("symbol")) Walk(sub);
        }

        Walk(node);

        foreach (var p in pins) Update(p.X, p.Y);

        if (double.IsPositiveInfinity(minX))
        {
            // No geometry at all - pick a tiny default box at origin.
            return new BBox(0, 0, 2.54, 2.54);
        }
        return new BBox(minX, minY, maxX, maxY);
    }

    private static bool ParseXY(SList atNode, out double x, out double y)
    {
        x = y = 0;
        if (atNode.Items.Count < 3) return false;
        if (atNode.Items[1] is not SAtom xa || atNode.Items[2] is not SAtom ya) return false;
        return double.TryParse(xa.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(ya.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }
}

public sealed class PinDef
{
    public string Name { get; }
    public string Number { get; }
    public PinType Type { get; }
    public double X { get; }
    public double Y { get; }
    public double Rotation { get; }       // degrees; 0 = pin points right (+X)
    public double Length { get; }         // pin stub length
    /// Whether the lib marked this pin (hide yes). Hidden pins are the
    /// KiCad-canonical way to represent stacked power balls without
    /// cluttering the visible body. When the SAME pin number has both a
    /// hidden entry AND a visible entry (typical when easyeda2kicad output
    /// double-represents a ball — see MT41K's K2 ball with a hidden V_{DD}
    /// AND a visible VSS entry), only the visible one should be carried
    /// into PinsByUnit. Otherwise schgen emits a label at the hidden coord,
    /// which collides with co-located pins of other nets and produces
    /// short-circuits in the netlist.
    public bool Hidden { get; }
    /// Unit number this pin belongs to, parsed from the sub-symbol name
    /// `<Base>_<unit>_<bodyStyle>`. 0 = shared across all units (uncommon).
    /// 1+ = a specific unit.
    public int Unit { get; }

    public PinDef(string name, string number, PinType type, double x, double y, double rotation, double length, int unit = 1, bool hidden = false)
    {
        Name = name; Number = number; Type = type;
        X = x; Y = y; Rotation = rotation; Length = length; Unit = unit;
        Hidden = hidden;
    }

    internal static PinDef? FromSExpr(SList pin, int unit = 1)
    {
        // (pin <electrical-type> <shape> (at x y rot) (length L)
        //   (hide yes)? (name "FOO" (effects ...)) (number "1" (effects ...)))
        if (pin.Items.Count < 3) return null;
        var typeStr = (pin.Items[1] as SAtom)?.Value ?? "passive";
        var at = pin.First("at");
        double x = 0, y = 0, rot = 0;
        if (at is not null && at.Items.Count >= 3)
        {
            double.TryParse(((SAtom)at.Items[1]).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            double.TryParse(((SAtom)at.Items[2]).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            if (at.Items.Count >= 4 && at.Items[3] is SAtom rotAtom)
                double.TryParse(rotAtom.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rot);
        }
        double length = 2.54;
        var lenNode = pin.First("length");
        if (lenNode?.StringValue is { } ls)
            double.TryParse(ls, NumberStyles.Float, CultureInfo.InvariantCulture, out length);

        string name = pin.First("name")?.StringValue ?? "~";
        string number = pin.First("number")?.StringValue ?? "";

        // (hide yes) on a pin shows up as a bare child symbol.
        bool hidden = false;
        var hideNode = pin.First("hide");
        if (hideNode is not null && hideNode.Items.Count >= 2 && hideNode.Items[1] is SAtom hideAtom
            && hideAtom.Value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            hidden = true;
        }

        return new PinDef(name, number, ParseType(typeStr), x, y, rot, length, unit, hidden);
    }

    private static PinType ParseType(string s) => s switch
    {
        "input"          => PinType.Input,
        "output"         => PinType.Output,
        "bidirectional"  => PinType.Bidirectional,
        "tri_state"      => PinType.TriState,
        "passive"        => PinType.Passive,
        "free"           => PinType.Free,
        "unspecified"    => PinType.Unspecified,
        "power_in"       => PinType.PowerIn,
        "power_out"      => PinType.PowerOut,
        "open_collector" => PinType.OpenCollector,
        "open_emitter"   => PinType.OpenEmitter,
        "no_connect"     => PinType.NoConnect,
        _                => PinType.Unspecified,
    };
}

public enum PinType
{
    Input, Output, Bidirectional, TriState, Passive, Free, Unspecified,
    PowerIn, PowerOut, OpenCollector, OpenEmitter, NoConnect,
}

public readonly record struct BBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) * 0.5;
    public double CenterY => (MinY + MaxY) * 0.5;

    public BBox Offset(double dx, double dy) =>
        new(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);

    public bool Overlaps(BBox other) =>
        MinX < other.MaxX && MaxX > other.MinX
        && MinY < other.MaxY && MaxY > other.MinY;
}
