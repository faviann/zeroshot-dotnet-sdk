using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qualification.Tests;

/// <summary>
/// Release and the gate on a downloaded candidate whose files were altered after they were written. An added field is
/// refused twice over, by the strict reading and by release comparing the two files' JSON, so either alone must refuse.
/// </summary>
[NotInParallel(nameof(FailClosedTests))]
public sealed class FailClosedTests
{
    private const string Tag = "v10.10.0.1";
    private string? created;

    [After(Test)]
    public void DeleteArtifacts()
    {
        if (created is not null) Directory.Delete(created, recursive: true);
    }

    [Test]
    public async Task ReleaseSelectsTheLibraryPackageOfAnUnalteredQualifiedCandidate()
    {
        var artifacts = Artifacts();
        var output = Path.Combine(artifacts, "out");
        await Assert.That(Publication.Release(artifacts, Tag, output)).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(output, "client.nupkg"))).IsTrue();
    }

    [Test]
    public async Task ReleaseRefusesACandidateManifestWithAnAddedField()
    {
        var artifacts = Artifacts();
        Edit(Path.Combine(artifacts, "candidate", "candidate.json"), json => json["extra"] = 1);
        await Refused(artifacts);
    }

    [Test]
    public async Task ReleaseRefusesQualificationEvidenceWhoseCandidateHasAnAddedField()
    {
        var artifacts = Artifacts();
        Edit(Path.Combine(artifacts, "qualification", "qualification.json"), json => json["candidate"]!["packages"]!["client"]!["extra"] = 1);
        await Refused(artifacts);
    }

    [Test]
    public async Task ReleaseRefusesQualificationEvidenceForAnotherCandidate()
    {
        var artifacts = Artifacts();
        Edit(Path.Combine(artifacts, "qualification", "qualification.json"), json => json["candidate"]!["dotnetSdk"] = "10.0.999");
        await Assert.That(await Refused(artifacts)).IsEqualTo("The qualification evidence names another candidate than the downloaded one.");
    }

    [Test]
    public async Task TheGateRefusesALegWithoutAVerdictAndStillWritesItsManifest()
    {
        var artifacts = Artifacts();
        var evidence = Path.Combine(artifacts, "platform-ubuntu-24.04", "evidence.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        var identity = CandidateFiles.Load(Path.Combine(artifacts, "candidate")).Identity();
        File.WriteAllText(evidence, JsonSerializer.Serialize(new LegEvidence("ubuntu-24.04", null, identity,
            new("Ubuntu 24.04", "", "", "X64", "X64", "", "", "10.0.0", "", ""), [], true), JsonFile.Options));
        Edit(evidence, json => json.AsObject().Remove("passed"));

        var output = Path.Combine(artifacts, "qualification.json");
        await Assert.That(Gate.Run(artifacts, output)).IsEqualTo(1);
        var failures = JsonFile.Read<QualificationManifest>(output).Failures;
        await Assert.That(failures).Contains(failure => failure.StartsWith($"{evidence} is not a valid LegEvidence", StringComparison.Ordinal));
        await Assert.That(failures).Contains("ubuntu-24.04: 0 evidence files, expected exactly one.");
    }

    /// <summary>A packed, qualified candidate for <see cref="Tag"/>, as the release job downloads it.</summary>
    private string Artifacts()
    {
        var artifacts = created = Directory.CreateTempSubdirectory("qualification-tests-").FullName;
        var candidate = Directory.CreateDirectory(Path.Combine(artifacts, "candidate")).FullName;
        File.WriteAllText(Path.Combine(candidate, "client.nupkg"), "client");
        File.WriteAllText(Path.Combine(candidate, "cli.nupkg"), "cli");
        var (client, cli) = (Tools.Sha256(Path.Combine(candidate, "client.nupkg")), Tools.Sha256(Path.Combine(candidate, "cli.nupkg")));
        Environment.SetEnvironmentVariable("ZEROSHOT_CANDIDATE_CLIENT_SHA256", client);
        Environment.SetEnvironmentVariable("ZEROSHOT_CANDIDATE_CLI_SHA256", cli);
        var manifest = new CandidateManifest("10.10.0.1", "abc", true, [Tag], Tag, null, null, "10.0.100", "10.10.0.1+abc",
            new(new("Zeroshot.Client", "client.nupkg", client, "l"), new("Zeroshot.Cli", "cli.nupkg", cli, "c", "l")),
            new(new("published", "10.10.0.0", Tag: "v10.10.0.0"), "10.10.0", 0, [], null), []);
        File.WriteAllText(Path.Combine(candidate, "candidate.json"), JsonSerializer.Serialize(manifest, JsonFile.Options));
        Directory.CreateDirectory(Path.Combine(artifacts, "qualification"));
        File.WriteAllText(Path.Combine(artifacts, "qualification", "qualification.json"),
            JsonSerializer.Serialize(new QualificationManifest(true, manifest, [], null, []), JsonFile.Options));
        return artifacts;
    }

    private static void Edit(string path, Action<JsonNode> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    private static async Task<string> Refused(string artifacts)
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Task.FromResult(Publication.Release(artifacts, Tag, Path.Combine(artifacts, "out"))));
        return refusal!.Message;
    }
}
