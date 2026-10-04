using System.Runtime.CompilerServices;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>
/// One route a capability descriptor compiles: whether it addresses one whole ID segment, and its exact query suffix. A route
/// with neither is literal, which accepts exactly what native compile_literal_route accepts.
/// </summary>
internal sealed record CapabilityRoute<TRoutes>(Func<TRoutes, string> Template, bool Addressed = false, string? Query = null);

internal static class HttpCapability
{
    // Native refuses direct targets; only hosted OAuth discovery can carry host-owned capabilities.
    internal static void Hosted(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(credentials);
        NativeClient.Admit(discovery, d => d.Authentication == TargetAuthentication.HostedOauth &&
            credentials.Authentication == TargetAuthentication.HostedOauth, "Hosted operations require hosted OAuth discovery and matching credentials.");
    }
}

/// <summary>
/// One discovered HTTP capability, compiled as native's build_*_descriptor compiles it and always in this order: the
/// discovery gate, the advertised kind and the capability's own wire rules, its base URL, then every declared route before
/// any is used. Only a compiled descriptor fills the route a call selects.
/// </summary>
/// <param name="compatible">The advertised kind and any wire rule of the capability's own.</param>
/// <param name="variable">The one variable segment the capability's addressed routes declare.</param>
/// <param name="within">
/// The descriptor native compiles this capability within, compiled whole after the gate; its base URL replaces <paramref name="baseUrl"/>.
/// </param>
internal sealed class HttpCapability<TWire, TRoutes>(
    Action<TargetDiscoveryDocument, TargetControlCredentials?> gate, Func<TargetDiscoveryExtensions, TWire?> advertised,
    Func<TWire, bool> compatible, Func<TWire, string>? baseUrl, Func<TWire, TRoutes> templates, CapabilityRoute<TRoutes>[] routes,
    string missing, string incompatible, string variable = "{run_id}",
    Func<Uri, TargetDiscoveryDocument, TargetControlCredentials?, Uri>? within = null)
    where TWire : class
{
    internal CapabilityDescriptor<TRoutes> Compile(Uri origin, TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
    {
        gate(discovery, credentials);
        var inherited = within?.Invoke(origin, discovery, credentials);
        // Native refuses a missing or foreign capability descriptor before compiling any of its routes.
        var wire = advertised(discovery.Extensions) ?? throw new ArgumentException(missing);
        if (!compatible(wire)) throw new ArgumentException(incompatible);
        var descriptor = new CapabilityDescriptor<TRoutes>(inherited ?? NativeRoutes.CapabilityBaseUrl(origin, baseUrl!(wire)), templates(wire), variable);
        foreach (var route in routes)
            _ = NativeRoutes.RunIdPath(route.Template(descriptor.Templates), route.Addressed, route.Query, variable);
        return descriptor;
    }

    // For a capability native compiles within this one's descriptor.
    internal Uri Base(Uri origin, TargetDiscoveryDocument discovery, TargetControlCredentials? credentials)
        => Compile(origin, discovery, credentials).BaseUrl;
}

/// <summary>A compiled capability: its base URL and route templates, every one of which compiled.</summary>
internal sealed record CapabilityDescriptor<TRoutes>(Uri BaseUrl, TRoutes Templates, string Variable)
{
    // Native pushes the ID as one path segment; an ID its segment setter would drop or rewrite cannot address the route.
    internal Uri Url(CapabilityRoute<TRoutes> route, RunId? id = null, (string Name, string? Value)[]? query = null,
        [CallerArgumentExpression(nameof(id))] string? idName = null)
    {
        if (id is not null && !NativeRoutes.IsAddressableSegment(id.Value))
            throw new ArgumentException("Native cannot address this ID as one route segment.", idName);
        return NativeRoutes.VariableRoute(BaseUrl, route.Template(Templates), Variable, id?.Value, route.Query, query ?? []);
    }
}
