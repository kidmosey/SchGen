using Schgen.Core.KiCad;

namespace Schgen.Core.Yaml;

public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Ok => Errors.Count == 0;
}

/// Cross-references the YAML document against resolved libraries and itself.
/// Catches: missing symbol/footprint, unknown pin, refdes collision,
/// power-net with no driver, ambiguous pin name without number, unknown template,
/// unknown port in params.
public static class Validator
{
    public static ValidationResult Validate(CircuitDocument doc, LibraryIndex libs)
    {
        var r = new ValidationResult();

        foreach (var sheet in doc.Sheets.Values)
        {
            // Refdes uniqueness is per-sheet AND per-unit. Multiple
            // ComponentDefs can share the same Ref if they're different units
            // of one multi-unit chip - each unit is a separate placement.
            var seenRefUnit = new HashSet<(string, int)>();
            foreach (var comp in sheet.Components)
            {
                if (string.IsNullOrEmpty(comp.Ref))
                {
                    r.Errors.Add($"sheet '{sheet.Name}': component has empty ref");
                    continue;
                }
                if (!seenRefUnit.Add((comp.Ref, comp.Unit)))
                    r.Errors.Add($"sheet '{sheet.Name}': refdes collision: '{comp.Ref}' unit {comp.Unit} is declared more than once");

                var sym = libs.ResolveSymbol(comp.Symbol);
                if (sym is null)
                {
                    r.Errors.Add($"{comp.Ref}: symbol '{comp.Symbol}' not found in any loaded library");
                    continue;
                }

                if (comp.Unit < 0 || !sym.PinsByUnit.ContainsKey(comp.Unit))
                {
                    r.Errors.Add($"{comp.Ref}: unit {comp.Unit} not present in symbol '{comp.Symbol}' (units available: {string.Join(",", sym.PinsByUnit.Keys.OrderBy(x => x))})");
                    continue;
                }

                // Pin verification: each pin_id in YAML must match a pin
                // belonging to this component's UNIT (plus unit-0 shared pins).
                var unitPins = sym.PinsOfUnit(comp.Unit);
                var nameLookup = unitPins
                    .GroupBy(p => p.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                var numberLookup = unitPins
                    .ToDictionary(p => p.Number, p => p, StringComparer.Ordinal);

                foreach (var (pinId, _) in comp.Pins)
                {
                    if (numberLookup.ContainsKey(pinId)) continue;
                    if (nameLookup.TryGetValue(pinId, out var matches))
                    {
                        if (matches.Count > 1)
                            r.Errors.Add(
                                $"{comp.Ref} unit {comp.Unit}: pin name '{pinId}' is ambiguous on symbol '{comp.Symbol}' " +
                                $"({matches.Count} pins share this name) - use pin numbers or bulk form");
                        continue;
                    }
                    r.Errors.Add($"{comp.Ref} unit {comp.Unit}: symbol '{comp.Symbol}' has no pin named or numbered '{pinId}'");
                }

                if (!string.IsNullOrEmpty(comp.Footprint))
                {
                    if (libs.ResolveFootprint(comp.Footprint) is null)
                        r.Errors.Add($"{comp.Ref}: footprint '{comp.Footprint}' not found in any loaded footprint library");
                }
                else
                {
                    r.Errors.Add($"{comp.Ref}: no footprint specified (PCB placement will be skipped for this part)");
                }
            }
        }

        // host: validation. For each component declaring host: <ref>.<pin>,
        // verify (a) the host string is well-formed, (b) the host refdes
        // exists on the same sheet, (c) the host pin is assigned to a net
        // (either explicitly or via auto-bind), and (d) the passive has a
        // pin whose first net matches the host pin's net. ApplyHostStubNets
        // will only patch entries that pass all four checks.
        foreach (var sheet in doc.Sheets.Values)
        {
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
                if (!LibraryIndex.TryParseHostRef(comp.Host, out var hRef, out var hPin))
                {
                    r.Errors.Add($"sheet '{sheet.Name}': {comp.Ref}.host = '{comp.Host}' is malformed; expected '<refdes>.<pin>'");
                    continue;
                }
                if (!bySheetRef.TryGetValue(hRef, out var hostUnits))
                {
                    r.Errors.Add($"sheet '{sheet.Name}': {comp.Ref}.host = '{comp.Host}' but no component '{hRef}' on this sheet");
                    continue;
                }
                List<string>? hostNets = null;
                foreach (var hu in hostUnits)
                {
                    if (hu.Pins.TryGetValue(hPin, out var hostPinNets) && hostPinNets.Count > 0)
                    {
                        hostNets = hostPinNets;
                        break;
                    }
                }
                if (hostNets is null)
                {
                    r.Errors.Add($"sheet '{sheet.Name}': {comp.Ref}.host = '{comp.Host}' but {hRef} has no net assigned on pin '{hPin}'");
                    continue;
                }
                bool matched = false;
                foreach (var hn in hostNets)
                {
                    if (LibraryIndex.TryFindAnchorPin(comp, hn, out _)) { matched = true; break; }
                }
                if (!matched)
                {
                    r.Errors.Add(
                        $"sheet '{sheet.Name}': {comp.Ref}.host = '{comp.Host}' but {comp.Ref} has no pin on any of " +
                        $"host pin {hPin}'s nets ({string.Join(", ", hostNets)}).");
                }
            }
        }

        // root.instantiate: each sheet name must exist; params keys must match ports.
        foreach (var inst in doc.Root.Instantiate)
        {
            if (!doc.Sheets.TryGetValue(inst.Sheet, out var sheetDef))
            {
                r.Errors.Add($"root: instantiates unknown sheet '{inst.Sheet}'");
                continue;
            }
            foreach (var (paramName, _) in inst.Params)
            {
                if (!sheetDef.Ports.ContainsKey(paramName))
                    r.Errors.Add($"root: instance '{inst.EffectiveName}' passes param '{paramName}' but sheet '{inst.Sheet}' has no such port");
            }
        }

        // Detect template-vs-singleton consistency: a template MUST have ports;
        // a sheet instantiated >1x without `template: true` is an error.
        var instCounts = doc.Root.Instantiate
            .GroupBy(i => i.Sheet)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (name, sd) in doc.Sheets)
        {
            if (sd.Template && sd.Ports.Count == 0)
                r.Warnings.Add($"sheet '{name}' is template:true but declares no ports - cross-sheet connectivity will be impossible");
            if (!sd.Template && instCounts.GetValueOrDefault(name, 0) > 1)
                r.Errors.Add($"sheet '{name}' is instantiated {instCounts[name]} times but is not marked template:true");
        }

        // Power-net driver detection: every power_net must either have a
        // power_out / output pin endpoint somewhere, or schgen emits PWR_FLAG.
        // That's a hint, not a failure. Stash a list of nets that will need flagging.
        // (Emitter does the actual stamping; here we just warn if a power_net is
        // declared but never appears on any pin.)
        var nets = CollectNetsToPins(doc, libs);
        foreach (var pn in doc.PowerNets)
        {
            if (!nets.ContainsKey(pn))
                r.Warnings.Add($"power_net '{pn}' is declared but never appears as a pin attribution");
        }

        // Strict power-net attribution audit. For every YAML pin attribution
        // that targets a declared power_net, check whether the symbol's pin
        // name already resolves to that net (literal match or via power_net_
        // aliases). Redundant attributions are an error - they should be
        // deleted and auto-bind left to do the work. Conflicting attributions
        // (pin name suggests a DIFFERENT power net than the YAML asserts) are
        // also an error - one of them is wrong and the user must reconcile.
        ValidatePowerAttributions(doc, libs, r);

        return r;
    }

    /// Build the same alias-resolution lookup that ApplyPowerNetPinAutoBind
    /// uses, then walk every component's pin attributions. Flags two error
    /// patterns:
    ///   1. Pin name resolves to the SAME power net the YAML attributes it to
    ///      -> redundant; auto-bind would produce the same wiring.
    ///   2. Pin name resolves to a DIFFERENT power net than the YAML asserts
    ///      -> conflict; either the symbol's pin name is wrong, the YAML is
    ///         wrong, or the alias table is wrong.
    /// Non-power-net assignments and pins whose names don't resolve to any
    /// power net are left alone.
    private static void ValidatePowerAttributions(
        CircuitDocument doc, LibraryIndex libs, ValidationResult r)
    {
        if (doc.PowerNets.Count == 0 && doc.PowerNetAliases.Count == 0) return;
        var powerSet = new HashSet<string>(doc.PowerNets, StringComparer.Ordinal);

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
            if (string.IsNullOrEmpty(pinName)) return null;
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

        foreach (var sheet in doc.Sheets.Values)
        {
            foreach (var comp in sheet.Components)
            {
                var sym = libs.ResolveSymbol(comp.Symbol);
                if (sym is null) continue;
                var unitPins = sym.PinsOfUnit(comp.Unit);
                foreach (var (pinId, netNames) in comp.Pins)
                {
                    // Skip entries inserted by auto-bind itself - we only audit
                    // user-authored YAML attributions.
                    if (comp.AutoBoundPinIds.Contains(pinId)) continue;

                    var pin = unitPins.FirstOrDefault(p => p.Number == pinId)
                              ?? unitPins.FirstOrDefault(p => p.Name == pinId);
                    if (pin is null) continue;
                    var resolvedFromName = ResolveBoundNet(pin.Name);
                    if (resolvedFromName is null) continue;   // pin name says nothing about power -> YAML may need to attribute

                    foreach (var netName in netNames)
                    {
                        if (netName == "NC") continue;
                        if (!powerSet.Contains(netName)) continue;    // YAML attribution is non-power -> not our concern

                        if (string.Equals(netName, resolvedFromName, StringComparison.Ordinal))
                        {
                            r.Errors.Add(
                                $"{comp.Ref} unit {comp.Unit} pin {pin.Number} ({pin.Name}): " +
                                $"redundant explicit attribution to power_net '{netName}' - " +
                                $"pin name '{pin.Name}' already auto-binds to '{resolvedFromName}'. " +
                                $"Remove this assignment.");
                        }
                        else
                        {
                            r.Errors.Add(
                                $"{comp.Ref} unit {comp.Unit} pin {pin.Number} ({pin.Name}): " +
                                $"YAML attributes pin to '{netName}' but pin name '{pin.Name}' " +
                                $"auto-binds to '{resolvedFromName}'. Reconcile the YAML, the symbol's " +
                                $"pin name, or the power_net_aliases entry.");
                        }
                    }
                }
            }
        }
    }

    /// Build net -> list of (component, pin) using resolved symbols so that pin
    /// names like "VIN" are mapped to canonical pins. Used by downstream stages
    /// too - exposed as a public helper.
    public static Dictionary<string, List<NetEndpoint>> CollectNetsToPins(
        CircuitDocument doc, LibraryIndex libs)
    {
        var nets = new Dictionary<string, List<NetEndpoint>>(StringComparer.Ordinal);
        foreach (var sheet in doc.Sheets.Values)
        {
            foreach (var comp in sheet.Components)
            {
                var sym = libs.ResolveSymbol(comp.Symbol);
                if (sym is null) continue;
                var unitPins = sym.PinsOfUnit(comp.Unit);
                foreach (var (pinId, netNames) in comp.Pins)
                {
                    var pin = unitPins.FirstOrDefault(p => p.Number == pinId)
                              ?? unitPins.FirstOrDefault(p => p.Name == pinId);
                    if (pin is null) continue;
                    foreach (var netName in netNames)
                    {
                        if (netName == "NC") continue;
                        if (!nets.TryGetValue(netName, out var list))
                        {
                            list = new List<NetEndpoint>();
                            nets[netName] = list;
                        }
                        list.Add(new NetEndpoint(sheet.Name, comp.Ref, comp.Unit, pin));
                    }
                }
            }
        }
        return nets;
    }
}

public readonly record struct NetEndpoint(string Sheet, string ComponentRef, int Unit, PinDef Pin);
