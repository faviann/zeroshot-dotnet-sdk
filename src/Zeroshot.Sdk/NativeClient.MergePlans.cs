using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>Hosted merge plans at routes advertised by explicitly supplied discovery. No polling, retry or token refresh.</summary>
public sealed class NativeMergePlansClient
{
    internal const string Kind = "zeroshot.merge-plans/v1";
    // Native MAX_MERGE_PLAN_RESPONSE_BYTES bounds every merge-plan result.
    private const int MaxBytes = 1024 * 1024;
    private static readonly HttpBinding<MergePlan> Create = new(
        new("merge_plans.create", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    private static readonly HttpBinding<MergePlan> Status = new(
        new("merge_plans.status", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Json, identity: SamePlan);
    private static readonly HttpBinding<MergePlan> Force = new(
        new("merge_plans.force", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal, SamePlan);
    // Native build_merge_plans_descriptor: its addressed routes declare {plan_id}.
    private static readonly HttpCapability<TargetMergePlansDiscovery, TargetMergePlanRoutes> Capability = new(HttpCapability.Hosted,
        e => e.MergePlans, w => w.Kind == Kind, w => w.BaseUrl, w => w.RouteTemplates, Routes.All,
        missing: "The target does not advertise merge plans.", incompatible: "Merge-plan discovery is incompatible.", variable: "{plan_id}");
    private readonly NativeClient client;
    internal NativeMergePlansClient(NativeClient client) => this.client = client;

    /// <summary>Submits one plan once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<MergePlan>> CreateAsync(TargetDiscoveryDocument discovery, MergePlanSubmitRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Create, () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return new HttpCall(Route(discovery, credentials, Routes.Create), NativeJson.SerializeUtf8(request));
        }, credentials, cancellationToken);

    /// <summary>Reads the plan's current state. Failures throw NativeHttpException.</summary>
    public Task<MergePlan> StatusAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(Status, () => Route(discovery, credentials, Routes.Status, Required(planId)), credentials, cancellationToken, planId);

    /// <summary>Requests a force-stop once; the returned plan need not be terminal. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<MergePlan>> ForceAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Force, () => new HttpCall(Route(discovery, credentials, Routes.Force, Required(planId)), "{}"u8.ToArray()),
            credentials, cancellationToken, planId);

    // A plan answered for another ID is foreign data, never this plan's state.
    private static void SamePlan(MergePlan plan, RunId planId)
    {
        if (plan.PlanId != planId) throw new JsonException();
    }

    private static RunId Required(RunId planId)
    {
        ArgumentNullException.ThrowIfNull(planId);
        return planId;
    }

    private Uri Route(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        CapabilityRoute<TargetMergePlanRoutes> route, RunId? planId = null)
        => Capability.Compile(client.Origin, discovery, credentials).Url(route, planId);

    private static class Routes
    {
        internal static readonly CapabilityRoute<TargetMergePlanRoutes> Create = new(r => r.Create), Status = new(r => r.Status, true),
            Force = new(r => r.Force, true);
        internal static readonly CapabilityRoute<TargetMergePlanRoutes>[] All = [Create, Status, Force];
    }
}
