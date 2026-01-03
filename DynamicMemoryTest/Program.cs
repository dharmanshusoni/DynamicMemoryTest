// GeneratorAndDotnetBuild_Fixed.cs
// Safe version: no duplicate "_" vars, faster I/O, still stresses compiler heavily.
// Usage: dotnet run -- 500 10 50  (numbers = namespaces, classes/namespace, fields/class)

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

class Program
{
    static int Main(string[] args)
    {
        int namespaces = args.Length > 0 ? int.Parse(args[0]) : 5000;
        int classesPerNamespace = args.Length > 1 ? int.Parse(args[1]) : 10;
        int fieldsPerClass = args.Length > 2 ? int.Parse(args[2]) : 50;

        Console.WriteLine($"Generating: {namespaces} namespaces × {classesPerNamespace} classes × {fieldsPerClass} fields");

        string workDir = Path.Combine(Path.GetTempPath(), "cs_compile_stress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        Run("dotnet", "new classlib -n StressLib", workDir);

        string projDir = Path.Combine(workDir, "StressLib");
        string srcDir = projDir;

        string defaultFile = Path.Combine(srcDir, "Class1.cs");
        if (File.Exists(defaultFile)) File.Delete(defaultFile);

        long totalClasses = (long)namespaces * classesPerNamespace;
        Console.WriteLine($"→ Creating about {totalClasses:N0} .cs files in {srcDir}");

        for (int ns = 1; ns <= namespaces; ns++)
        {
            string nsName = $"StressNs{ns}";
            for (int c = 1; c <= classesPerNamespace; c++)
            {
                string className = $"C_{ns}_{c}";
                var sb = new StringBuilder();
                sb.AppendLine("using System;");
                sb.AppendLine($"namespace {nsName}");
                sb.AppendLine("{");
                sb.AppendLine($"  public class {className}");
                sb.AppendLine("  {");

                // Generate many fields
                for (int f = 0; f < fieldsPerClass; f++)
                {
                    sb.AppendLine($"    public string F{f} = \"{new string('X', 50)}\";");
                }

                // Method that references fields (unique variable names)
                sb.AppendLine();
                sb.AppendLine("    public void DoNothing()");
                sb.AppendLine("    {");
                for (int i = 0; i < 30; i++)
                {
                    sb.AppendLine($"      int val{i} = F{i % fieldsPerClass}.Length;");
                }
                sb.AppendLine("    }");

                sb.AppendLine("  }");
                sb.AppendLine("}");

                File.WriteAllText(Path.Combine(srcDir, $"{className}.cs"), sb.ToString());
            }
            if (ns % 100 == 0)
                Console.WriteLine($"  Created namespaces: {ns}/{namespaces}");
        }

        Console.WriteLine("✅ Generation done. Building project...");

        int exitCode = 1;
        var sw = Stopwatch.StartNew();
        try
        {
            exitCode = Run("dotnet", "build -c Release", projDir);
            sw.Stop();

            Console.WriteLine($"Build completed in {sw.Elapsed}. Exit code: {exitCode}");
            Console.WriteLine($"Source directory: {projDir}");
            Console.WriteLine("(Temporary directory will be removed now.)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.Error.WriteLine($"Build failed with exception: {ex}");
            // exitCode already set to non-zero
        }
        finally
        {
            // Attempt to delete the temporary work directory (retry a few times if files are still in use)
            TryDeleteDirectoryWithRetries(workDir, maxRetries: 5, delayMs: 500);
        }

        return exitCode;
    }

    static int Run(string file, string args, string workDir)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

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

                // small backoff before retry
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