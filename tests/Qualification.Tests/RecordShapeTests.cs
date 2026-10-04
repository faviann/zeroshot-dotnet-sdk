using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qualification.Tests;

/// <summary>
/// The exact JSON of every file the tool writes: property names, order, and which absent values are null or omitted.
/// CI reads these files (jq over candidate.json) and later jobs read them back, so a rename must fail here.
/// </summary>
public sealed class RecordShapeTests
{
    private static readonly CandidateManifest Manifest = new(
        "10.10.0.1", "abc", true, ["v10.10.0.1"], "v10.10.0.1", null, null, "10.0.100", "10.10.0.1+abc",
        new(new("Zeroshot.Client", "client.nupkg", "c1", "l1"), new("Zeroshot.Cli", "cli.nupkg", "c2", "d2", "l1")),
        new(new("published", "10.10.0.0", Tag: "v10.10.0.0"), "10.10.0", 2, [], null),
        ["cli output run runId"]);

    private const string ManifestJson = """
        {"schema":"zeroshot-dotnet/qualification-candidate/v1","version":"10.10.0.1","sourceCommit":"abc","worktreeClean":true,"tags":["v10.10.0.1"],
        "releaseTag":"v10.10.0.1","ref":null,"workflowRun":null,"dotnetSdk":"10.0.100","informationalVersion":"10.10.0.1+abc",
        "packages":{"client":{"id":"Zeroshot.Client","file":"client.nupkg","sha256":"c1","librarySha256":"l1"},
        "cli":{"id":"Zeroshot.Cli","file":"cli.nupkg","sha256":"c2","commandSha256":"d2","bundledLibrarySha256":"l1"}},
        "compatibility":{"baseline":{"kind":"published","version":"10.10.0.0","tag":"v10.10.0.0"},"baselineNative":"10.10.0","entries":2,"missing":[],"migrationNotes":null},
        "cliOutput":["cli output run runId"]}
        """;

    private static readonly LegEvidence Evidence = new(
        "ubuntu-24.04", new("Ubuntu 24.04", "X64"), new("10.10.0.1", "abc", new("client.nupkg", "c1"), new("cli.nupkg", "c2")),
        new("Ubuntu 24.04", "Ubuntu 24.04.1 LTS", "Linux", "X64", "X64", "linux-x64", ".NET 10.0.0", "10.0.0", "10.0.100", "/dotnet"),
        [
            new() { Name = "platform", Passed = true },
            new() { Name = "fresh-consumers-restore-candidate", Detail = JsonFile.Node(new ConsumerRestore("c1", 3)), Passed = true },
            new() { Name = "sdk-tests", Detail = JsonFile.Node(new SuiteSummary(5, 0, 4, 1, ["Skipped(1)"], 0)), Passed = true },
            new() { Name = "cli-repository", Detail = JsonFile.Node(new SuiteSummary(2, 0, 2, 0, [], 0) { UnobservedOutput = [], UndeclaredOutput = ["x y"] }), Passed = true },
            new() { Name = "no-python-or-native-used", Detail = JsonFile.Node(new Isolation(["/poison", "/dotnet"], ["python"], [], [])), Passed = true },
            new() { Name = "cli-global-tool", Passed = false, Failure = "exited 131." },
        ],
        false);

    private const string EvidenceJson = """
        {"schema":"zeroshot-dotnet/qualification-platform/v1","runner":"ubuntu-24.04","expected":{"os":"Ubuntu 24.04","architecture":"X64"},
        "candidate":{"version":"10.10.0.1","sourceCommit":"abc","client":{"file":"client.nupkg","sha256":"c1"},"cli":{"file":"cli.nupkg","sha256":"c2"}},
        "platform":{"os":"Ubuntu 24.04","osDetail":"Ubuntu 24.04.1 LTS","osDescription":"Linux","processArchitecture":"X64","osArchitecture":"X64",
        "runtimeIdentifier":"linux-x64","runtime":".NET 10.0.0","runtimeVersion":"10.0.0","sdkVersion":"10.0.100","dotnetRoot":"/dotnet"},
        "checks":[{"name":"platform","passed":true},
        {"name":"fresh-consumers-restore-candidate","detail":{"cachedPackageSha256":"c1","consumerLibraries":3},"passed":true},
        {"name":"sdk-tests","detail":{"total":5,"failed":0,"succeeded":4,"skipped":1,"skippedTests":["Skipped(1)"],"exitCode":0},"passed":true},
        {"name":"cli-repository","detail":{"total":2,"failed":0,"succeeded":2,"skipped":0,"skippedTests":[],"exitCode":0,"unobservedOutput":[],"undeclaredOutput":["x y"]},"passed":true},
        {"name":"no-python-or-native-used","detail":{"path":["/poison","/dotnet"],"poisoned":["python"],"foundBeyondPoison":[],"invoked":[]},"passed":true},
        {"name":"cli-global-tool","passed":false,"failure":"exited 131."}],
        "passed":false}
        """;

    private static readonly QualificationManifest Qualification = new(
        false, Manifest with { Compatibility = new(new("accepted-prototype", "0.1.0-preview.1", Contract: "p.txt", Decision: "d"), "10.9.0", 1, ["api X"], "m.md") },
        [new("ubuntu-24.04", "Ubuntu 24.04", "d", "X64", "X64", "10.0.0", "10.0.100", [new("platform", true, null), new("sdk-tests", false, new JsonObject { ["total"] = 1 })])],
        new("10.10.0", "rev", null, "a", "e", [new("bin/zeroshot", "e")], "PASS: ok"),
        ["a failure"]);

    private const string QualificationJson = """
        {"schema":"zeroshot-dotnet/qualification/v1","qualified":false,"candidate":{"schema":"zeroshot-dotnet/qualification-candidate/v1","version":"10.10.0.1",
        "sourceCommit":"abc","worktreeClean":true,"tags":["v10.10.0.1"],"releaseTag":"v10.10.0.1","ref":null,"workflowRun":null,"dotnetSdk":"10.0.100",
        "informationalVersion":"10.10.0.1+abc","packages":{"client":{"id":"Zeroshot.Client","file":"client.nupkg","sha256":"c1","librarySha256":"l1"},
        "cli":{"id":"Zeroshot.Cli","file":"cli.nupkg","sha256":"c2","commandSha256":"d2","bundledLibrarySha256":"l1"}},
        "compatibility":{"baseline":{"kind":"accepted-prototype","version":"0.1.0-preview.1","contract":"p.txt","decision":"d"},"baselineNative":"10.9.0",
        "entries":1,"missing":["api X"],"migrationNotes":"m.md"},"cliOutput":["cli output run runId"]},
        "platforms":[{"runner":"ubuntu-24.04","os":"Ubuntu 24.04","osDetail":"d","processArchitecture":"X64","osArchitecture":"X64","runtime":"10.0.0","sdk":"10.0.100",
        "checks":[{"name":"platform","passed":true,"detail":null},{"name":"sdk-tests","passed":false,"detail":{"total":1}}]}],
        "nativeWitness":{"nativeVersion":"10.10.0","sourceRevision":"rev","release":null,"archiveSha256":"a","executableSha256":"e",
        "identities":[{"file":"bin/zeroshot","sha256":"e"}],"result":"PASS: ok"},
        "failures":["a failure"]}
        """;

    [Test]
    public async Task TheCandidateManifestKeepsItsShapeAndReadsBack() => await Shape(Manifest, ManifestJson);

    [Test]
    public async Task LegEvidenceKeepsItsShapeAndReadsBack() => await Shape(Evidence, EvidenceJson);

    [Test]
    public async Task TheQualificationManifestKeepsItsShapeAndReadsBack() => await Shape(Qualification, QualificationJson);

    [Test]
    public async Task AnUnexpectedRunnerAndAMissingWitnessAreNull()
    {
        await Assert.That(Compact(Evidence with { Expected = null })).Contains("""
            "runner":"ubuntu-24.04","expected":null,"candidate"
            """);
        await Assert.That(Compact(Qualification with { NativeWitness = null })).Contains("""
            "nativeWitness":null,"failures"
            """);
    }

    [Test]
    [Arguments("\"worktreeClean\":true,", "\"worktreeClean\":true,\"extra\":1,")]
    [Arguments("\"sourceCommit\":", "\"SourceCommit\":")]
    [Arguments("\"releaseTag\":\"v10.10.0.1\",", "")]
    [Arguments("\"dotnetSdk\":\"10.0.100\"", "\"dotnetSdk\":null")]
    [Arguments("\"librarySha256\":\"l1\"}", "\"librarySha256\":\"l1\",\"extra\":1}")]
    public async Task AnUnknownMisspelledMissingOrNullFieldRefusesTheManifest(string field, string replacement)
        => await Refused<CandidateManifest>(ManifestJson, field, replacement);

    [Test]
    [Arguments("{\"name\":\"platform\",\"passed\":true}", "{\"name\":\"platform\"}")]
    [Arguments(",\"passed\":false}", "}")]
    [Arguments("\"runner\":\"ubuntu-24.04\"", "\"runner\":null")]
    [Arguments("\"expected\":{\"os\":\"Ubuntu 24.04\",\"architecture\":\"X64\"},", "")]
    public async Task ALegWithoutAVerdictOrRunnerIsRefusedRatherThanDefaulted(string field, string replacement)
        => await Refused<LegEvidence>(EvidenceJson, field, replacement);

    [Test]
    public async Task AQualificationWithoutItsVerdictIsRefused()
        => await Refused<QualificationManifest>(QualificationJson, "\"qualified\":false,", "");

    private static async Task Refused<T>(string json, string field, string replacement)
    {
        var flat = Flat(json);
        await Assert.That(flat).Contains(field);
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Task.FromResult(JsonFile.Parse<T>(flat.Replace(field, replacement), "test.json")));
        await Assert.That(refusal!.Message).StartsWith($"test.json is not a valid {typeof(T).Name}");
    }

    [Test]
    public async Task ThePublicationRecordKeepsItsShape()
    {
        var restore = new RestoreSteps { Restore = true, Build = true, Run = true, ServedSha256 = "s", Source = "feed" };
        await Assert.That(Compact(new PublicationManifest(true, "Zeroshot.Client", "10.10.0.1", "abc", "feed", "s",
            new RegistryPackage("public", null, "https://h"), restore, null, "now", []))).IsEqualTo(Flat("""
            {"schema":"zeroshot-dotnet/publication/v1","verified":true,"packageId":"Zeroshot.Client","version":"10.10.0.1","sourceCommit":"abc","feed":"feed",
            "expectedSha256":"s","package":{"visibility":"public","repository":null,"htmlUrl":"https://h"},
            "restore":{"restore":true,"build":true,"run":true,"servedSha256":"s","source":"feed"},"workflowRun":null,"checkedAt":"now","failures":[]}
            """));
        // A refused registry query and a consumer that stopped at its first step.
        await Assert.That(Compact(new PublicationManifest(false, "Zeroshot.Client", "10.10.0.1", "abc", "feed", "s",
            new RegistryRefusal(401), new RestoreSteps { Restore = false }, "run", "now", ["f"]))).Contains("""
            "package":{"status":401},"restore":{"restore":false},"workflowRun":"run"
            """);
    }

    private static async Task Shape<T>(T value, string expected)
    {
        var json = Compact(value);
        await Assert.That(json).IsEqualTo(Flat(expected));
        await Assert.That(Compact(JsonFile.Parse<T>(json, "test.json"))).IsEqualTo(json);
    }

    private static string Compact<T>(T value) => JsonSerializer.Serialize(value, Single);

    private static readonly JsonSerializerOptions Single = new(JsonFile.Options) { WriteIndented = false };

    private static string Flat(string json) => json.ReplaceLineEndings("");
}
