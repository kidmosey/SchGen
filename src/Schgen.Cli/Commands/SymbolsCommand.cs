using Schgen.Core.KiCad;

namespace Schgen.Cli.Commands;

public static class SymbolsCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: schgen symbols <lib.kicad_sym>");
            return 2;
        }
        var lib = SymbolLibrary.Load(args[0]);
        Console.Out.WriteLine($"library: {lib.LibraryNickname}  ({lib.Symbols.Count} symbols)");
        foreach (var name in lib.Symbols.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            var s = lib.Symbols[name];
            Console.Out.WriteLine();
            Console.Out.WriteLine($"  {lib.LibraryNickname}:{name}    " +
                                  $"prefix={s.ReferencePrefix ?? "?"}, " +
                                  $"pins={s.Pins.Count}, " +
                                  $"units={s.UnitCount}, " +
                                  $"bbox=({s.BoundingBox.MinX:F2},{s.BoundingBox.MinY:F2})->({s.BoundingBox.MaxX:F2},{s.BoundingBox.MaxY:F2})");
            foreach (var unit in s.PinsByUnit.Keys.OrderBy(u => u))
            {
                // Show every pin visible on this unit, including pins that were
                // declared under unit 0 (shared) and pulled in by PinsOfUnit.
                var unitPins = s.PinsByUnit[unit].ToList();
                if (unitPins.Count == 0) continue;
                Console.Out.WriteLine($"    unit {unit}: {unitPins.Count} pins");
                foreach (var p in unitPins.OrderBy(p => p.Number, StringComparer.Ordinal))
                {
                    Console.Out.WriteLine($"      pin {p.Number,-5} {p.Name,-50} {p.Type,-14} at ({p.X:F2},{p.Y:F2}) rot {p.Rotation}");
                }
            }
        }
        return 0;
    }
}
