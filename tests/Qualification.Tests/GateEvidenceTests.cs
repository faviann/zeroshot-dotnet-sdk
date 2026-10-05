using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qualification.Tests;

/// <summary>The gate independently verifies downloaded evidence, even when a producer claims that every check passed.</summary>
[NotInParallel(nameof(FailClosedTests))]
public sealed class GateEvidenceTests
{
    private const string Runner = "ubuntu-24.04";
    private const string ClientHashVariable = "ZEROSHOT_CANDIDATE_CLIENT_SHA256";
    private const string CliHashVariable = "ZEROSHOT_CANDIDATE_CLI_SHA256";
    private string? created;
    private string? previousClientHash;
    private string? previousCliHash;

    [Before(Test)]
    public void RememberCandidateHashes()
    {
        previousClientHash = Environment.GetEnvironmentVariable(ClientHashVariable);
        previousCliHash = Environment.GetEnvironmentVariable(CliHashVariable);
    }

    [After(Test)]
    public void DeleteArtifactsAndRestoreCandidateHashes()
    {
        Environment.SetEnvironmentVariable(ClientHashVariable, previousClientHash);
        Environment.SetEnvironmentVariable(CliHashVariable, previousCliHash);
        if (created is not null) Directory.Delete(created, recursive: true);
    }

    [Test]
    public async Task CompleteEvidenceQualifiesTheCandidate()
    {
        var artifacts = Artifacts();
        var output = Path.Combine(artifacts, "qualification.json");

        await Assert.That(Gate.Run(artifacts, output)).IsEqualTo(0);
        var qualification = JsonFile.Read<QualificationManifest>(output);
        await Assert.That(qualification.Qualified).IsTrue();
        await Assert.That(qualification.Failures.Count).IsEqualTo(0);
        await Assert.That(qualification.Platforms.Count).IsEqualTo(6);
        await Assert.That(qualification.NativeWitness!.Result).IsEqualTo("PASS: candidate exercised");
    }

    [Test]
    [Arguments(Runner, "sdk-tests", "OnlyExactLocalPipesAreAcceptedAndConnectingIsCancellableAndBounded(1)")]
    [Arguments("windows-2025", "cli-repository", "CtrlCDetachesWithExit130AndTheLastDeliveredCursor")]
    public async Task PlatformSpecificAllowedSkipsStillQualify(string runner, string name, string skipped)
    {
        var artifacts = Artifacts();
        Edit(artifacts, runner, checks =>
        {
            var detail = Check(checks, name)["detail"]!.AsObject();
            detail["succeeded"] = 2;
            detail["skipped"] = 1;
            detail["skippedTests"] = new JsonArray(skipped);
        });

        await Assert.That(Gate.Run(artifacts, Path.Combine(artifacts, "qualification.json"))).IsEqualTo(0);
    }

    [Test]
    [Arguments("missing-check")]
    [Arguments("failed-check")]
    [Arguments("duplicate-passing-check")]
    [Arguments("duplicate-failed-check")]
    [Arguments("contradictory-failure")]
    [Arguments("undeclared-check")]
    [Arguments("null-check")]
    [Arguments("missing-detail")]
    [Arguments("malformed-detail")]
    [Arguments("empty-suite")]
    [Arguments("failed-tests")]
    [Arguments("negative-failed")]
    [Arguments("negative-succeeded")]
    [Arguments("negative-skipped")]
    [Arguments("inconsistent-total")]
    [Arguments("missing-skipped-names")]
    [Arguments("unreported-skip")]
    [Arguments("null-skipped-name")]
    [Arguments("unauthorized-skip")]
    [Arguments("nonzero-exit")]
    [Arguments("missing-unobserved-output")]
    [Arguments("missing-undeclared-output")]
    [Arguments("null-unobserved-output")]
    [Arguments("null-undeclared-output")]
    [Arguments("unobserved-output")]
    [Arguments("undeclared-output")]
    public async Task IncompleteOrContradictoryEvidenceIsRefusedAndRecorded(string mutation)
    {
        var artifacts = Artifacts();
        Edit(artifacts, Runner, checks => Mutate(checks, mutation));
        var output = Path.Combine(artifacts, "qualification.json");

        var result = Gate.Run(artifacts, output);
        var qualification = JsonFile.Read<QualificationManifest>(output);
        await Assert.That(result).IsEqualTo(1);
        await Assert.That(qualification.Qualified).IsFalse();
        await Assert.That(qualification.Failures).Contains(failure => failure.Contains(Runner, StringComparison.Ordinal));
        await Assert.That(qualification.NativeWitness!.Result).IsEqualTo("PASS: candidate exercised");
    }

    private static void Mutate(JsonArray checks, string mutation)
    {
        var check = Check(checks, "sdk-tests");
        var detail = check["detail"]!.AsObject();
        var cli = Check(checks, "cli-repository")["detail"]!.AsObject();
        switch (mutation)
        {
            case "missing-check": checks.Remove(check); break;
            case "failed-check": check["passed"] = false; break;
            case "duplicate-passing-check": checks.Add(check.DeepClone()); break;
            case "duplicate-failed-check":
                var duplicate = check.DeepClone();
                duplicate["passed"] = false;
                checks.Add(duplicate);
                break;
            case "contradictory-failure": check["failure"] = "The suite failed."; break;
            case "undeclared-check": checks.Add(new JsonObject { ["name"] = "unrecognized-check", ["passed"] = true }); break;
            case "null-check": checks.Add((JsonNode?)null); break;
            case "missing-detail": check.Remove("detail"); break;
            case "malformed-detail": detail["total"] = "three"; break;
            case "empty-suite": detail["total"] = 0; detail["succeeded"] = 0; break;
            case "failed-tests": detail["failed"] = 1; detail["succeeded"] = 2; break;
            case "negative-failed": detail["failed"] = -1; detail["succeeded"] = 4; break;
            case "negative-succeeded": detail["succeeded"] = -1; break;
            case "negative-skipped": detail["skipped"] = -1; detail["succeeded"] = 4; break;
            case "inconsistent-total": detail["total"] = 4; break;
            case "missing-skipped-names": detail["skipped"] = 1; detail["succeeded"] = 2; break;
            case "unreported-skip": detail["skippedTests"] = new JsonArray("OwnedPipeIsClosedOnDisposalAndABorrowedPipeStaysOpen"); break;
            case "null-skipped-name":
                detail["skipped"] = 1;
                detail["succeeded"] = 2;
                detail["skippedTests"] = new JsonArray((JsonNode?)null);
                break;
            case "unauthorized-skip":
                detail["skipped"] = 1;
                detail["succeeded"] = 2;
                detail["skippedTests"] = new JsonArray("AnEssentialTest");
                break;
            case "nonzero-exit": detail["exitCode"] = 1; break;
            case "missing-unobserved-output": cli.Remove("unobservedOutput"); break;
            case "missing-undeclared-output": cli.Remove("undeclaredOutput"); break;
            case "null-unobserved-output": cli["unobservedOutput"] = null; break;
            case "null-undeclared-output": cli["undeclaredOutput"] = null; break;
            case "unobserved-output": cli["unobservedOutput"] = new JsonArray("run runId"); break;
            case "undeclared-output": cli["undeclaredOutput"] = new JsonArray("run surprise"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown evidence mutation.");
        }
    }

    /// <summary>All six platform legs and the native witness name these exact candidate package bytes.</summary>
    private string Artifacts()
    {
        var artifacts = created = Directory.CreateTempSubdirectory("gate-evidence-tests-").FullName;
        var directory = Directory.CreateDirectory(Path.Combine(artifacts, "candidate")).FullName;
        File.WriteAllText(Path.Combine(directory, "client.nupkg"), "client");
        File.WriteAllText(Path.Combine(directory, "cli.nupkg"), "cli");
        var client = Tools.Sha256(Path.Combine(directory, "client.nupkg"));
        var cli = Tools.Sha256(Path.Combine(directory, "cli.nupkg"));
        Environment.SetEnvironmentVariable(ClientHashVariable, client);
        Environment.SetEnvironmentVariable(CliHashVariable, cli);
        var manifest = new CandidateManifest("10.10.0.1", "abc", true, ["v10.10.0.1"], "v10.10.0.1", null, null, "10.0.100", "10.10.0.1+abc",
            new(new("Zeroshot.Client", "client.nupkg", client, "library"), new("Zeroshot.Cli", "cli.nupkg", cli, "command", "library")),
            new(new("published", "10.10.0.0", Tag: "v10.10.0.0"), "10.10.0", 1, [], null), ["cli output run runId"]);
        Write(Path.Combine(directory, "candidate.json"), manifest);
        var identity = new CandidateFiles(directory, manifest).Identity();
        foreach (var (runner, os, architecture) in Required.Platforms)
        {
            List<CheckResult> checks = [.. new[]
            {
                "platform", "fresh-consumers-restore-candidate", "ContractsConsumer", "BindingsConsumer", "RunHandleConsumer", "no-python-or-native-used",
            }.Select(name => new CheckResult { Name = name, Passed = true })];
            checks.Add(new() { Name = "sdk-tests", Passed = true, Detail = JsonFile.Node(new SuiteSummary(3, 0, 3, 0, [], 0)) });
            foreach (var name in new[] { "cli-repository", "cli-global-tool", "cli-local-manifest", "cli-explicit-path" })
                checks.Add(new() { Name = name, Passed = true, Detail = JsonFile.Node(new SuiteSummary(3, 0, 3, 0, [], 0, [], [])) });
            Write(Path.Combine(artifacts, "platform-" + runner, "evidence.json"), new LegEvidence(runner,
                new(os, architecture.ToString()), identity,
                new(os, os, os, architecture.ToString(), architecture.ToString(), "", ".NET 10.0.0", "10.0.0", "10.0.100", "/dotnet"), checks, true));
        }

        var native = Directory.CreateDirectory(Path.Combine(artifacts, "native-witness")).FullName;
        File.WriteAllLines(Path.Combine(native, "provenance.txt"),
        [
            $"nativeVersion={Required.NativeVersion}", $"sourceRevision={Required.NativeSourceRevision}", "release=fixture", "archiveSha256=fixture",
            "sdkCandidate=supplied", "sdkCommit=abc", $"{client}  ./client.nupkg", $"{cli}  ./cli.nupkg",
            $"{new string('a', 64)}  bin/zeroshot", "zeroshot-dotnet 10.10.0.1+abc",
        ]);
        File.WriteAllText(Path.Combine(native, "result.txt"), "PASS: candidate exercised\n");
        return artifacts;
    }

    private static JsonObject Check(JsonArray checks, string name)
        => checks.Select(check => check!.AsObject()).Single(check => check["name"]!.GetValue<string>() == name);

    private static void Edit(string artifacts, string runner, Action<JsonArray> edit)
    {
        var path = Path.Combine(artifacts, "platform-" + runner, "evidence.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        edit(json["checks"]!.AsArray());
        File.WriteAllText(path, json.ToJsonString());
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonFile.Options));
    }
}
