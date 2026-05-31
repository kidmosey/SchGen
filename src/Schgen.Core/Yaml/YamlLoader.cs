using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Schgen.Core.Yaml;

/// Reads a circuit YAML file (plus any `includes:`) into a CircuitDocument.
/// The loader is deliberately schema-driven and forgiving about node ordering
/// inside maps - only the shape of children matters.
public static class YamlLoader
{
    public static CircuitDocument Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var doc = new CircuitDocument { SourcePath = fullPath };
        LoadInto(doc, fullPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return doc;
    }

    public static CircuitDocument LoadText(string text, string fakePath = "<inline>")
    {
        var doc = new CircuitDocument { SourcePath = fakePath };
        using var sr = new StringReader(text);
        var yaml = new YamlStream();
        yaml.Load(sr);
        if (yaml.Documents.Count == 0) return doc;
        ApplyRoot(doc, (YamlMappingNode)yaml.Documents[0].RootNode, baseDir: Directory.GetCurrentDirectory(), seenIncludes: new HashSet<string>());
        return doc;
    }

    /// Expand each `root.instantiate` entry that references a `template: true`
    /// sheet into its own concrete sheet. The template definition itself is
    /// dropped from `doc.Sheets`; each instantiation gets its own copy with:
    ///   - Component refs annotated `<original>_<instanceName>` so cross-
    ///     instance footprints stay distinct on the PCB.
    ///   - Pin nets remapped via the instance's `params` (so port name `VBUS`
    ///     becomes whatever `params: { VBUS: ... }` says — typically the
    ///     per-instance rail name).
    ///   - `host:` refs rewritten to point at the renamed components in this
    ///     instance.
    /// After expansion, downstream code (placer, emitters) sees regular non-
    /// template sheets only. The original template stays as a non-instantiated
    /// definition — useful for testing but otherwise inert.
    public static void ExpandTemplateInstances(CircuitDocument doc)
    {
        var newInstantiate = new List<SheetInstance>();
        var newSheets = new Dictionary<string, SheetDef>(StringComparer.Ordinal);

        foreach (var inst in doc.Root.Instantiate)
        {
            if (!doc.Sheets.TryGetValue(inst.Sheet, out var templ) || !templ.Template)
            {
                newInstantiate.Add(inst);
                continue;
            }

            var instanceName = inst.EffectiveName;
            // Ref remap: every distinct ref in the template gets the instance
            // name appended. Repeated refs (a multi-unit chip in a template)
            // share the same remapped name.
            var refRemap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var comp in templ.Components)
            {
                if (!refRemap.ContainsKey(comp.Ref))
                    refRemap[comp.Ref] = $"{comp.Ref}_{instanceName}";
            }

            var newComps = new List<ComponentDef>();
            foreach (var comp in templ.Components)
            {
                var newPins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var (pinId, nets) in comp.Pins)
                {
                    var remapped = new List<string>(nets.Count);
                    foreach (var n in nets)
                        remapped.Add(inst.Params.TryGetValue(n, out var p) ? p : n);
                    newPins[pinId] = remapped;
                }

                string? newHost = comp.Host;
                if (!string.IsNullOrEmpty(newHost))
                {
                    int dot = newHost.IndexOf('.');
                    if (dot > 0)
                    {
                        var hostRef = newHost.Substring(0, dot);
                        var hostPin = newHost.Substring(dot + 1);
                        if (refRemap.TryGetValue(hostRef, out var renamed))
                            newHost = $"{renamed}.{hostPin}";
                    }
                }

                newComps.Add(new ComponentDef
                {
                    Ref          = refRemap[comp.Ref],
                    Symbol       = comp.Symbol,
                    Unit         = comp.Unit,
                    Footprint    = comp.Footprint,
                    Value        = comp.Value,
                    Mpn          = comp.Mpn,
                    Manufacturer = comp.Manufacturer,
                    Tolerance    = comp.Tolerance,
                    Voltage      = comp.Voltage,
                    Datasheet    = comp.Datasheet,
                    Dnp          = comp.Dnp,
                    PcbAt        = comp.PcbAt,
                    PcbRotate    = comp.PcbRotate,
                    SchAt        = comp.SchAt,
                    SchRotate    = comp.SchRotate,
                    Pins         = newPins,
                    Host         = newHost,
                });
            }

            newSheets[instanceName] = new SheetDef
            {
                Name       = instanceName,
                Template   = false,
                Ports      = new Dictionary<string, PortDef>(StringComparer.Ordinal),
                Components = newComps,
            };
            newInstantiate.Add(new SheetInstance
            {
                Sheet  = instanceName,
                As     = null,
                Params = new Dictionary<string, string>(StringComparer.Ordinal),
            });
        }

        // Drop template sheets; keep concrete sheets and inject expanded ones.
        var templateNames = new List<string>();
        foreach (var (n, s) in doc.Sheets)
            if (s.Template) templateNames.Add(n);
        foreach (var n in templateNames)
            doc.Sheets.Remove(n);
        foreach (var (n, s) in newSheets)
            doc.Sheets[n] = s;

        doc.Root.Instantiate.Clear();
        foreach (var ni in newInstantiate)
            doc.Root.Instantiate.Add(ni);
    }

    private static void LoadInto(CircuitDocument doc, string fullPath, HashSet<string> seen)
    {
        if (!seen.Add(fullPath))
            throw new InvalidOperationException($"include cycle detected at {fullPath}");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"YAML not found: {fullPath}", fullPath);

        using var sr = new StreamReader(fullPath);
        var yaml = new YamlStream();
        yaml.Load(sr);
        if (yaml.Documents.Count == 0) return;
        var root = yaml.Documents[0].RootNode;
        if (root is not YamlMappingNode map)
            throw new FormatException($"{fullPath}: root must be a mapping");

        // Includes are loaded relative to the current file's directory and
        // merged into the in-progress document, BEFORE applying the rest of
        // the current file (so the current file's keys can override included
        // ones if needed).
        var baseDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        if (TryMap(map, "includes", out var incNode) && incNode is YamlSequenceNode incSeq)
        {
            foreach (var inc in incSeq.Children)
            {
                var incRel = ScalarString(inc);
                var incPath = Path.GetFullPath(Path.Combine(baseDir, incRel));
                LoadInto(doc, incPath, seen);
            }
        }

        ApplyRoot(doc, map, baseDir, seen);
    }

    private static void ApplyRoot(CircuitDocument doc, YamlMappingNode map, string baseDir, HashSet<string> seenIncludes)
    {
        if (TryMap(map, "libraries", out var libs) && libs is YamlSequenceNode libSeq)
        {
            foreach (var lib in libSeq.Children)
                doc.Libraries.Add(ResolveRelative(baseDir, ScalarString(lib)));
        }

        if (TryMap(map, "footprint_libs", out var fps) && fps is YamlSequenceNode fpSeq)
        {
            foreach (var fp in fpSeq.Children)
                doc.FootprintLibs.Add(ResolveRelative(baseDir, ScalarString(fp)));
        }

        if (TryMap(map, "config", out var cfg) && cfg is YamlMappingNode cfgMap)
        {
            var c = new CircuitConfig
            {
                PageSize        = OptString(cfgMap, "page_size",        doc.Config.PageSize),
                Title           = OptString(cfgMap, "title",            doc.Config.Title),
                Rev             = OptString(cfgMap, "rev",              doc.Config.Rev),
                Designer        = OptString(cfgMap, "designer",         doc.Config.Designer),
                Grid            = OptDouble(cfgMap, "grid",             doc.Config.Grid),
                StrictErc       = OptBool  (cfgMap, "strict_erc",       doc.Config.StrictErc),
                PcbSheetSpacing = OptDouble(cfgMap, "pcb_sheet_spacing", doc.Config.PcbSheetSpacing),
            };
            doc.Config = c;
        }

        if (TryMap(map, "power_nets", out var pn) && pn is YamlSequenceNode pnSeq)
        {
            foreach (var n in pnSeq.Children)
            {
                var s = ScalarString(n);
                if (!doc.PowerNets.Contains(s)) doc.PowerNets.Add(s);
            }
        }

        if (TryMap(map, "power_net_aliases", out var pna) && pna is YamlMappingNode pnaMap)
        {
            foreach (var entry in pnaMap.Children)
            {
                var net = ScalarString(entry.Key);
                if (!doc.PowerNetAliases.TryGetValue(net, out var list))
                    doc.PowerNetAliases[net] = list = new List<string>();
                if (entry.Value is YamlSequenceNode seq)
                {
                    foreach (var item in seq.Children) list.Add(ScalarString(item));
                }
                else
                {
                    foreach (var s in ScalarString(entry.Value).Split(',')
                                                              .Select(s => s.Trim())
                                                              .Where(s => s.Length > 0))
                        list.Add(s);
                }
            }
        }

        if (TryMap(map, "sheets", out var sh) && sh is YamlMappingNode shMap)
        {
            foreach (var entry in shMap.Children)
            {
                var name = ScalarString(entry.Key);
                if (entry.Value is not YamlMappingNode sheetMap)
                    throw new FormatException($"sheet '{name}' must be a mapping");
                doc.Sheets[name] = ParseSheet(name, sheetMap);
            }
        }

        if (TryMap(map, "root", out var rt) && rt is YamlMappingNode rtMap)
        {
            doc.Root.Instantiate.Clear();
            if (TryMap(rtMap, "instantiate", out var instNode) && instNode is YamlSequenceNode instSeq)
            {
                foreach (var item in instSeq.Children)
                {
                    if (item is not YamlMappingNode m)
                        throw new FormatException("root.instantiate entries must be mappings");
                    var si = new SheetInstance
                    {
                        Sheet  = OptString(m, "sheet", ""),
                        As     = TryMap(m, "as", out var asNode) ? ScalarString(asNode) : null,
                    };
                    if (TryMap(m, "params", out var paramsNode) && paramsNode is YamlMappingNode pm)
                    {
                        foreach (var pe in pm.Children)
                            si.Params[ScalarString(pe.Key)] = ScalarString(pe.Value);
                    }
                    doc.Root.Instantiate.Add(si);
                }
            }
        }
    }

    private static SheetDef ParseSheet(string name, YamlMappingNode m)
    {
        var s = new SheetDef
        {
            Name     = name,
            Template = OptBool(m, "template", false),
        };
        if (TryMap(m, "ports", out var pnode) && pnode is YamlMappingNode pmap)
        {
            foreach (var pe in pmap.Children)
            {
                var pname = ScalarString(pe.Key);
                var dir = "passive";
                if (pe.Value is YamlMappingNode pdir)
                    dir = OptString(pdir, "dir", "passive");
                s.Ports[pname] = new PortDef { Dir = dir };
            }
        }
        if (TryMap(m, "components", out var compsNode) && compsNode is YamlSequenceNode cseq)
        {
            foreach (var c in cseq.Children)
            {
                if (c is not YamlMappingNode cmap)
                    throw new FormatException($"sheet '{name}': components must be mappings");
                s.Components.Add(ParseComponent(cmap));
            }
        }
        return s;
    }

    private static ComponentDef ParseComponent(YamlMappingNode m)
    {
        var pins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Parse a pin's net value. Accepted shapes (pin->net direction only;
        // sequence values are reserved for the net->pin reverse form below):
        //   - scalar `NET`                          -> ["NET"]
        //   - scalar `NET_A, NET_B`                 -> ["NET_A", "NET_B"]
        // Multi-net pins emit a label for every net at the same world coord;
        // KiCad merges them electrically. Used to give a cap a dedicated
        // stub net (for proximity-attach) alongside the shared power rail.
        static List<string> ParsePinNets(YamlNode value) =>
            ScalarString(value)
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        // Expand a pin-key into individual pin ids. Accepts:
        //   - scalar `7`                            -> ["7"]
        //   - comma list `2, 4, 6`                  -> ["2", "4", "6"]
        //   - integer range `97-138`                -> ["97", "98", ..., "138"]
        //   - stepped range `2-138/2`               -> ["2", "4", "6", ..., "138"]
        //   - mixed `2-138/2, 140, 150-152`         -> expansion of every token
        // Non-numeric pin ids (e.g. `D+`) and any token that doesn't parse
        // as a `<int>-<int>[/<int>]` range are taken verbatim. Lets connectors
        // collapse `97: GND, 98: GND, ...` into one entry.
        static IEnumerable<string> ExpandPinKey(string key)
        {
            foreach (var token in key.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                // Optional `/step` suffix.
                int step = 1;
                string range = token;
                int slash = token.IndexOf('/');
                if (slash > 0
                    && int.TryParse(token[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedStep)
                    && parsedStep >= 1)
                {
                    step = parsedStep;
                    range = token[..slash];
                }
                int dash = range.IndexOf('-');
                if (dash > 0 && dash < range.Length - 1
                    && int.TryParse(range[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
                    && int.TryParse(range[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi)
                    && lo <= hi)
                {
                    for (int n = lo; n <= hi; n += step)
                        yield return n.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    yield return token;
                }
            }
        }
        // Append nets to the pin's existing list rather than overwriting -
        // this lets a single pin pick up nets from MULTIPLE bulk entries
        // (e.g. a pin in both the shared `VCCA1V8_PMU` bulk and its own
        // dedicated `VCCA1V8_PMU_U1_C7` bulk for cap proximity).
        void AddPinNets(string pinId, IEnumerable<string> nets)
        {
            if (!pins.TryGetValue(pinId, out var existing))
                pins[pinId] = existing = new List<string>();
            existing.AddRange(nets);
        }
        if (TryMap(m, "pins", out var pinsNode))
        {
            if (pinsNode is YamlMappingNode pmap)
            {
                foreach (var pe in pmap.Children)
                {
                    var k = ScalarString(pe.Key);
                    if (k == "bulk" && pe.Value is YamlMappingNode bulk)
                    {
                        foreach (var be in bulk.Children)
                        {
                            var netName = ScalarString(be.Key);
                            if (be.Value is YamlSequenceNode seq)
                            {
                                foreach (var pinId in seq.Children)
                                    foreach (var expanded in ExpandPinKey(ScalarString(pinId)))
                                        AddPinNets(expanded, new[] { netName });
                            }
                            else
                            {
                                foreach (var expanded in ExpandPinKey(ScalarString(be.Value)))
                                    AddPinNets(expanded, new[] { netName });
                            }
                        }
                    }
                    else if (k == "named" && pe.Value is YamlMappingNode named)
                    {
                        foreach (var ne in named.Children)
                        {
                            var nets = ParsePinNets(ne.Value);
                            foreach (var expanded in ExpandPinKey(ScalarString(ne.Key)))
                                AddPinNets(expanded, nets);
                        }
                    }
                    else if (pe.Value is YamlSequenceNode rev)
                    {
                        // Net->pin (reverse) form: the key is a net name, the
                        // sequence items are pin ids (each item itself can
                        // use the comma + range + step expansion). Lets
                        // connectors collapse 60+ GND lines into one.
                        //   GND: ["2-138/2", 140, 142]
                        foreach (var item in rev.Children)
                            foreach (var expanded in ExpandPinKey(ScalarString(item)))
                                AddPinNets(expanded, new[] { k });
                    }
                    else
                    {
                        var nets = ParsePinNets(pe.Value);
                        foreach (var expanded in ExpandPinKey(k))
                            AddPinNets(expanded, nets);
                    }
                }
            }
        }

        return new ComponentDef
        {
            Ref          = OptString(m, "ref", ""),
            Symbol       = OptString(m, "symbol", ""),
            Unit         = (int)OptDouble(m, "unit", 1.0),
            Footprint    = OptString(m, "footprint", ""),
            Value        = OptStringOpt(m, "value"),
            Mpn          = OptStringOpt(m, "mpn"),
            Manufacturer = OptStringOpt(m, "manufacturer"),
            Tolerance    = OptStringOpt(m, "tolerance"),
            Voltage      = OptStringOpt(m, "voltage"),
            Datasheet    = OptStringOpt(m, "datasheet"),
            Dnp          = OptBool(m, "dnp", false),
            PcbAt        = OptPair(m, "pcb_at"),
            PcbRotate    = OptDoubleOpt(m, "pcb_rotate"),
            SchAt        = OptPair(m, "sch_at"),
            SchRotate    = OptDoubleOpt(m, "sch_rotate"),
            Pins         = pins,
            Host         = OptStringOpt(m, "host"),
        };
    }

    // -- helpers --

    private static bool TryMap(YamlMappingNode m, string key, out YamlNode value)
    {
        if (m.Children.TryGetValue(new YamlScalarNode(key), out value!))
            return true;
        value = null!;
        return false;
    }

    private static string ScalarString(YamlNode n) =>
        n is YamlScalarNode s ? (s.Value ?? "") : throw new FormatException($"expected scalar, got {n.NodeType}");

    private static string OptString(YamlMappingNode m, string key, string defaultValue) =>
        TryMap(m, key, out var n) ? ScalarString(n) : defaultValue;

    private static string? OptStringOpt(YamlMappingNode m, string key) =>
        TryMap(m, key, out var n) ? ScalarString(n) : null;

    private static double OptDouble(YamlMappingNode m, string key, double defaultValue)
    {
        if (!TryMap(m, key, out var n)) return defaultValue;
        return double.Parse(ScalarString(n), CultureInfo.InvariantCulture);
    }

    private static double? OptDoubleOpt(YamlMappingNode m, string key)
    {
        if (!TryMap(m, key, out var n)) return null;
        return double.Parse(ScalarString(n), CultureInfo.InvariantCulture);
    }

    private static bool OptBool(YamlMappingNode m, string key, bool defaultValue)
    {
        if (!TryMap(m, key, out var n)) return defaultValue;
        var s = ScalarString(n).Trim().ToLowerInvariant();
        return s is "true" or "yes" or "1" or "on";
    }

    private static (double, double)? OptPair(YamlMappingNode m, string key)
    {
        if (!TryMap(m, key, out var n)) return null;
        if (n is not YamlSequenceNode seq || seq.Children.Count != 2)
            throw new FormatException($"'{key}': expected [x, y]");
        var x = double.Parse(ScalarString(seq.Children[0]), CultureInfo.InvariantCulture);
        var y = double.Parse(ScalarString(seq.Children[1]), CultureInfo.InvariantCulture);
        return (x, y);
    }

    private static string ResolveRelative(string baseDir, string p) =>
        Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
}
