using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>
/// A validated refusal body, in the shape the failed operation reads. The variants are closed: target, UI router,
/// run history and device token. Remote facts never appear in default formatting; inspect them explicitly.
/// </summary>
public abstract class NativeHttpProblem
{
    private protected NativeHttpProblem(string code) => Code = code;
    /// <summary>The server's machine-readable refusal code.</summary>
    public string Code { get; }
}

/// <summary>A <see cref="TargetHttpProblem"/> from a target or host-owned API.</summary>
public sealed class NativeTargetProblem : NativeHttpProblem
{
    internal NativeTargetProblem(TargetHttpProblem body) : base(body.Code) => Body = body;
    /// <summary>The validated target problem; its message and details are remote text.</summary>
    public TargetHttpProblem Body { get; }
}

/// <summary>A <see cref="UiProblem"/> from a direct target's UI router, for UI-routed operations.</summary>
public sealed class NativeUiProblem : NativeHttpProblem
{
    internal NativeUiProblem(UiProblem body) : base(body.Code) => Body = body;
    /// <summary>The validated UI problem; its message is remote text.</summary>
    public UiProblem Body { get; }
}

/// <summary>
/// A run-history operation's refusal: a <see cref="UiProblem"/> from the direct UI mount or a
/// <see cref="TargetHttpProblem"/> from a hosted or private-export host, with its closed native category.
/// </summary>
public sealed class NativeRunHistoryProblem : NativeHttpProblem
{
    internal NativeRunHistoryProblem(UiProblem body) : base(body.Code)
        => (Category, Message) = (RunHistoryProblems.Parse(body.Code), body.Message);
    internal NativeRunHistoryProblem(TargetHttpProblem body) : base(body.Code)
        => (Category, Message, Details) = (RunHistoryProblems.Parse(body.Code), body.Message, body.Details);
    /// <summary>The closed native history category of <see cref="NativeHttpProblem.Code"/>, or null for an unknown code.</summary>
    public RunHistoryProblemCode? Category { get; }
    /// <summary>Native text; from the direct UI mount it is bounded only by the operation's problem-body limit.</summary>
    public string Message { get; }
    /// <summary>A target problem's object details; a UI problem has none.</summary>
    public JsonElement? Details { get; }
}

/// <summary>A recognized OAuth <c>{error}</c> refusing a device-token exchange: no tokens were issued.</summary>
public sealed class NativeDeviceTokenProblem : NativeHttpProblem
{
    internal NativeDeviceTokenProblem(string code, DeviceTokenError error) : base(code) => Error = error;
    /// <summary>The typed OAuth error; <see cref="NativeHttpProblem.Code"/> keeps its wire spelling.</summary>
    public DeviceTokenError Error { get; }
}
