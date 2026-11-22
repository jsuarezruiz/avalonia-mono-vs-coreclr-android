using System.Text.RegularExpressions;

namespace Bench;

/// <summary>Turns what the user typed (an APK path, or this repo's track/variant) into a <see cref="Candidate"/>.</summary>
public static partial class CandidateResolver
{
    /// <param name="arg">An .apk path, or <c>track/variant</c> such as net10/mono-aot-profiled (built by bench build).</param>
    /// <param name="labelOption">Optional "Name" or "Name|Sub" for the video.</param>
    public static Candidate Resolve(string arg, string? labelOption, string scenario = "dashboard")
    {
        string apk;
        string entry;
        string name;
        string sub = "";
        var repoScenario = "app";

        if (File.Exists(arg) && arg.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            apk = Path.GetFullPath(arg);
            entry = Path.GetFileNameWithoutExtension(apk).Replace("-Signed", "");
            name = entry;
        }
        else if (RepoEntry().IsMatch(arg) && FindRepoRoot() is { } root)
        {
            var dir = Path.Combine(root, "artifacts", arg.Split('/')[0], scenario, arg.Split('/')[1]);
            apk = Directory.Exists(dir) ? Directory.GetFiles(dir, "*-Signed.apk").FirstOrDefault() ?? "" : "";
            if (apk.Length == 0)
                throw new BenchException($"No built APK for '{arg}' ({scenario}). Build it first: bench build --scenario {scenario} {arg.Split('/')[1]}");
            entry = arg;
            repoScenario = scenario;
            (name, sub) = RepoLabels.For(arg);
        }
        else
        {
            throw new BenchException($"'{arg}' is not an .apk file" + (FindRepoRoot() is null ? "." : " or a built entry such as net10/mono-aot-profiled."));
        }

        if (labelOption is { Length: > 0 })
        {
            var parts = labelOption.Split('|', 2);
            name = parts[0].Trim();
            sub = parts.Length > 1 ? parts[1].Trim() : "";
        }

        return new Candidate
        {
            Label = name, Sub = sub, Entry = entry, Scenario = repoScenario, ApkPath = apk, PackageId = ApkManifest.ReadPackageName(apk),
            ApkBytes = new FileInfo(apk).Length,
        };
    }

    /// <summary>
    /// Every built entry of the scenario (net10/coreclr, net10/mono-jit, ...), like running the old scripts with no
    /// arguments. Their labels are the entries themselves, which is what the tables show.
    /// </summary>
    public static IReadOnlyList<Candidate> ResolveBuilt(string scenario)
    {
        var root = FindRepoRoot() ?? throw new BenchException("Run this from inside the repository, or name the APKs explicitly.");
        var entries = RepoLayout.BuiltEntries(root, scenario);
        if (entries.Count == 0)
            throw new BenchException($"Nothing is built for '{scenario}'. Build first: bench build --scenario {scenario}");
        return entries.Select(e =>
        {
            var c = Resolve(e, null, scenario);
            return new Candidate { Label = e, Entry = e, Scenario = scenario, ApkPath = c.ApkPath, PackageId = c.PackageId, ApkBytes = c.ApkBytes };
        }).ToList();
    }

    /// <summary>Package ids must be distinct, and so must the names the results carry.</summary>
    public static void EnsureAllDistinct(IReadOnlyList<Candidate> candidates)
    {
        foreach (var dup in candidates.GroupBy(c => c.PackageId).Where(g => g.Count() > 1))
            throw new BenchException($"Two of the apps have the package id '{dup.Key}', so they cannot be installed side by side.");
        foreach (var dup in candidates.GroupBy(c => c.Entry).Where(g => g.Count() > 1))
            throw new BenchException($"Two of the apps would carry the name '{dup.Key}' in the results; give them different labels.");
    }

    /// <summary>Two apps can only be installed side by side if their package ids differ.</summary>
    public static void EnsureDistinct(Candidate left, Candidate right)
    {
        if (left.PackageId == right.PackageId)
            throw new BenchException($"Both APKs have the package id '{left.PackageId}', so they cannot be installed side by side. " +
                                     "Build one of them with a different application id.");
        if (left.Entry == right.Entry)
            throw new BenchException("Both apps would carry the same name in the results; give them different labels (--left-label / --right-label).");
    }

    /// <summary>The repository root, found by walking up to the folder that holds the solution file.</summary>
    public static string? FindRepoRoot(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AvaloniaMonoVsCoreClr.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    [GeneratedRegex(@"^net\d+/[A-Za-z0-9-]+$")]
    private static partial Regex RepoEntry();
}
