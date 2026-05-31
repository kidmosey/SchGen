using System.Globalization;

namespace Schgen.Core.KiCad;

/// View of a .pretty directory: each .kicad_mod inside is one footprint.
public sealed class FootprintLibrary
{
    public string LibraryNickname { get; }
    public IReadOnlyDictionary<string, FootprintDef> Footprints { get; }

    private FootprintLibrary(string nick, IReadOnlyDictionary<string, FootprintDef> fps)
    {
        LibraryNickname = nick;
        Footprints = fps;
    }

    /// Load every .kicad_mod under a .pretty directory. The directory's basename
    /// (with .pretty stripped) becomes the library nickname.
    public static FootprintLibrary LoadDir(string dirPath)
    {
        if (!Directory.Exists(dirPath))
            throw new DirectoryNotFoundException(dirPath);

        var nick = Path.GetFileName(dirPath.TrimEnd('/', '\\'));
        if (nick.EndsWith(".pretty", StringComparison.Ordinal))
            nick = nick[..^".pretty".Length];

        var dict = new Dictionary<string, FootprintDef>(StringComparer.Ordinal);
        foreach (var mod in Directory.EnumerateFiles(dirPath, "*.kicad_mod"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(mod);
                var def = FootprintDef.FromText(File.ReadAllText(mod), name);
                if (def is not null) dict[name] = def;
            }
            catch (FormatException)
            {
                // Skip malformed footprints; the validator surfaces the missing
                // footprint downstream by name.
            }
        }
        return new FootprintLibrary(nick, dict);
    }

    public FootprintDef? TryGet(string name) =>
        Footprints.TryGetValue(name, out var f) ? f : null;
}

public sealed class FootprintDef
{
    public string Name { get; }
    public BBox BoundingBox { get; }
    /// Source-verbatim `(footprint "Name" ...)` node from the .kicad_mod file.
    /// Used by PcbEmitter to embed real pad geometry, silkscreen, and courtyard
    /// rather than hand-rolling stubs.
    public SList RawNode { get; }
    /// Pad-name -> pad-node lookup so PcbEmitter can attach nets per pad
    /// without re-scanning the raw tree each time.
    public IReadOnlyDictionary<string, SList> PadsByName { get; }

    public FootprintDef(string name, BBox bbox, SList rawNode, IReadOnlyDictionary<string, SList> padsByName)
    {
        Name = name;
        BoundingBox = bbox;
        RawNode = rawNode;
        PadsByName = padsByName;
    }

    public static FootprintDef? FromText(string text, string defaultName)
    {
        var root = (SList)SExpr.Parse(text);
        if (root.Head != "footprint" && root.Head != "module")
            return null;

        string name = defaultName;
        if (root.Items.Count >= 2 && root.Items[1] is SAtom a) name = a.Value;

        var bbox = ComputeBBox(root);

        var pads = new Dictionary<string, SList>(StringComparer.Ordinal);
        foreach (var pad in root.All("pad"))
        {
            // (pad "<name>" <type> <shape> ...) - pad name is items[1].
            if (pad.Items.Count >= 2 && pad.Items[1] is SAtom padNameAtom)
                pads[padNameAtom.Value] = pad;
        }

        return new FootprintDef(name, bbox, root, pads);
    }

    private static BBox ComputeBBox(SList root)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        void Update(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        // Pads contribute their position; courtyard / fab outlines contribute
        // (start,end) of fp_line, fp_rect. Conservative bbox is the union of
        // all pad positions plus courtyard lines.
        foreach (var pad in root.All("pad"))
        {
            var at = pad.First("at");
            if (at is null || at.Items.Count < 3) continue;
            if (!TryDouble(at.Items[1], out var x)) continue;
            if (!TryDouble(at.Items[2], out var y)) continue;
            // Approximate pad extents by adding its size box, if present.
            var size = pad.First("size");
            double w = 0, h = 0;
            if (size is not null && size.Items.Count >= 3)
            {
                TryDouble(size.Items[1], out w);
                TryDouble(size.Items[2], out h);
            }
            Update(x - w * 0.5, y - h * 0.5);
            Update(x + w * 0.5, y + h * 0.5);
        }

        foreach (var line in root.All("fp_line"))
        {
            var s = line.First("start");
            var e = line.First("end");
            if (s is not null && s.Items.Count >= 3
                && TryDouble(s.Items[1], out var sx) && TryDouble(s.Items[2], out var sy))
                Update(sx, sy);
            if (e is not null && e.Items.Count >= 3
                && TryDouble(e.Items[1], out var ex) && TryDouble(e.Items[2], out var ey))
                Update(ex, ey);
        }

        foreach (var rect in root.All("fp_rect"))
        {
            var s = rect.First("start");
            var e = rect.First("end");
            if (s is not null && s.Items.Count >= 3
                && TryDouble(s.Items[1], out var sx) && TryDouble(s.Items[2], out var sy))
                Update(sx, sy);
            if (e is not null && e.Items.Count >= 3
                && TryDouble(e.Items[1], out var ex) && TryDouble(e.Items[2], out var ey))
                Update(ex, ey);
        }

        if (double.IsPositiveInfinity(minX))
        {
            // 0805 default if nothing parseable
            return new BBox(-1.0, -0.6, 1.0, 0.6);
        }
        return new BBox(minX, minY, maxX, maxY);
    }

    private static bool TryDouble(SExpr e, out double v)
    {
        v = 0;
        return e is SAtom a
            && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
