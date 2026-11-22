using System.Globalization;
using Bench;

namespace Bench.Cli;

/// <summary>Minimal option parsing: positional arguments, --name value options and --flags.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags = ["--no-video", "--drop-caches", "--include-warm", "--strict", "--show-home", "--help"];

    private static readonly HashSet<string> Options =
    [
        "--runs", "--warmup", "--takes", "--cooldown", "--serial", "--adb", "--out", "--left-label", "--right-label",
        "--eyebrow", "--note", "--device-label", "--scenario", "--stats", "--baseline", "--track", "--rid", "--seconds",
    ];

    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string> _values = [];
    private readonly HashSet<string> _flags = [];

    public static Args Parse(IEnumerable<string> argv)
    {
        var args = new Args();
        using var e = argv.GetEnumerator();
        while (e.MoveNext())
        {
            var a = e.Current;
            if (!a.StartsWith("--", StringComparison.Ordinal)) { args.Positional.Add(a); continue; }
            if (Flags.Contains(a)) { args._flags.Add(a); continue; }
            if (!Options.Contains(a)) throw new BenchException($"Unknown option '{a}'. Run `bench --help`.");
            if (!e.MoveNext()) throw new BenchException($"Option '{a}' needs a value.");
            args._values[a] = e.Current;
        }

        return args;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public bool Flag(string name) => _flags.Contains(name);

    public int Int(string name, int fallback)
    {
        if (Get(name) is not { } text) return fallback;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : throw new BenchException($"{name} must be a whole number, got '{text}'.");
    }

    public double Seconds(string name, double fallback)
    {
        if (Get(name) is not { } text) return fallback;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : throw new BenchException($"{name} must be a number of seconds, got '{text}'.");
    }
}
