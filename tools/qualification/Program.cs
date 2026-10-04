using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// Qualifies one release candidate (README "Release qualification"). Run from the repository root:
//   pack --out DIR                                    build and pack the library and CLI once; check packaging and compatibility
//   platform --candidate DIR --runner LABEL --out DIR test those exact bytes on this OS/architecture
//   gate --artifacts DIR --out FILE                   refuse unless every required platform and the native witness passed
//   release --artifacts DIR --tag TAG --out DIR       select the qualified library package, and only it, for publication
//   verify-publication --candidate DIR --out DIR [--feed URL]
//                                                     check the published package's visibility, linkage and served bytes
try
{
    return args switch
    {
        ["pack", "--out", var output] => Candidate.Pack(output),
        ["platform", "--candidate", var candidate, "--runner", var runner, "--out", var output] => PlatformLeg.Run(candidate, runner, output),
        ["gate", "--artifacts", var artifacts, "--out", var output] => Gate.Run(artifacts, output),
        ["release", "--artifacts", var artifacts, "--tag", var tag, "--out", var output] => Publication.Release(artifacts, tag, output),
        ["verify-publication", "--candidate", var candidate, "--out", var output] => Publication.Verify(candidate, Publication.Feed, output),
        ["verify-publication", "--candidate", var candidate, "--out", var output, "--feed", var feed] => Publication.Verify(candidate, feed, output),
        _ => Fail("Usage: pack --out DIR | platform --candidate DIR --runner LABEL --out DIR | gate --artifacts DIR --out FILE"
            + " | release --artifacts DIR --tag TAG --out DIR | verify-publication --candidate DIR --out DIR [--feed URL]"),
    };
}
catch (QualificationException failure)
{
    return Fail(failure.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"Qualification refused: {message}");
    return 1;
}

internal sealed class QualificationException(string message) : Exception(message);

/// <summary>The six client combinations the distribution decision requires, by hosted runner label.</summary>
internal static class Required
{
    public static readonly (string Runner, string Os, Architecture Architecture)[] Platforms =
    [
        ("windows-2025", "Windows Server 2025", Architecture.X64),
        ("windows-11-vs2026-arm", "Windows 11", Architecture.Arm64),
        ("ubuntu-24.04", "Ubuntu 24.04", Architecture.X64),
        ("ubuntu-24.04-arm", "Ubuntu 24.04", Architecture.Arm64),
        ("macos-15-intel", "macOS 15", Architecture.X64),
        ("macos-15", "macOS 15", Architecture.Arm64),
    ];

    /// <summary>The only tests a platform may skip: named-pipe tests run only on Windows, Ctrl+C (SIGINT) tests only off it.</summary>
    public static bool MaySkip(string check, string os, string test)
    {
        var method = test.Split('(')[0];
        return os.StartsWith("Windows", StringComparison.Ordinal)
            ? check.StartsWith("cli-", StringComparison.Ordinal) && WindowsSkips.Contains(method)
            : check == "sdk-tests" && PosixSkips.Contains(method);
    }

    private static readonly string[] PosixSkips =
    [
        "OnlyExactLocalPipesAreAcceptedAndConnectingIsCancellableAndBounded", "OwnedPipeIsClosedOnDisposalAndABorrowedPipeStaysOpen",
        "PipeThatIsNotPrivateToThisUserIsRefusedBeforeAnythingIsSent", "PrivatePipeCarriesOecpUntilTheControllerDisconnects",
    ];

    private static readonly string[] WindowsSkips =
    [
        "CtrlCDetachesWithExit130AndTheLastDeliveredCursor", "CtrlCAfterTheSubmissionWasSentIsAnUnknownOutcome",
        "CtrlCWhileWaitingAfterAcknowledgementIsExit130AndKeepsTheAcknowledgedRun", "CtrlCBeforeTheForceIsSentIsExit130WithNothingSent",
    ];

    /// <summary>Declared output only Ctrl+C tests produce, which cannot run on Windows.</summary>
    public static bool MayBeUnobserved(bool windows, string entry) => windows && entry == "error attempt.cancelled";

    public const string Repository = "https://github.com/faviann/zeroshot-dotnet-sdk";
    // The native pin, from native.props through this tool's assembly metadata.
    public static readonly string NativeVersion = Pin("ZeroshotNativeVersion");
    public static readonly string NativeSourceRevision = Pin("ZeroshotNativeSourceRevision");

    /// <summary>The last line of <c>zeroshot-dotnet --version</c>: the native release the bundled library binds.</summary>
    public static string NativeLine => $"native Zeroshot {NativeVersion} {NativeSourceRevision}";

    private static string Pin(string key) => typeof(Required).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(pin => pin.Key == key).Value!;
}

internal static class Tools
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The dotnet host running this process; every build, test and tool command uses the same one.</summary>
    public static string DotnetHost { get; } = Path.Combine(DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    public static string DotnetRoot => Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    /// <summary>The GitHub Actions run this process belongs to, or null outside one.</summary>
    public static string? WorkflowRun => Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { } run
        ? $"{Environment.GetEnvironmentVariable("GITHUB_SERVER_URL")}/{Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")}/actions/runs/{run}" : null;

    /// <summary>Runs a process, echoing and returning its combined output. <paramref name="path"/> replaces PATH when given.</summary>
    public static (int ExitCode, string Output) Run(string file, IEnumerable<string> arguments, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, string? log = null)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        }
        Console.WriteLine($"> {file} {string.Join(' ', start.ArgumentList)}");
        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => { if (line.Data is { } text) lock (output) { output.AppendLine(text); Console.WriteLine(text); } };
        process.ErrorDataReceived += (_, line) => { if (line.Data is { } text) lock (output) { output.AppendLine(text); Console.WriteLine(text); } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // A hung process fails its check instead of running into the job's time limit.
        if (!process.WaitForExit(TimeSpan.FromMinutes(20)))
        {
            process.Kill(entireProcessTree: true);
            lock (output) output.AppendLine("Killed after 20 minutes.");
            Console.WriteLine("Killed after 20 minutes.");
        }
        process.WaitForExit(); // drains the redirected output
        if (log is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, output.ToString());
        }
        return (process.ExitCode, output.ToString());
    }

    public static string Checked(string file, IEnumerable<string> arguments, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, string? log = null)
    {
        var (exitCode, output) = Run(file, arguments, workingDirectory, environment, log);
        return exitCode == 0 ? output : throw new QualificationException($"'{Path.GetFileName(file)} {string.Join(' ', arguments)}' exited {exitCode}.");
    }

    public static string Git(params string[] arguments) => Checked("git", arguments).Trim();
}

/// <summary>The packed candidate: its manifest and the exact package bytes every later job must receive.</summary>
internal sealed record CandidateFiles(string Directory, CandidateManifest Manifest)
{
    public string Version => Manifest.Version;
    public string Commit => Manifest.SourceCommit;
    public string ClientFile => Path.Combine(Directory, Manifest.Packages.Client.File);
    public string CliFile => Path.Combine(Directory, Manifest.Packages.Cli.File);
    public string ClientSha256 => Manifest.Packages.Client.Sha256;
    public string CliSha256 => Manifest.Packages.Cli.Sha256;

    /// <summary>
    /// Loads and verifies a downloaded candidate: the files must hash to the manifest and, when the pack job's outputs
    /// are supplied, the manifest must name those same hashes.
    /// </summary>
    public static CandidateFiles Load(string directory)
    {
        var manifestPath = Path.Combine(directory, "candidate.json");
        if (!File.Exists(manifestPath)) throw new QualificationException($"No candidate manifest in {directory}.");
        var candidate = new CandidateFiles(directory, JsonFile.Read<CandidateManifest>(manifestPath));
        foreach (var (file, sha256, expected) in new[]
        {
            (candidate.ClientFile, candidate.ClientSha256, Environment.GetEnvironmentVariable("ZEROSHOT_CANDIDATE_CLIENT_SHA256")),
            (candidate.CliFile, candidate.CliSha256, Environment.GetEnvironmentVariable("ZEROSHOT_CANDIDATE_CLI_SHA256")),
        })
        {
            if (Tools.Sha256(file) != sha256) throw new QualificationException($"{Path.GetFileName(file)} does not hash to its manifest entry.");
            if (!string.IsNullOrEmpty(expected) && expected != sha256)
                throw new QualificationException($"{Path.GetFileName(file)} is not the packed candidate ({sha256} != {expected}).");
        }
        return candidate;
    }

    public CandidateIdentity Identity() => new(Version, Commit, new(Path.GetFileName(ClientFile), ClientSha256), new(Path.GetFileName(CliFile), CliSha256));
}
