using System.Diagnostics;

namespace Schgen.Cli.Commands;

/// <summary>
/// One-time per-machine setup that populates the shared KiCad stock-lib cache
/// at ~/.local/share/schgen/kicad-stock/{symbols,footprints} so every project
/// on the machine can reference Device:R, Connector:USB_C, etc. without
/// needing its own libs/ copy.
///
/// Looks for an already-extracted KiCad install first, falls back to
/// extracting an AppImage if found.
/// </summary>
public static class InstallStockLibsCommand
{
    public static int Run(string[] args)
    {
        string? outDir = null;
        bool force = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                case "-o":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("error: --out needs a path"); return 2; }
                    outDir = args[++i];
                    break;
                case "--force":
                case "-f":
                    force = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"error: unexpected argument '{args[i]}'");
                    PrintHelp();
                    return 2;
            }
        }

        outDir ??= DefaultStockLibsPath();
        var symsDst = Path.Combine(outDir, "symbols");
        var fpsDst  = Path.Combine(outDir, "footprints");

        if (!force && IsPopulated(symsDst) && IsPopulated(fpsDst))
        {
            Console.WriteLine($"schgen: stock libs already present at {outDir} (use --force to re-extract)");
            return 0;
        }

        Directory.CreateDirectory(outDir);

        // Try already-extracted KiCad installs first.
        foreach (var src in AlreadyExtractedCandidates())
        {
            if (Directory.Exists(Path.Combine(src, "symbols")) && Directory.Exists(Path.Combine(src, "footprints")))
            {
                Console.WriteLine($"schgen: copying from {src}");
                CopyDir(Path.Combine(src, "symbols"), symsDst);
                CopyDir(Path.Combine(src, "footprints"), fpsDst);
                Console.WriteLine($"schgen: stock libs populated at {outDir}");
                return 0;
            }
        }

        // Fall back to extracting an AppImage.
        var appimage = FindAppImage();
        if (appimage is null)
        {
            Console.Error.WriteLine("""
                schgen: could not find a KiCad install.

                Tried:
                  - /usr/share/kicad/
                  - /usr/local/share/kicad/
                  - ~/bin/kicad/squashfs-root/usr/share/kicad/
                  - ~/Applications/kicad/squashfs-root/usr/share/kicad/
                  - /Applications/KiCad/KiCad.app/Contents/SharedSupport/
                  - C:\Program Files\KiCad\10.0\share\kicad\
                  - ~/bin/kicad/*.AppImage, ~/Applications/*.AppImage, ~/Downloads/*.AppImage

                Install KiCad (https://www.kicad.org/download/) and re-run.
                """);
            return 1;
        }

        Console.WriteLine($"schgen: extracting {appimage} (one-time, ~30s)");
        var tmp = Directory.CreateTempSubdirectory("schgen-kicad-").FullName;
        try
        {
            var psi = new ProcessStartInfo(appimage, "--appimage-extract")
            {
                WorkingDirectory = tmp,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"schgen: AppImage extraction failed: {proc.StandardError.ReadToEnd()}");
                return 1;
            }

            var extracted = Path.Combine(tmp, "squashfs-root", "usr", "share", "kicad");
            if (!Directory.Exists(Path.Combine(extracted, "symbols")) || !Directory.Exists(Path.Combine(extracted, "footprints")))
            {
                Console.Error.WriteLine($"schgen: extraction produced unexpected layout at {extracted}");
                return 1;
            }

            CopyDir(Path.Combine(extracted, "symbols"), symsDst);
            CopyDir(Path.Combine(extracted, "footprints"), fpsDst);
            Console.WriteLine($"schgen: stock libs populated at {outDir}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    public static string DefaultStockLibsPath()
    {
        // XDG_DATA_HOME first, then ~/.local/share. Windows: %LOCALAPPDATA%.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = !string.IsNullOrEmpty(xdg)
            ? xdg
            : OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "schgen", "kicad-stock");
    }

    private static bool IsPopulated(string dir)
        => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    private static IEnumerable<string> AlreadyExtractedCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/usr/share/kicad";
        yield return "/usr/local/share/kicad";
        yield return Path.Combine(home, "bin/kicad/squashfs-root/usr/share/kicad");
        yield return Path.Combine(home, "Applications/kicad/squashfs-root/usr/share/kicad");
        yield return Path.Combine(home, ".local/share/kicad/squashfs-root/usr/share/kicad");
        yield return "/Applications/KiCad/KiCad.app/Contents/SharedSupport";
        yield return @"C:\Program Files\KiCad\10.0\share\kicad";
        yield return @"C:\Program Files\KiCad\9.0\share\kicad";
    }

    private static string? FindAppImage()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, "bin/kicad"), Path.Combine(home, "Applications"), Path.Combine(home, "Downloads") })
        {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir, "*.AppImage", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).Contains("kicad", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }

    private static void CopyDir(string src, string dst)
    {
        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(dst);
        foreach (var entry in Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, entry);
            var target = Path.Combine(dst, rel);
            if (Directory.Exists(entry))
                Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, overwrite: true);
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            usage: schgen install-stock-libs [--out <path>] [--force]

            Populates the shared KiCad stock-lib cache so every project on this
            machine can reference Device:R, Connector:USB_C, etc. without needing
            its own libs/ copy.

            Default destination: {DefaultStockLibsPath()}
            Override with --out or with $XDG_DATA_HOME (POSIX).

            Idempotent: skips re-extraction if the cache is already populated.
            Use --force to re-extract anyway.
            """);
    }
}
