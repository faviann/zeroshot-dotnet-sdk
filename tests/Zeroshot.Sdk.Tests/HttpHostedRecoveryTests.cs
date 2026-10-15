using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Text;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

// Source-backed fixtures from native hosted_runs/recovery_http.rs and contract/hosted_runs.rs; no hosted service evidence.
public sealed class HttpHostedRecoveryTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Run = "run/1";
    private const string Encoded = "run%2F1";
    private const string Successor = "run-2";
    private const string Entry = """{"checkpointId":"opaque/a","sequence":1,"node":"worker","mapIndices":[0],"loopIterations":[],"createdAt":1700000000000}""";
    private const string Page = """{"runId":"run/1","checkpoints":[""" + Entry + """],"nextAfter":"opaque/a"}""";
    private const string Resumed = """{"runId":"run-2","resumedFrom":"run/1"}""";
    private const string Discarded = """{"runId":"run/1","discarded":true}""";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetHostedRunsDiscovery HostedRuns = new()
    {
        Kind = "zeroshot.hosted-runs/v1", BaseUrl = "https://target.example/api/", RouteTemplates = new()
        {
            List = "/runs", Status = "/runs/{run_id}", Watch = "/runs/{run_id}/watch{?from_cursor}",
            Logs = "/runs/{run_id}/logs{?from_cursor,execution}", Force = "/runs/{run_id}/force"
        }
    };
    private static readonly TargetHostedWorkspaceRecoveryDiscovery Capability = new()
    {
        Kind = "openengine.hosted-workspace-recovery/v1", RouteTemplates = new()
        {
            Resume = "/runs/{run_id}/resume", Checkpoints = "/runs/{run_id}/checkpoints", DiscardWorkspace = "/runs/{run_id}/discard-workspace"
        }
    };
    private static TargetDiscoveryDocument Discovery(TargetHostedWorkspaceRecoveryDiscovery recovery)
        => TestDiscovery.Controller(TargetAuthentication.HostedOauth) with { Extensions = new() { HostedRuns = HostedRuns, HostedWorkspaceRecovery = recovery } };
    private static readonly RunCheckpointsParams Checkpoints = new() { RunId = new(Run) };

    // Wire, gate, refusal and formatting rules: CapabilityConformanceTests.
    [Test]
    public async Task EachOperationDecodesItsHostOwnedResult()
    {
        using var handler = new Handler((request, _) =>
        {
            var url = request.RequestUri!.OriginalString;
            return Task.FromResult(Reply(request, url.EndsWith("/checkpoints") ? Page : url.EndsWith("/resume") ? Resumed : Discarded));
        });
        using var client = ClientFor(handler);
        var page = await client.HostedRecovery.CheckpointsAsync(Discovery(Capability), Checkpoints with { After = new("opaque/0"), Limit = 1 }, Hosted);
        var resumed = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted,
            new CheckpointResumeFrom { CheckpointId = new("opaque/a") });
        var restarted = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
        var discarded = await client.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(Run), Hosted);

        Check(page.NextAfter!.Value == "opaque/a" && page.Checkpoints.Single().CheckpointId.Value == "opaque/a");
        Check(resumed is { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "hosted_workspace_recovery.resume" } &&
            resumed.Response!.RunId.Value == Successor && restarted.Outcome == NativeAttemptOutcome.Acknowledged);
        Check(discarded is { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "hosted_workspace_recovery.discard_workspace" } &&
            discarded.Response!.Discarded);
    }

    [Test]
    public async Task RepliesMustNameTheRequestedRunsAndKeepThePageContract()
    {
        var reply = "";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, reply)));
        using var client = ClientFor(handler);
        foreach (var page in new[]
        {
            Page.Replace("\"runId\":\"run/1\"", "\"runId\":\"run-9\""),
            Page.Replace("\"nextAfter\":\"opaque/a\"", "\"nextAfter\":\"opaque/z\""),
            Page.Replace(Entry, Entry + "," + Entry.Replace("opaque/a", "opaque/b").Replace("\"sequence\":1", "\"sequence\":2"))
                .Replace("\"nextAfter\":\"opaque/a\"", "\"nextAfter\":\"opaque/b\"")
        })
        {
            reply = page;
            await Failure(client.HostedRecovery.CheckpointsAsync(Discovery(Capability), Checkpoints with { Limit = 1 }, Hosted),
                NativeHttpFailureKind.Protocol);
        }
        foreach (var foreign in new[] { """{"runId":"run-9","resumedFrom":"run/1"}""", """{"runId":"run-2","resumedFrom":"run-9"}""" })
        {
            reply = foreign;
            var resumed = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
            Check(resumed is { Outcome: NativeAttemptOutcome.Unknown, Response: null, Failure: NativeHttpException { Kind: NativeHttpFailureKind.Protocol } }, foreign);
        }
        reply = """{"runId":"run-9","discarded":true}""";
        var discarded = await client.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(Run), Hosted);
        Check(discarded is { Outcome: NativeAttemptOutcome.Unknown, Response: null });
    }

    [Test]
    public async Task ResultsOverTheNativeHostedBoundFail()
    {
        // Native reads every hosted recovery result under 64 KiB.
        using var large = new Handler((request, _) => Task.FromResult(Reply(request,
            request.RequestUri!.OriginalString.EndsWith("/checkpoints") ? Page + new string(' ', 64 * 1024) : Resumed + new string(' ', 64 * 1024))));
        using var bounded = ClientFor(large);
        var oversized = await bounded.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
        Check(oversized is { Outcome: NativeAttemptOutcome.Unknown, Failure: NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit } });
        await Failure(bounded.HostedRecovery.CheckpointsAsync(Discovery(Capability), Checkpoints, Hosted), NativeHttpFailureKind.SizeLimit);
    }

    [Test]
    public async Task ResumeWithGitCredentialsNeedsTheGitTransportMarker()
    {
        var bodies = new List<JsonNode>();
        using var handler = new Handler(async (request, token) =>
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsByteArrayAsync(token))!);
            return Reply(request, Resumed);
        });
        using var client = ClientFor(handler);
        var fresh = new TargetRunCredentials
        {
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty,
            GitCredentials = new HttpsGitTransportCredentials { Username = "git", Password = "PASSWORD-CANARY" }
        };
        TargetDiscoveryDocument With(TargetGitTransportDiscovery? git)
            => Discovery(Capability) with { Extensions = Discovery(Capability).Extensions with { GitTransport = git } };
        foreach (var marker in new TargetGitTransportDiscovery?[] { null, new() { Kind = "openengine.git-transport/v2" } })
            await Invalid(() => client.HostedRecovery.ResumeAsync(With(marker), new(Run), new(Successor), Hosted, runCredentials: fresh), "PASSWORD-CANARY");
        Check(handler.Calls == 0, "Git credentials were sent to a target without the marker.");
        var resumed = await client.HostedRecovery.ResumeAsync(With(new() { Kind = "openengine.git-transport/v1" }), new(Run), new(Successor), Hosted, runCredentials: fresh);
        Check(resumed.Outcome == NativeAttemptOutcome.Acknowledged && JsonNode.DeepEquals(bodies.Single(),
            JsonNode.Parse("""{"runId":"run/1","successorRunId":"run-2","gitCredentials":{"kind":"https","username":"git","password":"PASSWORD-CANARY"}}""")));
    }
}
