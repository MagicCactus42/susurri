using System.Diagnostics;
using System.Security.Cryptography;
using SharpFuzz;

namespace Susurri.Tests.Fuzz;

/// <summary>
/// Two modes:
///   * AFL persistent (default when run via afl-fuzz / libFuzzer):
///       susurri-fuzz &lt;target&gt;
///     SharpFuzz.Fuzzer.Run reads bytes from stdin, calls the target, repeats.
///     afl-fuzz drives the loop and detects crashes / hangs / new coverage.
///   * Standalone smoke (no AFL needed, runs locally):
///       susurri-fuzz &lt;target&gt; --smoke [--seconds N] [--iterations N]
///     Generates random byte arrays, calls the target, fails if any
///     unexpected exception type escapes. Useful for quick regression checks
///     and as a CI step before AFL infrastructure is in place.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return PrintUsage();
        }

        var name = args[0];
        if (name is "--help" or "-h" or "help")
        {
            return PrintUsage();
        }

        if (name == "list")
        {
            foreach (var key in FuzzTargets.All.Keys)
                Console.WriteLine(key);
            return 0;
        }

        if (!FuzzTargets.All.TryGetValue(name, out var target))
        {
            Console.Error.WriteLine($"Unknown target '{name}'. Run `susurri-fuzz list` to see valid targets.");
            return 2;
        }

        var smoke = args.Contains("--smoke");
        if (smoke)
        {
            return RunSmoke(name, target, args);
        }

        // AFL persistent loop. SharpFuzz's Action<Stream> overload reads
        // stdin per iteration; afl-fuzz drives the loop and detects crashes /
        // hangs / new coverage. Outside afl-fuzz this reads stdin once,
        // processes, and exits — works for the libFuzzer-style entry point too.
        Fuzzer.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var buf = ms.ToArray();
            try { target(buf); }
            catch (Exception ex) when (FuzzTargets.IsGracefulRejection(ex))
            {
                // Expected: parser refused malformed input cleanly.
            }
            // Any other exception escapes — AFL records it as a crash.
        });

        return 0;
    }

    private static int RunSmoke(string name, Action<byte[]> target, string[] args)
    {
        var seconds = ParseIntFlag(args, "--seconds", defaultValue: 0);
        var iterations = ParseIntFlag(args, "--iterations",
            defaultValue: seconds > 0 ? int.MaxValue : 10_000);
        var crashDir = ParseStringFlag(args, "--crashes", "crashes");
        var seed = ParseIntFlag(args, "--seed", Random.Shared.Next());

        var rng = new Random(seed);
        var deadline = seconds > 0
            ? Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency)
            : long.MaxValue;

        Console.WriteLine($"smoke target={name} iterations={iterations} seconds={seconds} seed={seed}");

        var ran = 0;
        var failures = 0;
        while (ran < iterations && Stopwatch.GetTimestamp() < deadline)
        {
            var len = rng.Next(0, 8192);
            var bytes = new byte[len];
            rng.NextBytes(bytes);

            try
            {
                target(bytes);
            }
            catch (Exception ex) when (FuzzTargets.IsGracefulRejection(ex))
            {
                // Expected.
            }
            catch (Exception ex)
            {
                failures++;
                var savedAt = SaveCrash(crashDir, name, bytes, ex);
                Console.Error.WriteLine(
                    $"[crash {failures}] {ex.GetType().Name}: {ex.Message} (saved {savedAt})");
                if (failures >= 5)
                {
                    Console.Error.WriteLine("Aborting after 5 crashes; rerun with --seed to reproduce.");
                    return 1;
                }
            }

            ran++;
        }

        Console.WriteLine($"smoke complete target={name} ran={ran} crashes={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static string SaveCrash(string crashDir, string targetName, byte[] bytes, Exception ex)
    {
        Directory.CreateDirectory(crashDir);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        var path = Path.Combine(crashDir, $"{targetName}-{hash}.bin");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".txt", $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}");
        return path;
    }

    private static int ParseIntFlag(string[] args, string flag, int defaultValue)
    {
        var i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length) return defaultValue;
        return int.TryParse(args[i + 1], out var v) ? v : defaultValue;
    }

    private static string ParseStringFlag(string[] args, string flag, string defaultValue)
    {
        var i = Array.IndexOf(args, flag);
        return (i < 0 || i + 1 >= args.Length) ? defaultValue : args[i + 1];
    }

    private static int PrintUsage()
    {
        Console.WriteLine(@"susurri-fuzz: parser fuzzing harness

Usage:
  susurri-fuzz list
  susurri-fuzz <target>                                # AFL persistent mode (reads stdin)
  susurri-fuzz <target> --smoke [--seconds N] [--iterations N] [--seed N] [--crashes DIR]

Examples:
  # List available targets
  susurri-fuzz list

  # Quick local smoke (10 000 random inputs, default)
  susurri-fuzz kademlia --smoke

  # Time-bounded smoke
  susurri-fuzz onion-layer --smoke --seconds 30 --seed 42

  # Real fuzzing with AFL (requires afl-fuzz installed and assembly instrumented)
  afl-fuzz -i corpus/ -o findings/ -- ./susurri-fuzz kademlia
");
        return 0;
    }
}
