using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>
/// One surface's reads of native's run-history contract, which native serves as the public run_history capability, on the
/// dashboard's UI router and as private exports. Each read checks its caller input before the surface routes it, keeps
/// native's response bound, judges refusals as history problems in the surface's dialect, and holds the record to its
/// request: a list strictly after its position, the addressed run's definition, a page continuing its cursor. A surface
/// supplies its operation names, routes, problem dialect, headers and cursor message.
/// </summary>
internal sealed class RunHistoryReads
{
    // Native history/contract.rs bounds a list at 4 MiB and a definition or page at 8 MiB.
    internal const int ListBytes = 4 * 1024 * 1024, RecordBytes = 8 * 1024 * 1024;
    private readonly HttpProblemDialect problems;
    private readonly HttpProblemDialect? hostOwned;
    private readonly RequestHeaders headers;
    private readonly int? requestBytes;
    private readonly string cursorMessage;
    // Native TargetRunHistoryTransport sends an omitted page cursor as v2:0; the dashboard and private exports leave it out.
    private readonly bool sendsInitialCursor;
    private readonly HttpBinding<RunHistoryList>? list;
    private readonly HttpBinding<RunDefinition> definition;
    private readonly HttpBinding<HistoryPage> page;
    private readonly HttpBinding<NativeHeadResult>? headList, headDefinition, headPage;

    internal RunHistoryReads((string? List, string Definition, string Page) reads, HttpProblemDialect problems,
        (string List, string Definition, string Page)? heads = null, HttpProblemDialect? hostOwned = null,
        RequestHeaders headers = RequestHeaders.None, int? requestBytes = null, string cursorMessage = RunHistoryRules.PageCursorMessage,
        bool sendsInitialCursor = false)
    {
        (this.problems, this.hostOwned, this.headers, this.requestBytes, this.cursorMessage, this.sendsInitialCursor) =
            (problems, hostOwned, headers, requestBytes, cursorMessage, sendsInitialCursor);
        list = reads.List is { } name ? Bind<RunHistoryList>(name, ListBytes) : null;
        definition = Bind<RunDefinition>(reads.Definition, RecordBytes, RunHistoryRules.Definition);
        page = Bind<HistoryPage>(reads.Page, RecordBytes);
        if (heads is { } head)
            (headList, headDefinition, headPage) = (Bind<NativeHeadResult>(head.List, ListBytes),
                Bind<NativeHeadResult>(head.Definition, RecordBytes), Bind<NativeHeadResult>(head.Page, RecordBytes));
    }

    /// <summary>Another operation of this surface that native serves with the history handlers.</summary>
    internal HttpBinding<T> Bind<T>(string name, int responseBytes, Action<T, RunId>? identity = null)
        => new(new(name, OperationTransport.Http, requestBytes: requestBytes, responseBytes: responseBytes), headers, identity: identity,
            response: HttpResponsePolicy.History(problems), hostOwned: hostOwned is { } host ? HttpResponsePolicy.History(host) : null);

    internal Task<RunHistoryList> ListAsync(NativeClient client, RunId? after, Func<RunId?, HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => client.ReadAsync(list!, () => ListRoute(after, route), credentials, cancellationToken, validate: runs => RunHistoryRules.List(runs, after));

    internal Task<NativeHeadResult> HeadListAsync(NativeClient client, RunId? after, Func<RunId?, HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => client.HeadAsync(headList!, () => ListRoute(after, route), credentials, cancellationToken);

    internal Task<RunDefinition> DefinitionAsync(NativeClient client, RunId runId, Func<RunId, HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => client.ReadAsync(definition, () => DefinitionRoute(runId, route), credentials, cancellationToken, runId);

    internal Task<NativeHeadResult> HeadDefinitionAsync(NativeClient client, RunId runId, Func<RunId, HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => client.HeadAsync(headDefinition!, () => DefinitionRoute(runId, route), credentials, cancellationToken);

    internal Task<HistoryPage> PageAsync(NativeClient client, RunId runId, Cursor? after, Func<RunId, Cursor?, HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => client.ReadAsync(page, () => PageRoute(runId, after, route), credentials, cancellationToken,
            validate: received => RunHistoryRules.Page(received, after ?? RunHistoryRules.InitialCursor));

    internal Task<NativeHeadResult> HeadPageAsync(NativeClient client, RunId runId, Cursor? after, Func<RunId, Cursor?, HttpCall> route,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
        => client.HeadAsync(headPage!, () => PageRoute(runId, after, route), credentials, cancellationToken);

    /// <summary>The input check of a page read, for another route that reads from a cursor.</summary>
    internal void Require(RunId runId, Cursor? after)
    {
        RunHistoryRules.RequireRunId(runId, nameof(runId));
        if (after is not null) RequireCursor(after, nameof(after));
    }

    internal void RequireCursor(Cursor cursor, string name) => RunHistoryRules.RequireCursor(cursor, name, cursorMessage);

    private static HttpCall ListRoute(RunId? after, Func<RunId?, HttpCall> route)
    {
        if (after is not null) RunHistoryRules.RequireRunId(after, nameof(after));
        return route(after);
    }

    private static HttpCall DefinitionRoute(RunId runId, Func<RunId, HttpCall> route)
    {
        RunHistoryRules.RequireRunId(runId, nameof(runId));
        return route(runId);
    }

    private HttpCall PageRoute(RunId runId, Cursor? after, Func<RunId, Cursor?, HttpCall> route)
    {
        Require(runId, after);
        return route(runId, sendsInitialCursor ? after ?? RunHistoryRules.InitialCursor : after);
    }
}
