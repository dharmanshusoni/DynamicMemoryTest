// DynamicMemoryTest
// Generates a large synthetic C# project, builds it, and measures CPU/memory
// usage of both the generator and the child `dotnet build` process.
// Usage: dotnet run -- [namespaces] [classesPerNamespace] [fieldsPerClass]

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // Hoisted out of the per-field hot loop.
    static readonly string Filler = new string('X', 50);

    static int Main(string[] args)
    {
        if (!TryParseArgs(args, out int namespaces, out int classesPerNamespace, out int fieldsPerClass, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: DynamicMemoryTest [namespaces=5000] [classesPerNamespace=10] [fieldsPerClass=50]");
            return 2;
        }

        PrintSystemInfo();
        Console.WriteLine($"Config: {namespaces} namespaces × {classesPerNamespace} classes × {fieldsPerClass} fields");

        string workDir = Path.Combine(Path.GetTempPath(), "cs_compile_stress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var newStats = RunSampled("dotnet", "new classlib -n StressLib", workDir);
        if (newStats.ExitCode != 0)
        {
            Console.Error.WriteLine($"`dotnet new classlib` failed with exit code {newStats.ExitCode}.");
            TryDeleteDirectoryWithRetries(workDir, maxRetries: 5, delayMs: 500);
            return newStats.ExitCode;
        }

        string projDir = Path.Combine(workDir, "StressLib");
        string defaultFile = Path.Combine(projDir, "Class1.cs");
        if (File.Exists(defaultFile)) File.Delete(defaultFile);

        long totalClasses = (long)namespaces * classesPerNamespace;
        Console.WriteLine($"→ Creating {totalClasses:N0} .cs files in {projDir}");

        SelfStats genStats = MeasureSelf(() =>
            GenerateSources(projDir, namespaces, classesPerNamespace, fieldsPerClass));
        Console.WriteLine($"Generation done in {genStats.ElapsedSeconds:F2}s.");

        Console.WriteLine("Building project...");
        int exitCode = 1;
        ChildStats buildStats = default;
        try
        {
            buildStats = RunSampled("dotnet", "build -c Release --nologo -v:q", projDir);
            exitCode = buildStats.ExitCode;
            Console.WriteLine($"Build exit code: {exitCode}, elapsed {buildStats.ElapsedSeconds:F2}s.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Build failed with exception: {ex}");
        }
        finally
        {
            TryDeleteDirectoryWithRetries(workDir, maxRetries: 5, delayMs: 500);
        }

        PrintReport(namespaces, classesPerNamespace, fieldsPerClass, totalClasses, genStats, buildStats, exitCode);
        return exitCode;
    }

    static bool TryParseArgs(string[] args, out int ns, out int cpn, out int fpc, out string? error)
    {
        ns = 5000; cpn = 10; fpc = 50; error = null;
        if (args.Length > 0 && !int.TryParse(args[0], out ns)) { error = $"Invalid namespaces: '{args[0]}'"; return false; }
        if (args.Length > 1 && !int.TryParse(args[1], out cpn)) { error = $"Invalid classesPerNamespace: '{args[1]}'"; return false; }
        if (args.Length > 2 && !int.TryParse(args[2], out fpc)) { error = $"Invalid fieldsPerClass: '{args[2]}'"; return false; }
        if (ns <= 0 || cpn <= 0 || fpc <= 0) { error = "All counts must be > 0"; return false; }
        return true;
    }

    static void PrintSystemInfo()
    {
        var mem = GC.GetGCMemoryInfo();
        Console.WriteLine("=== System ===");
        Console.WriteLine($"OS:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"Runtime:   .NET {Environment.Version}");
        Console.WriteLine($"Cores:     {Environment.ProcessorCount}");
        Console.WriteLine($"Total RAM: {BytesToMB(mem.TotalAvailableMemoryBytes):F2} MB");
        Console.WriteLine();
    }

    static void GenerateSources(string srcDir, int namespaces, int classesPerNamespace, int fieldsPerClass)
    {
        int done = 0;
        int progressEvery = Math.Max(1, namespaces / 10);

        Parallel.For(
            1, namespaces + 1,
            () => new StringBuilder(8192),
            (ns, _, sb) =>
            {
                string nsName = $"StressNs{ns}";
                for (int c = 1; c <= classesPerNamespace; c++)
                {
                    string className = $"C_{ns}_{c}";
                    sb.Clear();
                    sb.AppendLine("using System;");
                    sb.Append("namespace ").AppendLine(nsName);
                    sb.AppendLine("{");
                    sb.Append("  public class ").AppendLine(className);
                    sb.AppendLine("  {");
                    for (int f = 0; f < fieldsPerClass; f++)
                    {
                        sb.Append("    public string F").Append(f).Append(" = \"").Append(Filler).AppendLine("\";");
                    }
                    sb.AppendLine();
                    sb.AppendLine("    public void DoNothing()");
                    sb.AppendLine("    {");
                    for (int i = 0; i < 30; i++)
                    {
                        sb.Append("      int val").Append(i).Append(" = F").Append(i % fieldsPerClass).AppendLine(".Length;");
                    }
                    sb.AppendLine("    }");
                    sb.AppendLine("  }");
                    sb.AppendLine("}");
                    File.WriteAllText(Path.Combine(srcDir, $"{className}.cs"), sb.ToString());
                }
                int d = Interlocked.Increment(ref done);
                if (d % progressEvery == 0)
                    Console.WriteLine($"  Created namespaces: {d}/{namespaces}");
                return sb;
            },
            _ => { });
    }

    readonly record struct SelfStats(
        double ElapsedSeconds,
        long PeakWorkingSetBytes,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);

    static SelfStats MeasureSelf(Action body, int sampleIntervalMs = 200)
    {
        var p = Process.GetCurrentProcess();
        long peak = 0;
        using var timer = new Timer(_ =>
        {
            try
            {
                p.Refresh();
                long ws = p.WorkingSet64;
                long prev = Interlocked.Read(ref peak);
                while (ws > prev && Interlocked.CompareExchange(ref peak, ws, prev) != prev)
                    prev = Interlocked.Read(ref peak);
            }
            catch { /* sampler is best-effort */ }
        }, null, 0, sampleIntervalMs);

        long startBytes = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();

        body();

        sw.Stop();
        long endBytes = GC.GetTotalAllocatedBytes(true);

        return new SelfStats(
            sw.Elapsed.TotalSeconds,
            peak,
            endBytes - startBytes,
            GC.CollectionCount(0) - g0,
            GC.CollectionCount(1) - g1,
            GC.CollectionCount(2) - g2);
    }

    readonly record struct ChildStats(
        int ExitCode,
        double ElapsedSeconds,
        long PeakWorkingSetBytes,
        double TotalProcessorTimeSeconds);

    static ChildStats RunSampled(string file, string args, string workDir, int sampleIntervalMs = 200)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {file} {args}");
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        long peak = 0;
        var sw = Stopwatch.StartNew();
        using var timer = new Timer(_ =>
        {
            try
            {
                p.Refresh();
                if (p.HasExited) return;
                long ws = p.WorkingSet64;
                long prev = Interlocked.Read(ref peak);
                while (ws > prev && Interlocked.CompareExchange(ref peak, ws, prev) != prev)
                    prev = Interlocked.Read(ref peak);
            }
            catch { /* process may have exited between checks */ }
        }, null, 0, sampleIntervalMs);

        p.WaitForExit();
        sw.Stop();

        double cpuSec = 0;
        try { cpuSec = p.TotalProcessorTime.TotalSeconds; } catch { }

        return new ChildStats(p.ExitCode, sw.Elapsed.TotalSeconds, peak, cpuSec);
    }

    static void PrintReport(
        int namespaces, int classesPerNamespace, int fieldsPerClass, long totalClasses,
        SelfStats gen, ChildStats build, int exitCode)
    {
        var mem = GC.GetGCMemoryInfo();
        double cores = Environment.ProcessorCount;
        double buildCpuPct = build.ElapsedSeconds > 0
            ? 100.0 * build.TotalProcessorTimeSeconds / build.ElapsedSeconds / cores
            : 0.0;

        var report = new
        {
            system = new
            {
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture.ToString(),
                runtime = Environment.Version.ToString(),
                processorCount = Environment.ProcessorCount,
                totalAvailableMemoryMB = Math.Round(BytesToMB(mem.TotalAvailableMemoryBytes), 2)
            },
            config = new { namespaces, classesPerNamespace, fieldsPerClass, totalClasses },
            generate = new
            {
                elapsedSeconds = Math.Round(gen.ElapsedSeconds, 3),
                peakWorkingSetMB = Math.Round(BytesToMB(gen.PeakWorkingSetBytes), 2),
                allocatedMB = Math.Round(BytesToMB(gen.AllocatedBytes), 2),
                gen0 = gen.Gen0Collections,
                gen1 = gen.Gen1Collections,
                gen2 = gen.Gen2Collections
            },
            build = new
            {
                exitCode = build.ExitCode,
                elapsedSeconds = Math.Round(build.ElapsedSeconds, 3),
                driverPeakWorkingSetMB = Math.Round(BytesToMB(build.PeakWorkingSetBytes), 2),
                driverProcessorTimeSeconds = Math.Round(build.TotalProcessorTimeSeconds, 3),
                driverAvgCpuUtilizationPercent = Math.Round(buildCpuPct, 1),
                note = "driver-only metrics; MSBuild/Roslyn worker subprocesses are not included"
            },
            finalExitCode = exitCode
        };

        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine();
        Console.WriteLine("=== Report (JSON) ===");
        Console.WriteLine(json);
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Generate:  {gen.ElapsedSeconds,7:F2}s   peak WS {BytesToMB(gen.PeakWorkingSetBytes),8:F2} MB   alloc {BytesToMB(gen.AllocatedBytes),8:F2} MB   GC {gen.Gen0Collections}/{gen.Gen1Collections}/{gen.Gen2Collections}");
        Console.WriteLine($"Build:     {build.ElapsedSeconds,7:F2}s   driver peak WS {BytesToMB(build.PeakWorkingSetBytes),8:F2} MB   driver CPU {build.TotalProcessorTimeSeconds:F1}s   avg {buildCpuPct:F1}% across {cores:F0} cores");
        Console.WriteLine("           (build-worker subprocesses not included in driver metrics)");
    }

    static double BytesToMB(long bytes) => bytes / (1024.0 * 1024.0);

    static void TryDeleteDirectoryWithRetries(string path, int maxRetries = 3, int delayMs = 200)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                Console.WriteLine($"Deleted temporary directory: {path}");
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (attempt == maxRetries)
                {
                    Console.Error.WriteLine($"Failed to delete temporary directory '{path}' after {maxRetries} attempts: {ex.Message}");
                    return;
                }
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unexpected error deleting temporary directory '{path}': {ex}");
                return;
            }
        }
    }
}
