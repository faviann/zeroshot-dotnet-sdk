using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

// The run-history reads every surface serves (public history direct and hosted, the dashboard, private exports), checked
// from one table: each surface contributes how it calls list, definition and page; each rule a reply and the request it
// answers. Surface files keep their routes and problem dialects; HistoryTests keeps the record decoding.
public sealed class RunHistoryReadTests
{
    public enum Read { List, Definition, Page }
    public enum Outcome { Accepted, Protocol, SizeLimit, Refused, Invalid }

    public sealed record Surface(string Name, Func<NativeClient, RunId?, Task>? List, Func<NativeClient, RunId, Task> Definition,
        Func<NativeClient, RunId, Cursor?, Task> Page, string CursorMessage)
    {
        public override string ToString() => Name;
    }

    /// <summary>One rule: the reply to <paramref name="Read"/> after <paramref name="After"/> on run <paramref name="RunId"/>.</summary>
    public sealed record Rule(string Name, Read Read, Func<string> Reply, Outcome Outcome, string? After = null, string RunId = Run,
        HttpStatusCode Status = HttpStatusCode.OK)
    {
        public override string ToString() => Name;
    }

    private const string Run = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    private const string Bearer = "HISTORY-CREDENTIAL-CANARY";
    private const string PageCursor = "A history page cursor must be canonical v2:<sequence>.";
    private const int MiB = 1024 * 1024;
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/history.json")))!;
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetControlCredentials Private = new(TargetAuthentication.PrivateCapability, Bearer);

    private static TargetDiscoveryDocument History(TargetAuthentication authentication) => TestDiscovery.Controller(authentication) with
    {
        Extensions = new()
        {
            RunHistory = new()
            {
                Kind = NativeHistoryClient.Kind, BaseUrl = "https://target.example/history",
                RouteTemplates = new() { List = "/runs{?after}", Detail = "/runs/{run_id}", Page = "/runs/{run_id}/page{?after}" }
            }
        }
    };

    public static IEnumerable<Surface> Surfaces()
    {
        var direct = History(TargetAuthentication.None);
        var hosted = History(TargetAuthentication.HostedOauth);
        yield return new("history", (n, a) => n.History.ListAsync(direct, a), (n, r) => n.History.DetailAsync(direct, r),
            (n, r, a) => n.History.PageAsync(direct, r, a), PageCursor);
        yield return new("hosted history", (n, a) => n.History.ListAsync(hosted, a, Hosted), (n, r) => n.History.DetailAsync(hosted, r, Hosted),
            (n, r, a) => n.History.PageAsync(hosted, r, a, Hosted), PageCursor);
        yield return new("dashboard", (n, a) => n.Dashboard.ListRunsAsync(a), (n, r) => n.Dashboard.GetRunAsync(r),
            (n, r, a) => n.Dashboard.GetHistoryAsync(r, a), "A history cursor must be canonical v2:<sequence>.");
        // Native exports no run list privately.
        yield return new("private exports", null, (n, r) => n.Private.GetHistoryDefinitionAsync(r, Private),
            (n, r, a) => n.Private.GetHistoryPageAsync(r, Private, a), PageCursor);
    }

    private static string Fixture(string name) => Golden[name]!.ToJsonString();
    private static Func<string> Padded(string name, int bytes) => () => Fixture(name) + new string(' ', bytes);
    private static readonly string Problem = """{"code":"run_not_found","message":"No retained run."}""";

    public static IEnumerable<Rule> Rules()
    {
        // A list holds only runs strictly after the requested position.
        yield return new("list strictly after position", Read.List, () => Fixture("list"), Outcome.Accepted, "0195af77-1000-7000-8000-000000000011");
        yield return new("list entry not after position", Read.List, () => Fixture("list"), Outcome.Protocol, "0195af77-1000-7000-8000-000000000010");
        yield return new("list over 4 MiB", Read.List, Padded("list", 4 * MiB), Outcome.SizeLimit);
        yield return new("definition of the addressed run", Read.Definition, () => Fixture("definition"), Outcome.Accepted);
        yield return new("foreign definition", Read.Definition, () =>
        {
            var definition = Golden["definition"]!.DeepClone();
            definition["runId"] = "0195af77-1000-7000-8000-000000000010";
            return definition.ToJsonString();
        }, Outcome.Protocol);
        yield return new("definition beyond the list bound", Read.Definition, Padded("definition", 5 * MiB), Outcome.Accepted);
        yield return new("definition over 8 MiB", Read.Definition, Padded("definition", 8 * MiB), Outcome.SizeLimit);
        // A page continues its cursor, and an omitted cursor reads from v2:0.
        yield return new("page from v2:0", Read.Page, () => DashboardHistoryTests.Page(0, 9), Outcome.Accepted);
        yield return new("page from a later cursor than requested", Read.Page, () => DashboardHistoryTests.Page(4, 9), Outcome.Protocol);
        yield return new("page continuing its cursor", Read.Page, () => DashboardHistoryTests.Page(4, 9), Outcome.Accepted, "v2:4");
        yield return new("page before its cursor", Read.Page, () => DashboardHistoryTests.Page(0, 9), Outcome.Protocol, "v2:4");
        yield return new("page beyond the list bound", Read.Page, Padded("page", 5 * MiB), Outcome.Accepted);
        yield return new("page over 8 MiB", Read.Page, Padded("page", 8 * MiB), Outcome.SizeLimit);
        // Refusals keep the closed history category, and native bounds their bodies at 64 KiB.
        foreach (var read in Enum.GetValues<Read>())
            yield return new($"{read} refusal", read, () => Problem, Outcome.Refused, Status: HttpStatusCode.NotFound);
        yield return new("refusal over 64 KiB", Read.Page, () => Problem[..^1] + $$""","pad":"{{new string('x', 64 * 1024)}}"}""", Outcome.SizeLimit,
            Status: HttpStatusCode.NotFound);
        // Caller input is checked before anything is sent.
        yield return new("non-UUIDv7 list position", Read.List, () => "{}", Outcome.Invalid, "not-a-run");
        yield return new("non-UUIDv7 run", Read.Definition, () => "{}", Outcome.Invalid, RunId: "018f5e78-7f95-4c22-8d98-3f15af20c991");
        yield return new("non-UUIDv7 page run", Read.Page, () => "{}", Outcome.Invalid, RunId: "018f5e78-7f95-4c22-8d98-3f15af20c991");
        yield return new("non-canonical cursor", Read.Page, () => "{}", Outcome.Invalid, "v2:01");
        yield return new("cursor beyond i64", Read.Page, () => "{}", Outcome.Invalid, "v2:9223372036854775808");
    }

    public static IEnumerable<(Surface Surface, Rule Rule)> Cases()
        => from surface in Surfaces() from rule in Rules() where rule.Read != Read.List || surface.List is not null select (surface, rule);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task EverySurfaceHoldsItsReadsToTheNativeContract(Surface surface, Rule rule)
    {
        var reply = rule.Reply();
        var handler = new Handler((request, _) => Task.FromResult(Reply(request, reply, rule.Status)));
        using var native = ClientFor(handler, new() { MaxErrorBodyBytes = MiB });
        var run = new RunId(rule.RunId);
        Func<Task> call = rule.Read switch
        {
            Read.List => () => surface.List!(native, rule.After is null ? null : new RunId(rule.After)),
            Read.Definition => () => surface.Definition(native, run),
            _ => () => surface.Page(native, run, rule.After is null ? null : new Cursor(rule.After))
        };
        try
        {
            await call();
            Check(rule.Outcome == Outcome.Accepted && handler.Calls == 1, rule.Name);
        }
        catch (ArgumentException error)
        {
            Check(rule.Outcome == Outcome.Invalid && handler.Calls == 0 && !error.ToString().Contains(Bearer), rule.Name);
            var cursor = rule is { Read: Read.Page, After: not null };
            Check(error.ParamName == (cursor || rule.Read == Read.List ? "after" : "runId") &&
                error.Message.StartsWith(cursor ? surface.CursorMessage : "Run history requires a canonical UUIDv7 run ID."), error.Message);
        }
        catch (NativeHttpException error)
        {
            Check(rule.Outcome switch
            {
                Outcome.Protocol => error is { Kind: NativeHttpFailureKind.Protocol, StatusCode: HttpStatusCode.OK },
                Outcome.SizeLimit => error is { Kind: NativeHttpFailureKind.SizeLimit, HistoryProblem: null },
                Outcome.Refused => error is { Kind: NativeHttpFailureKind.HttpStatus, StatusCode: HttpStatusCode.NotFound,
                    HistoryProblem: RunHistoryProblemCode.RunNotFound },
                _ => false
            } && !error.ToString().Contains("No retained run"), $"{rule.Name}: {error}");
        }
    }
}
