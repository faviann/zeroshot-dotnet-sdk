using System.Text.Json.Nodes;

namespace Qualification.Tests;

public sealed class CompatibilityTests
{
    private const string Kept = "Zeroshot.Native.OecpConnection.DisposeAsync() -> System.Threading.Tasks.ValueTask";
    private const string Dropped = "Zeroshot.Native.OecpConnection.InitializeAsync(Zeroshot.Native.Contracts.InitializeParams? parameters = null) -> System.Threading.Tasks.Task!";
    private const string Replacement = "Zeroshot.Native.OecpConnection.InitializeAsync(Zeroshot.Native.OecpRequest? request = null) -> System.Threading.Tasks.Task!";

    // The PublicAPI files of a tree that recorded a removal but has not yet had its release commit.
    private static readonly string[] Shipped = ["#nullable enable", Kept, Dropped];
    private static readonly string[] Unshipped = ["#nullable enable", "*REMOVED*" + Dropped, Replacement];

    private static readonly JsonObject Baseline = new() { ["kind"] = "published", ["version"] = "0.1.0-preview.1", ["tag"] = "v0.1.0-preview.1" };
    private static readonly List<string> BaselineEntries = ["api " + Kept, "api " + Dropped];

    [Test]
    public async Task ARecordedRemovalIsNotDeclared()
    {
        await Assert.That(Candidate.DeclaredApi(Shipped, Unshipped)).IsEquivalentTo(["api " + Kept, "api " + Replacement]);
    }

    [Test]
    public async Task ASameMinorReleaseWithARecordedRemovalIsRefused()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(
            () => Task.FromResult(Candidate.Check("0.1.0-preview.2", Baseline, BaselineEntries, Candidate.DeclaredApi(Shipped, Unshipped))));
        await Assert.That(refusal!.Message).IsEqualTo("1 published baseline entries (above) are missing; 0.1 releases must keep them.");
    }

    [Test]
    public async Task ALaterMinorReleaseWithARecordedRemovalNeedsMigrationNotes()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(
            () => Task.FromResult(Candidate.Check("0.2.0-preview.1", Baseline, BaselineEntries, Candidate.DeclaredApi(Shipped, Unshipped))));
        await Assert.That(refusal!.Message).IsEqualTo("Breaking changes (above) in 0.2 need migration notes at docs/migration/0.2.md.");
    }
}
