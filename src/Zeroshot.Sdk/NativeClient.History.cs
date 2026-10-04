using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativeHistoryClient? history;
    /// <summary>Discovered public run-history reads. No retention guarantee or history SSE route is implied.</summary>
    public NativeHistoryClient History => history ??= new NativeHistoryClient(this);
}

/// <summary>
/// Binds the discovered <c>run_history</c> capability (native controller_authority/history.rs). A
/// direct target serves it through its UI mount, which also answers HEAD for the same routes.
/// </summary>
public sealed class NativeHistoryClient
{
    internal const string Kind = "zeroshot.run-history/v1";
    internal const string AfterQuery = "{?after}";
    // Direct history is served by the target's UI router; hosted history is a host-owned HTTP API. Native
    // TargetRunHistoryTransport sends Accept: application/json with no-store to both.
    private static readonly RunHistoryReads Reads = new(("history.list", "history.detail", "history.page"), HttpProblemDialect.UiRouter,
        ("history.list.head", "history.detail.head", "history.page.head"), HttpProblemDialect.Target, RequestHeaders.Json, sendsInitialCursor: true);
    private const string Incompatible = "Discovery does not advertise compatible run history.";
    // Native build_run_history_descriptor.
    private static readonly HttpCapability<TargetRunHistoryDiscovery, TargetRunHistoryRoutes> Capability = new(Admit,
        e => e.RunHistory, w => w.Kind == Kind, w => w.BaseUrl, w => w.RouteTemplates, Routes.All, missing: Incompatible, incompatible: Incompatible);
    private readonly NativeClient client;

    internal NativeHistoryClient(NativeClient client) => this.client = client;

    /// <summary>Reads one list page, optionally strictly after a canonical UUIDv7 run ID.</summary>
    public Task<RunHistoryList> ListAsync(TargetDiscoveryDocument discovery, RunId? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => Reads.ListAsync(client, after, ListUrl(discovery, credentials), credentials, cancellationToken);

    /// <summary>Reads the admitted definition. Legacy snapshots are accepted and never re-emitted.</summary>
    public Task<RunDefinition> DetailAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => Reads.DefinitionAsync(client, runId, DetailUrl(discovery, credentials), credentials, cancellationToken);

    /// <summary>Reads one bounded page strictly after <paramref name="after"/>, or from <c>v2:0</c>.</summary>
    public Task<HistoryPage> PageAsync(TargetDiscoveryDocument discovery, RunId runId, Cursor? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => Reads.PageAsync(client, runId, after, PageUrl(discovery, credentials), credentials, cancellationToken);

    /// <summary>HEAD for the list route. Native serves it only on the direct target UI mount.</summary>
    public Task<NativeHeadResult> HeadListAsync(TargetDiscoveryDocument discovery, RunId? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => Reads.HeadListAsync(client, after, ListUrl(discovery, credentials), credentials, cancellationToken);

    public Task<NativeHeadResult> HeadDetailAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => Reads.HeadDefinitionAsync(client, runId, DetailUrl(discovery, credentials), credentials, cancellationToken);

    public Task<NativeHeadResult> HeadPageAsync(TargetDiscoveryDocument discovery, RunId runId, Cursor? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => Reads.HeadPageAsync(client, runId, after, PageUrl(discovery, credentials), credentials, cancellationToken);

    private Func<RunId?, HttpCall> ListUrl(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
        => after => Route(discovery, credentials, Routes.List, query: [("after", after?.Value)]);

    private Func<RunId, HttpCall> DetailUrl(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
        => runId => Route(discovery, credentials, Routes.Detail, runId);

    private Func<RunId, Cursor?, HttpCall> PageUrl(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
        => (runId, after) => Route(discovery, credentials, Routes.Page, runId, [("after", after?.Value)]);

    // History has no hosted-only gate: a direct target serves it through its UI mount without credentials.
    private static void Admit(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
        => NativeClient.Admit(discovery, d => d.Authentication != TargetAuthentication.PrivateCapability &&
            (credentials?.Authentication ?? TargetAuthentication.None) == d.Authentication,
            "Discovery and supplied history authority are incompatible.");

    private HttpCall Route(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials,
        CapabilityRoute<TargetRunHistoryRoutes> route, RunId? runId = null, (string, string?)[]? query = null)
        => new(Capability.Compile(client.Origin, discovery, credentials).Url(route, runId, query),
            HostOwned: discovery.Authentication == TargetAuthentication.HostedOauth);

    private static class Routes
    {
        internal static readonly CapabilityRoute<TargetRunHistoryRoutes> List = new(r => r.List, Query: AfterQuery),
            Detail = new(r => r.Detail, true), Page = new(r => r.Page, true, AfterQuery);
        internal static readonly CapabilityRoute<TargetRunHistoryRoutes>[] All = [List, Detail, Page];
    }
}
