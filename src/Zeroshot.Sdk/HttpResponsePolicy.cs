using System.Net;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>The refusal body a native HTTP server sends.</summary>
internal enum HttpProblemDialect
{
    /// <summary><see cref="TargetHttpProblem"/>, from target and host-owned APIs.</summary>
    Target,
    /// <summary>A direct target's UI router: its own <c>{code,message}</c> <see cref="UiProblem"/>.</summary>
    UiRouter,
    /// <summary>An OAuth token endpoint's <c>{error}</c>.</summary>
    OAuth
}

/// <summary>
/// How one HTTP operation's response is judged beyond the shared redirect and size rules: which problem dialect a
/// refusal speaks, how large that body may be, and which success status it requires. Transport-neutral limits stay
/// on the <see cref="OperationDescriptor"/>.
/// </summary>
internal sealed record HttpResponsePolicy
{
    public static readonly HttpResponsePolicy Target = new();
    public static readonly HttpResponsePolicy UiRouter = new() { Problems = HttpProblemDialect.UiRouter };
    public static readonly HttpResponsePolicy OAuth = new() { Problems = HttpProblemDialect.OAuth };
    /// <summary>Only 200 is success; any other 2xx is an <see cref="OperationFailureKind.HttpStatus"/> failure.</summary>
    public static readonly HttpResponsePolicy OkOnly = new() { RequiresOk = true };

    public HttpProblemDialect Problems { get; init; }
    /// <summary>A native refusal-body bound below the shared diagnostic ceiling.</summary>
    public int? ProblemBytes { get; init; }
    /// <summary>Refusals carry the closed native run-history code, read in either dialect as a <see cref="NativeRunHistoryProblem"/>.</summary>
    public bool HistoryCodes { get; init; }
    public bool RequiresOk { get; init; }

    // Native history handlers bound refusals at 64 KiB; the direct UI mount and hosted hosts send the same
    // closed code in their own problem shapes.
    public static HttpResponsePolicy History(HttpProblemDialect problems)
        => new() { Problems = problems, ProblemBytes = 64 * 1024, HistoryCodes = true };

    // A direct target hands every later request on a UI-routed connection to its UI router (native transport.rs
    // serve_connection), so pooled reuse would send control requests there.
    internal bool ClosesConnection => Problems == HttpProblemDialect.UiRouter;

    internal int RefusalBytes(int diagnosticBytes) => Math.Min(diagnosticBytes, ProblemBytes ?? int.MaxValue);

    internal bool Admits(HttpStatusCode status) => !RequiresOk || status == HttpStatusCode.OK;

    /// <summary>The validated problem in this dialect; an invalid one leaves the status as the only observed refusal.</summary>
    internal NativeHttpProblem? ReadRefusal(byte[] bytes)
    {
        try
        {
            return Problems switch
            {
                HttpProblemDialect.UiRouter when HistoryCodes => new NativeRunHistoryProblem(NativeJson.DeserializeUtf8<UiProblem>(bytes)),
                HttpProblemDialect.UiRouter => new NativeUiProblem(NativeJson.DeserializeUtf8<UiProblem>(bytes)),
                HttpProblemDialect.OAuth => NativeJson.DeserializeUtf8<OAuthErrorResponse>(bytes) is { Known: { } error } body
                    ? new NativeDeviceTokenProblem(body.Error, error) : null,
                _ when HistoryCodes => new NativeRunHistoryProblem(NativeJson.DeserializeUtf8<TargetHttpProblem>(bytes)),
                _ => new NativeTargetProblem(NativeJson.DeserializeUtf8<TargetHttpProblem>(bytes))
            };
        }
        catch (JsonException) { return null; }
    }
}
