using System.Text;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>Hosted user/org connection records at routes advertised by explicitly supplied discovery.</summary>
public sealed class NativeConnectionsClient
{
    internal const string Kind = "zeroshot.connections/v1";
    private const int MaxBytes = 64 * 1024;
    private static readonly HttpBinding<ConnectionListResult> List = new(
        new("connections.list", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore);
    private static readonly HttpBinding<ConnectionMutationResult> Set = new(
        new("connections.set", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    private static readonly HttpBinding<ConnectionDeleteResult> Delete = new(
        new("connections.delete", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    // Native build_connections_descriptor. The run-scoped resolver is a host callback, not a caller operation; its
    // declaration must still be valid.
    private static readonly HttpCapability<TargetConnectionsDiscovery, TargetConnectionRoutes> Capability = new(HttpCapability.Hosted,
        e => e.Connections, w => w.Kind == Kind && w.DynamicKinds.Distinct(StringComparer.Ordinal).Count() == w.DynamicKinds.Length &&
            w.DynamicKinds.All(kind => kind.Length > 0 && Encoding.UTF8.GetByteCount(kind) <= 128 && !kind.Any(char.IsControl)),
        w => w.BaseUrl, w => w.RouteTemplates, Routes.All,
        missing: "The target does not advertise connection management.", incompatible: "Connection discovery is incompatible.");
    private readonly NativeClient client;
    internal NativeConnectionsClient(NativeClient client) => this.client = client;

    /// <summary>Reads secret-free summaries. Failures throw NativeHttpException.</summary>
    public Task<ConnectionListResult> ListAsync(TargetDiscoveryDocument discovery, ConnectionListRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(List, () => Route(discovery, request, credentials, Routes.List), credentials, cancellationToken);

    /// <summary>Stores static values once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionMutationResult>> SetAsync(TargetDiscoveryDocument discovery, ConnectionSetRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Set, () => Route(discovery, request, credentials, Routes.Set), credentials, cancellationToken);

    /// <summary>Deletes one record once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionDeleteResult>> DeleteAsync(TargetDiscoveryDocument discovery, ConnectionDeleteRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Delete, () => Route(discovery, request, credentials, Routes.Delete), credentials, cancellationToken);

    private HttpCall Route(TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials,
        CapabilityRoute<TargetConnectionRoutes> route)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(Capability.Compile(client.Origin, discovery, credentials).Url(route), NativeJson.SerializeUtf8(request));
    }

    private static class Routes
    {
        internal static readonly CapabilityRoute<TargetConnectionRoutes> Resolve = new(r => r.Resolve), List = new(r => r.List),
            Set = new(r => r.Set), Delete = new(r => r.Delete);
        internal static readonly CapabilityRoute<TargetConnectionRoutes>[] All = [Resolve, List, Set, Delete];
    }
}
