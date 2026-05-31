using Schgen.Cli.Commands;

namespace Schgen.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "build"    => BuildCommand.Run(args[1..]),
                "validate" => ValidateCommand.Run(args[1..]),
                "symbols"  => SymbolsCommand.Run(args[1..]),
                "-h" or "--help" or "help" => PrintUsage(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"error: unknown command '{cmd}'");
        PrintUsage();
        return 1;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            schgen - KiCad schematic + initial PCB generator

            usage:
              schgen build    <circuit.yaml> --out <dir>
              schgen validate <circuit.yaml>
              schgen symbols  <lib.kicad_sym>
            """);
        return 0;
    }
}
