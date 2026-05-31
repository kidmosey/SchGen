using Schgen.Core.KiCad;

namespace Schgen.Core.Yaml;

/// Resolves `"Lib:Symbol"` strings to SymbolDef and `"Lib:Footprint"` to FootprintDef
/// across configured libraries.
///
/// Resolution chain (in order):
///   1. YAML-listed libraries (eager-loaded from `doc.Libraries` / `doc.FootprintLibs`).
///      These are "project libs" - their symbols WILL be embedded in the schematic
///      output so the resulting .kicad_sch is self-contained for project-specific parts.
///   2. CLI `--lib <path>` arguments (eager-loaded, treated as project libs).
///   3. Bundled libs at `<schgen install dir>/libs/{symbols,footprints}/` (lazy).
///   4. Common KiCad install paths: /usr/share/kicad, /Applications/KiCad/...,
///      C:\Program Files\KiCad\..., ~/bin/kicad/squashfs-root/usr/share/kicad,
///      $KICAD_DATA (lazy).
///
/// Lazy steps only trigger when a YAML reference touches a lib nickname that
/// isn't already in the cache. Stock libs are NOT embedded in schematic output;
/// KiCad resolves them via the user's sym-lib-table at open time.
public sealed class LibraryIndex
{
    private readonly Dictionary<string, SymbolLibrary> _symbolLibs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FootprintLibrary> _fpLibs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectSymbolLibs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _projectFootprintLibs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingSymbolLibs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingFootprintLibs = new(StringComparer.Ordinal);
    private readonly List<string> _extraSymbolPaths = new();
    private readonly List<string> _extraFootprintPaths = new();

    /// Library nicknames that came from the YAML's `libraries:` list or from
    /// CLI `--lib` paths. Their symbols are embedded in the schematic output.
    public IReadOnlyCollection<string> ProjectSymbolLibNicknames => _projectSymbolLibs;

    /// Library nicknames that came from the YAML's `footprint_libs:` list or
    /// from CLI `--lib` paths.
    public IReadOnlyCollection<string> ProjectFootprintLibNicknames => _projectFootprintLibs;

    /// Build an in-memory index from pre-loaded libraries (test convenience).
    /// All provided libs are flagged as "project" libs.
    public static LibraryIndex FromLibraries(
        IEnumerable<SymbolLibrary>? symbolLibs = null,
        IEnumerable<FootprintLibrary>? footprintLibs = null)
    {
        var idx = new LibraryIndex();
        foreach (var sl in symbolLibs ?? Array.Empty<SymbolLibrary>())
        {
            idx._symbolLibs[sl.LibraryNickname] = sl;
            idx._projectSymbolLibs.Add(sl.LibraryNickname);
        }
        foreach (var fl in footprintLibs ?? Array.Empty<FootprintLibrary>())
        {
            idx._fpLibs[fl.LibraryNickname] = fl;
            idx._projectFootprintLibs.Add(fl.LibraryNickname);
        }
        return idx;
    }

    /// Build an index from a YAML document plus any `--lib` CLI overrides.
    /// `extraPaths` paths are treated as project libraries.
    public static LibraryIndex FromDocument(CircuitDocument doc, IEnumerable<string>? extraPaths = null)
    {
        var idx = new LibraryIndex();
        foreach (var libPath in doc.Libraries)
            idx.LoadSymbolPath(libPath, isProject: true);
        foreach (var fpDir in doc.FootprintLibs)
            idx.LoadFootprintPath(fpDir, isProject: true);
        foreach (var ep in extraPaths ?? Array.Empty<string>())
        {
            if (LooksLikeSymbolPath(ep))
                idx.LoadSymbolPath(ep, isProject: true);
            else
                idx.LoadFootprintPath(ep, isProject: true);
        }
        ApplyPowerNetPinAutoBind(doc, idx);
        return idx;
    }

    /// For each component with a `host:` attribute, generate a deterministic
    /// stub net and patch the passive's anchor pin + the host pin's net list.
    /// Call AFTER `FromDocument` and AFTER `Validator.Validate` (host:
    /// validation lives there and surfaces errors before this method runs).
    ///
    /// Stub name: `<hostNet>__<hostRef>_<pinId>` (double underscore between
    /// rail and host-pin tag for unambiguous parsing).
    ///
    /// Anchor pin: the passive's pin whose declared net == the host pin's
    /// net. That pin's net list is REPLACED by [stub] -- only the stub
    /// appears on this pad.
    ///
    /// Host pin: the stub is APPENDED as a second net on the host's pin,
    /// alongside the original rail. KiCad's connectivity then merges the
    /// stub (local label at host pin) with the rail (global label at host
    /// pin) into a single electrical net.
    ///
    /// Idempotent: if the stub is already on the host's net list (from a
    /// previous call), it isn't duplicated. The passive's anchor pin is
    /// always rewritten to [stub] - calling twice produces the same shape.
    /// Skips any malformed/incomplete host: entries (should have been
    /// caught by Validator earlier).
    public static void ApplyHostStubNets(CircuitDocument doc)
    {
        foreach (var (sheetName, sheet) in doc.Sheets)
        {
            // Per-sheet refdes -> components map (multi-unit refs collapse).
            var bySheetRef = new Dictionary<string, List<ComponentDef>>(StringComparer.Ordinal);
            foreach (var c in sheet.Components)
            {
                if (!bySheetRef.TryGetValue(c.Ref, out var list))
                    bySheetRef[c.Ref] = list = new List<ComponentDef>();
                list.Add(c);
            }

            foreach (var comp in sheet.Components)
            {
                if (string.IsNullOrEmpty(comp.Host)) continue;
                // Idempotency: if any pin of this comp already points at a
                // previously-generated stub net, we've processed this comp
                // already. Skip to avoid layering stubs on stubs.
                if (comp.Pins.Values.Any(nets => nets.Any(doc.GeneratedStubNets.Contains)))
                    continue;

                if (!TryResolveHost(comp, sheet.Name, bySheetRef, out var hostRef, out var hostPinId, out var hostNets))
                    continue;
                // Host pin may be multi-net (rail + earlier stubs etc.). Find
                // the first NON-STUB net the passive has a matching pin for
                // - that's the rail the cap is meant to anchor to. Skipping
                // stub entries on hostNets prevents stub-on-stub layering.
                string? matchedHostNet = null;
                string? anchorPinId = null;
                foreach (var hn in hostNets)
                {
                    if (doc.GeneratedStubNets.Contains(hn)) continue;
                    if (TryFindAnchorPin(comp, hn, out var ap))
                    {
                        matchedHostNet = hn;
                        anchorPinId = ap;
                        break;
                    }
                }
                if (matchedHostNet is null || anchorPinId is null)
                    continue;

                var stub = $"{matchedHostNet}__{hostRef}_{hostPinId}";
                // APPEND the stub to the passive's anchor pin (don't replace
                // the rail). Keeping the rail name in the pin's net list makes
                // the anchor pin multi-net, so the emitter's "hide stub on
                // multi-net pin" rule kicks in for the cap side too - same
                // way it already does for the host pin.
                var anchorNets = comp.Pins[anchorPinId];
                if (!anchorNets.Contains(stub, StringComparer.Ordinal))
                    anchorNets.Add(stub);
                if (!hostNets.Contains(stub, StringComparer.Ordinal))
                    hostNets.Add(stub);
                doc.GeneratedStubNets.Add(stub);
            }
        }
    }

    /// Parse `U160.11` -> ("U160", "11"). Returns false on malformed input.
    /// Used by both ApplyHostStubNets and Validator's host-rule check.
    internal static bool TryParseHostRef(string? host, out string hostRef, out string pinId)
    {
        hostRef = ""; pinId = "";
        if (string.IsNullOrEmpty(host)) return false;
        var trimmed = host.Trim();
        int dot = trimmed.IndexOf('.');
        if (dot <= 0 || dot >= trimmed.Length - 1 || trimmed.IndexOf('.', dot + 1) >= 0)
            return false;
        hostRef = trimmed.Substring(0, dot);
        pinId   = trimmed.Substring(dot + 1);
        return true;
    }

    /// Locate the host component + the host pin's net list. Returns false
    /// if the host doesn't exist on the sheet or the pin isn't assigned.
    /// `bySheetRef` should be `sheet.Components` grouped by Ref.
    internal static bool TryResolveHost(
        ComponentDef comp, string sheetName,
        Dictionary<string, List<ComponentDef>> bySheetRef,
        out string hostRef, out string hostPinId, out List<string> hostNets)
    {
        hostRef = ""; hostPinId = ""; hostNets = null!;
        if (!TryParseHostRef(comp.Host, out hostRef, out hostPinId)) return false;
        if (!bySheetRef.TryGetValue(hostRef, out var hostUnits)) return false;
        foreach (var hu in hostUnits)
        {
            if (hu.Pins.TryGetValue(hostPinId, out var nets) && nets.Count > 0)
            {
                hostNets = nets;
                return true;
            }
        }
        return false;
    }

    /// Find the pin on `comp` whose first net == hostNet. Returns false if
    /// no such pin exists.
    internal static bool TryFindAnchorPin(ComponentDef comp, string hostNet, out string anchorPinId)
    {
        anchorPinId = "";
        foreach (var (pinId, pNets) in comp.Pins)
        {
            if (pNets.Count > 0 && string.Equals(pNets[0], hostNet, StringComparison.Ordinal))
            {
                anchorPinId = pinId;
                return true;
            }
        }
        return false;
    }

    /// Auto-bind any unassigned chip pin whose NAME matches a declared
    /// `power_nets:` entry (or an alias from `power_net_aliases:`) to that
    /// net. This kills the tedium of listing `1B10: GND, 1B11: GND, ...`
    /// on BGAs with dozens of like-named power balls.
    ///
    /// Match rules:
    ///   1. Pin name == a `power_nets:` entry         -> bind to that net
    ///   2. Pin name matches a `power_net_aliases:`
    ///      glob (trailing `*` wildcard supported)    -> bind to the alias's net
    /// Explicit assignments in `pins:` always win - only pins not mentioned
    /// by either number or name get the auto-bind. Multiple lib pins sharing
    /// the same name (typical on BGAs for VSS / VDD) all get bound.
    ///
    /// Scope is limited to user-declared `power_nets:` / `power_net_aliases:`
    /// deliberately. No code-level naming conventions - the YAML expresses
    /// every pin-name -> net mapping the auto-bind should use.
    internal static void ApplyPowerNetPinAutoBind(CircuitDocument doc, LibraryIndex idx)
    {
        if (doc.PowerNets.Count == 0 && doc.PowerNetAliases.Count == 0) return;
        var powerSet = new HashSet<string>(doc.PowerNets, StringComparer.Ordinal);

        // Compile alias patterns into (net, prefix, exact) tuples for cheap
        // matching. A glob ends with `*` (prefix match); without `*` it's
        // an exact-equals match.
        var aliasPatterns = new List<(string Net, string Pattern, bool IsPrefix)>();
        foreach (var (net, globs) in doc.PowerNetAliases)
            foreach (var g in globs)
            {
                if (g.EndsWith("*", StringComparison.Ordinal))
                    aliasPatterns.Add((net, g.Substring(0, g.Length - 1), true));
                else
                    aliasPatterns.Add((net, g, false));
            }

        string? ResolveBoundNet(string pinName)
        {
            if (powerSet.Contains(pinName)) return pinName;
            foreach (var (net, pat, isPrefix) in aliasPatterns)
            {
                if (isPrefix)
                {
                    if (pinName.StartsWith(pat, StringComparison.Ordinal)) return net;
                }
                else
                {
                    if (string.Equals(pinName, pat, StringComparison.Ordinal)) return net;
                }
            }
            return null;
        }

        foreach (var (_, sheet) in doc.Sheets)
        {
            foreach (var comp in sheet.Components)
            {
                var sym = idx.ResolveSymbol(comp.Symbol);
                if (sym is null) continue;
                foreach (var pin in sym.PinsOfUnit(comp.Unit))
                {
                    if (comp.Pins.ContainsKey(pin.Number)) continue;
                    if (!string.IsNullOrEmpty(pin.Name) && comp.Pins.ContainsKey(pin.Name)) continue;
                    if (string.IsNullOrEmpty(pin.Name)) continue;
                    var net = ResolveBoundNet(pin.Name);
                    if (net is null) continue;
                    comp.Pins[pin.Number] = new List<string> { net };
                    comp.AutoBoundPinIds.Add(pin.Number);
                }
            }
        }
    }

    private static bool LooksLikeSymbolPath(string p)
    {
        if (File.Exists(p) && p.EndsWith(".kicad_sym", StringComparison.Ordinal)) return true;
        if (Directory.Exists(p))
        {
            if (p.EndsWith(".kicad_symdir", StringComparison.Ordinal)) return true;
            // A directory of .kicad_symdir children counts as symbol libs.
            if (Directory.EnumerateDirectories(p, "*.kicad_symdir").Any()) return true;
            if (Directory.EnumerateFiles(p, "*.kicad_sym").Any()) return true;
        }
        return false;
    }

    private void LoadSymbolPath(string libPath, bool isProject)
    {
        // Three accepted shapes:
        //   - foo.kicad_sym                  -> single-file library
        //   - foo.kicad_symdir/              -> KiCad 10 dir-per-library
        //   - <dir containing .kicad_symdir>  -> walk + add each
        if (Directory.Exists(libPath)
            && !libPath.EndsWith(".kicad_symdir", StringComparison.Ordinal)
            && !Directory.EnumerateFiles(libPath, "*.kicad_sym").Any())
        {
            foreach (var sub in Directory.EnumerateDirectories(libPath, "*.kicad_symdir"))
                LoadSingleSymbolLib(sub, isProject);
            return;
        }
        LoadSingleSymbolLib(libPath, isProject);
    }

    private void LoadSingleSymbolLib(string path, bool isProject)
    {
        var lib = SymbolLibrary.Load(path);
        _symbolLibs[lib.LibraryNickname] = lib;
        if (isProject) _projectSymbolLibs.Add(lib.LibraryNickname);
    }

    private void LoadFootprintPath(string fpDir, bool isProject)
    {
        if (fpDir.EndsWith(".pretty", StringComparison.Ordinal)
            || (Directory.Exists(fpDir) && Directory.EnumerateFiles(fpDir, "*.kicad_mod").Any()))
        {
            var lib = FootprintLibrary.LoadDir(fpDir);
            _fpLibs[lib.LibraryNickname] = lib;
            if (isProject) _projectFootprintLibs.Add(lib.LibraryNickname);
            return;
        }
        if (Directory.Exists(fpDir))
        {
            foreach (var sub in Directory.EnumerateDirectories(fpDir, "*.pretty"))
            {
                var lib = FootprintLibrary.LoadDir(sub);
                _fpLibs[lib.LibraryNickname] = lib;
                if (isProject) _projectFootprintLibs.Add(lib.LibraryNickname);
            }
        }
    }

    public SymbolDef? ResolveSymbol(string libColonName)
    {
        if (!TrySplit(libColonName, out var lib, out var name)) return null;
        if (!_symbolLibs.TryGetValue(lib, out var sl))
        {
            sl = TryLoadStockSymbol(lib);
            if (sl is not null) _symbolLibs[lib] = sl;
        }
        return sl?.TryGet(name);
    }

    public FootprintDef? ResolveFootprint(string libColonName)
    {
        if (!TrySplit(libColonName, out var lib, out var name)) return null;
        if (!_fpLibs.TryGetValue(lib, out var fl))
        {
            fl = TryLoadStockFootprint(lib);
            if (fl is not null) _fpLibs[lib] = fl;
        }
        return fl?.TryGet(name);
    }

    private SymbolLibrary? TryLoadStockSymbol(string nickname)
    {
        if (_missingSymbolLibs.Contains(nickname)) return null;
        foreach (var root in StockSymbolRoots())
        {
            var dir = Path.Combine(root, nickname + ".kicad_symdir");
            if (Directory.Exists(dir)) return SymbolLibrary.Load(dir);
            var file = Path.Combine(root, nickname + ".kicad_sym");
            if (File.Exists(file)) return SymbolLibrary.Load(file);
        }
        _missingSymbolLibs.Add(nickname);
        return null;
    }

    private FootprintLibrary? TryLoadStockFootprint(string nickname)
    {
        if (_missingFootprintLibs.Contains(nickname)) return null;
        foreach (var root in StockFootprintRoots())
        {
            var dir = Path.Combine(root, nickname + ".pretty");
            if (Directory.Exists(dir)) return FootprintLibrary.LoadDir(dir);
        }
        _missingFootprintLibs.Add(nickname);
        return null;
    }

    private static IEnumerable<string> StockSymbolRoots() => StockRoots("symbols");
    private static IEnumerable<string> StockFootprintRoots() => StockRoots("footprints");

    private static IEnumerable<string> StockRoots(string subdir)
    {
        // 1. Shared per-user cache populated by `schgen install-stock-libs`.
        //    XDG_DATA_HOME first, then ~/.local/share/schgen/kicad-stock, then
        //    %LOCALAPPDATA%\schgen\kicad-stock on Windows.
        foreach (var sharedRoot in SharedCacheRoots())
        {
            var p = Path.Combine(sharedRoot, subdir);
            if (Directory.Exists(p)) yield return p;
        }

        // 2. $KICAD_DATA env var (KiCad's own override).
        var env = Environment.GetEnvironmentVariable("KICAD_DATA");
        if (!string.IsNullOrEmpty(env))
        {
            var p = Path.Combine(env, subdir);
            if (Directory.Exists(p)) yield return p;
        }

        // 3. Common install paths (KiCad installed system-wide).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            "/usr/share/kicad",
            "/usr/local/share/kicad",
            "/Applications/KiCad/KiCad.app/Contents/SharedSupport",
            @"C:\Program Files\KiCad\10.0\share\kicad",
            @"C:\Program Files\KiCad\9.0\share\kicad",
            Path.Combine(home, "bin/kicad/squashfs-root/usr/share/kicad"),
            Path.Combine(home, "Applications/kicad/squashfs-root/usr/share/kicad"),
            Path.Combine(home, ".local/share/kicad/squashfs-root/usr/share/kicad"),
        };
        foreach (var c in candidates)
        {
            var p = Path.Combine(c, subdir);
            if (Directory.Exists(p)) yield return p;
        }
    }

    private static IEnumerable<string> SharedCacheRoots()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
            yield return Path.Combine(xdg, "schgen", "kicad-stock");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".local", "share", "schgen", "kicad-stock");

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "schgen", "kicad-stock");
        }
    }

    public IEnumerable<string> SymbolLibraryNicknames => _symbolLibs.Keys;
    public IEnumerable<string> FootprintLibraryNicknames => _fpLibs.Keys;

    /// True if the library nickname came from a YAML `libraries:` entry or a
    /// CLI `--lib` path (so its symbols should be embedded in the .kicad_sch).
    public bool IsProjectSymbolLib(string nickname) => _projectSymbolLibs.Contains(nickname);

    private static bool TrySplit(string s, out string lib, out string name)
    {
        var i = s.IndexOf(':');
        if (i <= 0 || i == s.Length - 1) { lib = name = ""; return false; }
        lib = s[..i];
        name = s[(i + 1)..];
        return true;
    }
}
