using Schgen.Core.Yaml;

namespace Schgen.Cli.Commands;

public static class ValidateCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: schgen validate <circuit.yaml>");
            return 2;
        }
        var doc = YamlLoader.Load(args[0]);
        var libs = LibraryIndex.FromDocument(doc);
        var report = Validator.Validate(doc, libs);
        foreach (var w in report.Warnings) Console.Out.WriteLine($"warn:  {w}");
        foreach (var e in report.Errors)   Console.Out.WriteLine($"error: {e}");
        if (!report.Ok)
        {
            Console.Out.WriteLine($"validation FAILED ({report.Errors.Count} error(s), {report.Warnings.Count} warning(s))");
            return 1;
        }
        Console.Out.WriteLine($"validation ok ({report.Warnings.Count} warning(s))");
        return 0;
    }
}
