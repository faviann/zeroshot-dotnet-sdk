using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

// The files the tool writes, and the jobs after it read back: one type per file and per nested object, so a writer and
// its readers cannot disagree on a property name. Property order is the files' field order.

internal static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>One line, for failure messages.</summary>
    public static string Compact<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    public static JsonObject? Node(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options)!.AsObject();

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new QualificationException($"{path} holds no {typeof(T).Name}.");

    /// <summary>Writes and echoes the file.</summary>
    public static void Write<T>(string path, T value)
    {
        var text = JsonSerializer.Serialize(value, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text + "\n");
        Console.WriteLine(text);
    }

    /// <summary>Same JSON, so a record holding collections compares by content.</summary>
    public static bool Same<T>(T x, T y) => JsonSerializer.Serialize(x, Options) == JsonSerializer.Serialize(y, Options);
}

/// <summary>candidate.json, written by pack.</summary>
internal sealed record CandidateManifest(
    string Version, string SourceCommit, bool WorktreeClean, string[] Tags, string? ReleaseTag, string? Ref, string? WorkflowRun,
    string DotnetSdk, string InformationalVersion, Packages Packages, Compatibility Compatibility,
    // Every declared output kind and path; each platform's CLI suites must emit them all and nothing else.
    string[] CliOutput)
{
    [JsonPropertyOrder(-1)] public string Schema => "zeroshot-dotnet/qualification-candidate/v1";
}

internal sealed record Packages(ClientPackage Client, CliPackage Cli);
internal sealed record ClientPackage(string Id, string File, string Sha256, string LibrarySha256);
internal sealed record CliPackage(string Id, string File, string Sha256, string CommandSha256, string BundledLibrarySha256);

internal sealed record Compatibility(CompatibilityBaseline Baseline, string BaselineNative, int Entries, List<string> Missing, string? MigrationNotes);

/// <summary>A published release (<see cref="Tag"/>) or the accepted prototype (<see cref="Contract"/>, <see cref="Decision"/>).</summary>
internal sealed record CompatibilityBaseline(
    string Kind, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tag = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Contract = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Decision = null);

/// <summary>The package bytes a leg tested; equal to the candidate's only for exactly the same files.</summary>
internal sealed record CandidateIdentity(string Version, string SourceCommit, FileHash Client, FileHash Cli);
internal sealed record FileHash(string File, string Sha256);

/// <summary>evidence.json, written by a platform leg.</summary>
internal sealed record LegEvidence(string Runner, ExpectedPlatform? Expected, CandidateIdentity Candidate, ObservedPlatform Platform, List<CheckResult> Checks, bool Passed)
{
    [JsonPropertyOrder(-1)] public string Schema => "zeroshot-dotnet/qualification-platform/v1";
}

internal sealed record ExpectedPlatform(string Os, string Architecture);

internal sealed record ObservedPlatform(
    string Os, string OsDetail, string OsDescription, string ProcessArchitecture, string OsArchitecture, string RuntimeIdentifier,
    string Runtime, string RuntimeVersion, string SdkVersion, string DotnetRoot);

/// <summary>One check of a leg. <see cref="Detail"/> is the check's own record, or absent.</summary>
internal sealed record CheckResult(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Detail,
    bool Passed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Failure);

internal sealed record ConsumerRestore(string CachedPackageSha256, int ConsumerLibraries);

/// <summary>A test suite's summary; the CLI suites add the output coverage.</summary>
internal sealed record SuiteSummary(
    int Total, int Failed, int Succeeded, int Skipped,
    // Named so the gate can hold skips to the platform-specific allowlist.
    string[] SkippedTests, int ExitCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? UnobservedOutput = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? UndeclaredOutput = null);

internal sealed record Isolation(string[] Path, string[] Poisoned, List<string> FoundBeyondPoison, string[] Invoked);

/// <summary>qualification.json, written by the gate either way.</summary>
internal sealed record QualificationManifest(bool Qualified, CandidateManifest Candidate, List<PlatformSummary> Platforms, NativeWitness? NativeWitness, List<string> Failures)
{
    [JsonPropertyOrder(-1)] public string Schema => "zeroshot-dotnet/qualification/v1";
}

internal sealed record PlatformSummary(
    string Runner, string? Os, string? OsDetail, string? ProcessArchitecture, string? OsArchitecture, string? Runtime, string? Sdk, List<CheckSummary> Checks);

internal sealed record CheckSummary(string Name, bool Passed, JsonObject? Detail);

internal sealed record NativeWitness(
    string? NativeVersion, string? SourceRevision, string? Release, string? ArchiveSha256, string? ExecutableSha256, List<FileHash> Identities, string Result);

/// <summary>publication.json, written by verify-publication either way.</summary>
internal sealed record PublicationManifest(
    bool Verified, string PackageId, string Version, string SourceCommit, string Feed, string ExpectedSha256,
    // A RegistryPackage, or the RegistryRefusal status when the registry would not say.
    object Package, RestoreSteps Restore, string? WorkflowRun, string CheckedAt, List<string> Failures)
{
    [JsonPropertyOrder(-1)] public string Schema => "zeroshot-dotnet/publication/v1";
}

internal sealed record RegistryPackage(string? Visibility, string? Repository, string? HtmlUrl);
internal sealed record RegistryRefusal(int Status);

/// <summary>How far the fresh consumer got: each step that ran, then what the feed served.</summary>
internal sealed class RestoreSteps
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Restore { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Build { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Run { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ServedSha256 { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; set; }
}
