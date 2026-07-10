using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Tools.Ts2Resx;

namespace ComfyUI.Manager.Tools.Ts2Resx;

public static class Program
{
    public static int Main(string[] args)
    {
        // 手写解析:--ts-dir <path> --out <path>
        //                  [--default-locale <locale>]
        //                  [--target-locales <a,b,c>]
        //                  [--fail-on-missing]
        string? tsDir = null, outDir = null;
        string defaultLocale = "en_US";
        List<string> targets = new();
        bool failOnMissing = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ts-dir": tsDir = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--default-locale": defaultLocale = args[++i]; break;
                case "--target-locales":
                    targets.AddRange(args[++i].Split(','));
                    break;
                case "--fail-on-missing": failOnMissing = true; break;
                default:
                    Console.Error.WriteLine($"Unknown arg: {args[i]}");
                    return 2;
            }
        }
        if (tsDir is null || outDir is null || targets.Count == 0)
        {
            Console.Error.WriteLine(
                "Usage: --ts-dir <path> --out <path> "
                + "--target-locales <a,b> [--default-locale <locale>] "
                + "[--fail-on-missing]");
            return 2;
        }
        Directory.CreateDirectory(outDir);
        // 解析所有 .ts
        var parsed = new Dictionary<string, TsFile>();
        foreach (var file in Directory.GetFiles(tsDir, "*.ts"))
        {
            var ts = TsParser.Parse(file);
            parsed[ts.Locale] = ts;
        }
        // 默认 locale(无 culture 后缀)
        if (parsed.TryGetValue(defaultLocale, out var defaultTs))
        {
            var entries = defaultTs.Messages
                .Where(m => !m.IsVanished)
                .Select(m => (Key: KeyDeriver.Derive(m.Context, m.Source,
                    m.ExtraContext, m.IsPlural), Value: m.Source));
            var path = Path.Combine(outDir, "Strings.resx");
            ResxEmitter.Emit(path, null, entries);
            Console.WriteLine($"wrote {path} ({entries.Count()} entries)");
        }
        else
        {
            Console.Error.WriteLine($"default locale {defaultLocale} not found");
            return 1;
        }
        // 其它 locale(有 culture 后缀)
        foreach (var loc in targets)
        {
            if (loc == defaultLocale) continue;
            if (!parsed.TryGetValue(loc, out var ts))
            {
                Console.Error.WriteLine($"locale {loc} not found");
                if (failOnMissing) return 1;
                continue;
            }
            var cultureName = LocaleToCulture(loc);  // en_US -> en-US
            var entries = ts.Messages
                .Where(m => !m.IsVanished && !string.IsNullOrEmpty(m.Translation))
                .Select(m => (Key: KeyDeriver.Derive(m.Context, m.Source,
                    m.ExtraContext, m.IsPlural), Value: m.Translation));
            var path = Path.Combine(outDir, $"Strings.{cultureName}.resx");
            ResxEmitter.Emit(path, cultureName, entries);
            Console.WriteLine($"wrote {path} ({entries.Count()} entries)");
        }
        return 0;
    }

    private static string LocaleToCulture(string locale) =>
        locale.Replace("_", "-");
}