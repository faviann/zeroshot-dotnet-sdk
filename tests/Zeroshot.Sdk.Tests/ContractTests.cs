using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class ContractTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static void Reject(Action action)
    {
        try { action(); }
        catch (JsonException error) { Check(!error.ToString().Contains("must-not-be-retained"), "Error exposed authored data."); return; }
        throw new InvalidOperationException("Invalid contract was accepted.");
    }
    private static void Roundtrip<T>(string fixture)
    {
        using var data = JsonDocument.Parse(Fixture(fixture));
        foreach (var value in data.RootElement.EnumerateArray())
        {
            var parsed = NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(value.GetRawText()));
            _ = NativeJson.DeserializeUtf8<T>(NativeJson.SerializeUtf8(parsed));
        }
    }

    [Test]
    public void GoldenGraphPayloadRuntimeAndWorkerAlternatives()
    {
        Roundtrip<GraphSpec>("graphs.json");
        Roundtrip<PayloadType>("payloads.json");
        Roundtrip<RuntimePlan>("runtimes.json");
        Roundtrip<WorkerOutcome>("outcomes.json");
        _ = NativeJson.DeserializeUtf8<WorkerDescriptor>(Fixture("worker.json"));
        _ = NativeJson.DeserializeUtf8<ArtifactRef>(Fixture("artifact.json"));
        using var schema = JsonDocument.Parse(NativeSchemas.ExportCompiledIrUtf8());
        Check(schema.RootElement.GetProperty("title").GetString() == "CompiledGraphIr", "Missing compiled IR schema.");
    }

    [Test]
    public void DiagnosticPathsPreserveTaggedSegmentsAndNativeIndexBounds()
    {
        Roundtrip<GraphDiagnostic>("diagnostics.json");
        using var fixture = JsonDocument.Parse(Fixture("diagnostics.json"));
        var diagnostic = NativeJson.DeserializeUtf8<GraphDiagnostic>(Encoding.UTF8.GetBytes(fixture.RootElement[0].GetRawText()));
        Check(diagnostic.Path is [FieldDiagnosticPathSegment { Name.Value: "input" },
            IndexDiagnosticPathSegment { Index: uint.MaxValue }, NodeDiagnosticPathSegment { Name.Value: "work" }],
            "Diagnostic field, index and node alternatives were not preserved.");
        DiagnosticPathSegment[] authored =
        [
            new FieldDiagnosticPathSegment { Name = new FieldName("input") },
            new IndexDiagnosticPathSegment { Index = 0 },
            new NodeDiagnosticPathSegment { Name = new NodeName("work") }
        ];
        foreach (var segment in authored)
            Check(NativeJson.DeserializeUtf8<DiagnosticPathSegment>(NativeJson.SerializeUtf8(segment)) == segment,
                "Authored diagnostic path did not round-trip.");
        Check(NativeJson.DeserializeUtf8<IndexDiagnosticPathSegment>("""{"kind":"index","index":1e0}"""u8).Index == 1,
            "Native integral floating index spelling was rejected.");
        using var rejected = JsonDocument.Parse(Fixture("negative-diagnostic-paths.json"));
        foreach (var segment in rejected.RootElement.EnumerateArray())
            Reject(() => NativeJson.DeserializeUtf8<DiagnosticPathSegment>(Encoding.UTF8.GetBytes(segment.GetRawText())));
    }

    [Test]
    public void GoldenInvalidEnvelopesFailLocally()
    {
        using var cases = JsonDocument.Parse(Fixture("negative-prepared.json"));
        foreach (var example in cases.RootElement.EnumerateArray())
        {
            try { Reject(() => PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(example.GetProperty("value").GetRawText()))); }
            catch (InvalidOperationException) { throw new InvalidOperationException("Accepted negative fixture: " + example.GetProperty("name").GetString()); }
        }
    }

    [Test]
    public void ImportPreservesBytesAndOwnsItsImmutableSnapshot()
    {
        var node = JsonNode.Parse(Fixture("prepared.json"))!;
        node["submission"]!["initialInput"] = JsonNode.Parse("""{"z":"must-not-be-retained","a":[true,null,3]}""");
        node["submission"]!["environment"] = JsonNode.Parse("""{"setup":"sensitive-script","variables":{"PUBLIC":"authored"}}""");
        var reordered = " \r\n{ \"submission\" : " + node["submission"]!.ToJsonString() + ", \"runId\":\"018f5e78-7f95-7c22-8d98-3f15af20c991\" }\t\n";
        var input = Encoding.UTF8.GetBytes(reordered);
        var expected = input.ToArray();
        var prepared = PreparedSubmission.ImportUtf8(input);
        Array.Fill(input, (byte)'x');
        var export = prepared.ExportUtf8();
        Check(export.SequenceEqual(expected), "Import changed retained bytes.");
        Array.Fill(export, (byte)'x');
        Check(prepared.ExportUtf8().SequenceEqual(expected), "Export exposed mutable retained storage.");
        Check(prepared.Submission.Source.Value!.Revision.Value == new string('a', 40), "Source changed.");
        Check(prepared.Submission.SubmissionKey.Value == "debug-fixture", "Key changed.");
        Check(prepared.Submission.InitialInput.GetProperty("z").GetString() == "must-not-be-retained", "Input changed.");
        Check(!prepared.ToString().Contains("must-not-be-retained") && !prepared.Submission.ToString().Contains("must-not-be-retained"), "Default formatting exposed payload.");
        PreparedSubmission created;
        using (var authored = JsonDocument.Parse("""{"private":"content"}"""))
            created = PreparedSubmission.Create(prepared.RunId, prepared.Submission with { InitialInput = authored.RootElement });
        Check(created.Submission.InitialInput.GetProperty("private").GetString() == "content", "Prepared content depended on caller document lifetime.");
        var modified = created.Submission with { Title = new RunTitle("changed") };
        Check(created.Submission.Title.Value != modified.Title.Value, "With expression mutated prepared content.");
    }

    [Test]
    public void OmissionExplicitNullAndOpenAuthoredValuesSurvive()
    {
        var original = PreparedSubmission.ImportUtf8(Fixture("prepared.json"));
        Check(!original.Submission.Environment.HasValue, "Omitted environment became present.");
        var explicitNull = PreparedSubmission.Create(original.RunId, original.Submission with { Environment = new Optional<RuntimeEnvironment?>(null) });
        Check(explicitNull.Submission.Environment.HasValue && explicitNull.Submission.Environment.Value is null, "Null became omitted.");
        var empty = PreparedSubmission.Create(original.RunId, original.Submission with { Environment = new RuntimeEnvironment() });
        Check(empty.Submission.Environment.Value is not null, "Empty environment became null.");
        var raw = JsonNode.Parse(Fixture("prepared.json"))!;
        raw["submission"]!["initialInput"] = JsonNode.Parse("""{"kind":"unknown","credentials":{"callerAuthored":"sensitive"},"future":null}""");
        _ = PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(raw.ToJsonString()));
        // Execution/admission validation belongs to native, including reserved environment names.
        _ = NativeJson.DeserializeUtf8<RuntimeEnvironment>("""{"variables":{"PATH":"authored"}}"""u8);
    }

    [Test]
    public void UnicodeAndNativeNumericBoundaries()
    {
        var run = PreparedSubmission.ImportUtf8(Fixture("prepared.json"));
        _ = PreparedSubmission.Create(run.RunId, run.Submission with { Title = new RunTitle(string.Concat(Enumerable.Repeat("😀", 256))) });
        var graph = NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(JsonDocument.Parse(Fixture("graphs.json")).RootElement[0].GetRawText()));
        var leaf = (StepNode)graph.Root;
        _ = NativeJson.DeserializeUtf8<StepNode>(NativeJson.SerializeUtf8(leaf));
        Reject(() => NativeJson.SerializeUtf8(new RuntimeEnvironment { Setup = "\ud800" }));
        _ = NativeJson.SerializeUtf8(graph with { Root = leaf with { Instructions = new NodeInstructions(new string('é', 8192)), Attempts = new PositiveInteger(Generation.Maximum) } });
        Reject(() => NativeJson.DeserializeUtf8<NodeInstructions>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string('é', 8193)))));
        Reject(() => NativeJson.DeserializeUtf8<ConnectionKey>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string('é', 65)))));
        _ = NativeJson.DeserializeUtf8<ConnectionKey>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string('é', 64))));
        Check(NativeJson.DeserializeUtf8<PositiveInteger>("1e0"u8).Value == 1, "Native integral float spelling rejected.");
        Reject(() => NativeJson.DeserializeUtf8<PositiveInteger>("9007199254740992"u8));
        Reject(() => NativeJson.DeserializeUtf8<Generation>("-1"u8));
        Reject(() => NativeJson.DeserializeUtf8<Generation>("1.5"u8));
        Reject(() => NativeJson.DeserializeUtf8<ByteLength>("\"1\""u8));
        Check(NativeJson.DeserializeUtf8<RequestId>("-9223372036854775808"u8).Number == long.MinValue, "Signed correlation ID boundary lost.");
        Reject(() => NativeJson.DeserializeUtf8<RequestId>("9223372036854775808"u8));
        Reject(() => NativeJson.DeserializeUtf8<RequestId>("1e0"u8));
        _ = NativeJson.DeserializeUtf8<RunId>("\"\""u8); // Generic RunId is not the target UUIDv7 boundary.
        Reject(() => PreparedSubmission.ImportUtf8("{\"runId\":\"\\ud800\",\"submission\":{}}"u8));
        var bytes = Fixture("prepared.json");
        var text = Encoding.UTF8.GetString(bytes).Replace("\"test\"", "\"\\ud800\"", StringComparison.Ordinal);
        Reject(() => PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(text)));
        var malformed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"test\"", "\"INVALID\"", StringComparison.Ordinal));
        malformed[Array.IndexOf(malformed, (byte)'I')] = 0xff;
        Reject(() => PreparedSubmission.ImportUtf8(malformed));
    }

    [Test]
    public void PreRenameRunSizesAreRejected()
    {
        foreach (var legacy in new[] { "tiny", "standard" })
        {
            var raw = Encoding.UTF8.GetString(Fixture("prepared.json")).Replace("\"small\"", $"\"{legacy}\"", StringComparison.Ordinal);
            Reject(() => PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(raw)));
            Reject(() => NativeJson.DeserializeUtf8<RunSize>(Encoding.UTF8.GetBytes($"\"{legacy}\"")));
        }
    }

    [Test]
    public void WorkerDescriptorInvariantsFailLocally()
    {
        var worker = JsonNode.Parse(Fixture("worker.json"))!;
        worker["binding"]!["protocol"] = "builtin";
        Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(worker.ToJsonString())));
        worker = JsonNode.Parse(Fixture("worker.json"))!;
        worker["contract"]!["errors"] = new JsonArray("crash");
        Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(worker.ToJsonString())));
        Reject(() => NativeJson.DeserializeUtf8<WorkerOutcome>("""{"status":"error","code":"crash","reason":"policy_denied"}"""u8));
        Reject(() => NativeJson.DeserializeUtf8<PayloadType>("""{"kind":"null","extra":true}"""u8));
        // Escaped tags must receive the same native-only validation as literal spellings.
        Reject(() => NativeJson.DeserializeUtf8<RuntimePlan>("""{"harness":"co\u0064ex","provider":"openai","size":"small","nodes":{"work":{"kind":"ag\u0065nt","model":"ok","connections":{"a":["TOKEN"],"b":["TOKEN"]}}}}"""u8));
        Reject(() => NativeJson.DeserializeUtf8<RuntimePlan>("""{"harness":"codex","provider":"openai","size":"small","nodes":{"work":{"kind":"agent","model":"ok","connections":{"a":["NOT-AN-ENV-NAME"]}}}}"""u8));
        var duplicateErrors = Encoding.UTF8.GetString(Fixture("worker.json")).Replace("\"timeout\"", "\"cr\\u0061sh\"", StringComparison.Ordinal);
        Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(duplicateErrors)));
        foreach (var (path, value) in new[] { ("graphProfiles", "[]"), ("artifactProfile.allowedTypeIds", "[]"), ("artifactProfile.allowedMediaTypes", "[\"text/plain\",\"text/plain\"]"),
            ("binding.protocol", "\"external\\n\"") })
        {
            worker = JsonNode.Parse(Fixture("worker.json"))!;
            var parent = path.Contains('.') ? worker[path.Split('.')[0]]! : worker;
            parent[path.Split('.')[^1]] = JsonNode.Parse(value);
            Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(worker.ToJsonString())));
        }
    }

    [Test]
    public void LoopIterationInputsAndRuntimeLanesRoundTrip()
    {
        // Native 10.12.0 LoopNode.max_iterations_input: a run-input path that replaces maxIterations.
        var graph = JsonNode.Parse(Fixture("graphs.json"))![8]!;
        graph["root"]!["maxIterationsInput"] = new JsonArray("limits", "rounds");
        var spec = NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(graph.ToJsonString()));
        Check(((LoopNode)spec.Root).MaxIterationsInput.Value!.Value.SequenceEqual(["limits", "rounds"]), "The loop input path was lost.");
        Check(JsonNode.DeepEquals(JsonNode.Parse(NativeJson.SerializeUtf8(spec))!["root"]!["maxIterationsInput"], new JsonArray("limits", "rounds")));
        graph["root"]!["maxIterationsInput"] = null;
        Check(((LoopNode)NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(graph.ToJsonString())).Root).MaxIterationsInput is { HasValue: true, Value: null });
        foreach (var invalid in new JsonNode[] { new JsonArray(), new JsonArray([.. Enumerable.Range(0, 65).Select(i => (JsonNode)("f" + i))]), new JsonArray("1st") })
        {
            graph["root"]!["maxIterationsInput"] = invalid;
            Reject(() => NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(graph.ToJsonString())));
        }
        Check(Encoding.UTF8.GetString(NativeSchemas.ExportCompiledIrUtf8()).Contains("\"maxIterationsInput\""), "The compiled IR schema lacks the loop input.");

        // Native 10.12.0 RuntimeLane: an agent node's own harness and provider.
        foreach (var (lane, expected) in new (string, RuntimeLane)[]
        {
            ("""{"harness":"copilot","provider":"github"}""", new CopilotLane { Provider = CopilotProvider.Github }),
            ("""{"harness":"codex","provider":"openai"}""", new CodexLane { Provider = CodexProvider.OpenAi }),
            ("""{"harness":"claude","provider":"anthropic"}""", new ClaudeLane { Provider = ClaudeProvider.Anthropic })
        })
        {
            var wire = $$"""{"kind":"agent","lane":{{lane}},"model":"opaque-model"}""";
            var binding = (AgentBinding)NativeJson.DeserializeUtf8<NodeRuntimeBinding>(Encoding.UTF8.GetBytes(wire));
            Check(binding.Lane.Value == expected && Encoding.UTF8.GetString(NativeJson.SerializeUtf8(binding)) == wire, lane);
        }
        foreach (var invalid in new[]
        {
            """{"kind":"agent","lane":{"harness":"codex","provider":"anthropic"},"model":"opaque-model"}""",
            """{"kind":"agent","lane":{"harness":"claude"},"model":"opaque-model"}""",
            """{"kind":"agent","lane":{"harness":"claude","provider":"anthropic","model":"m"},"model":"opaque-model"}""",
            """{"kind":"git_delivery","connections":{"github":["GITHUB_TOKEN"]},"pullRequestFeedback":"ignore","lane":{"harness":"claude","provider":"anthropic"}}"""
        })
            Reject(() => NativeJson.DeserializeUtf8<NodeRuntimeBinding>(Encoding.UTF8.GetBytes(invalid)));
    }

    [Test]
    public void GitTransportCredentialsAreAClosedRedactedUnion()
    {
        var https = NativeJson.DeserializeUtf8<GitTransportCredentials>("""{"kind":"https","username":"git","password":"PASSWORD-CANARY"}"""u8);
        Check(https is HttpsGitTransportCredentials { Username: "git", Password: "PASSWORD-CANARY" } && !https.ToString().Contains("CANARY"));
        GitTransportCredentials ssh = new SshGitTransportCredentials { PrivateKey = "KEY-CANARY", KnownHosts = "gitlab.example ssh-ed25519 AAAA" };
        Check(Encoding.UTF8.GetString(NativeJson.SerializeUtf8(ssh)) ==
            """{"kind":"ssh","privateKey":"KEY-CANARY","knownHosts":"gitlab.example ssh-ed25519 AAAA"}""" && !ssh.ToString().Contains("CANARY"));
        foreach (var invalid in new[]
        {
            """{"kind":"https","username":"git"}""", """{"kind":"ftp","username":"git","password":"p"}""",
            """{"kind":"https","username":"","password":"p"}""", """{"kind":"https","username":"g\u0000it","password":"p"}""",
            """{"kind":"ssh","privateKey":"k","knownHosts":"h","username":"git"}"""
        })
            Reject(() => NativeJson.DeserializeUtf8<GitTransportCredentials>(Encoding.UTF8.GetBytes(invalid)));
        // Native admits each value as a static connection value: at most 64 KiB of UTF-8, fewer than the schema's 65536 characters.
        Reject(() => NativeJson.SerializeUtf8<GitTransportCredentials>(new SshGitTransportCredentials { PrivateKey = new string('é', 40_000), KnownHosts = "h" }));

        var forge = NativeJson.DeserializeUtf8<SourceForgeIdentity>("""{"provider":"gitlab","repository":"group/sub/project"}"""u8);
        Check(forge.Provider == "gitlab" && forge.Repository.Value == "group/sub/project");
        Reject(() => NativeJson.SerializeUtf8(forge with { Provider = new string('é', 100) }));
    }
}
