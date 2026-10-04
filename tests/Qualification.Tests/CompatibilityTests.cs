namespace Qualification.Tests;

public sealed class CompatibilityTests
{
    private const string Kept = "Zeroshot.Native.OecpConnection.DisposeAsync() -> System.Threading.Tasks.ValueTask";
    private const string Dropped = "Zeroshot.Native.OecpConnection.InitializeAsync(Zeroshot.Native.Contracts.InitializeParams? parameters = null) -> System.Threading.Tasks.Task!";
    private const string Replacement = "Zeroshot.Native.OecpConnection.InitializeAsync(Zeroshot.Native.OecpRequest? request = null) -> System.Threading.Tasks.Task!";

    // The PublicAPI files of a tree that recorded a removal but has not yet had its release commit.
    private static readonly string[] Shipped = ["#nullable enable", Kept, Dropped];
    private static readonly string[] Unshipped = ["#nullable enable", "*REMOVED*" + Dropped, Replacement];

    private static readonly List<string> BaselineEntries = ["api " + Kept, "api " + Dropped];

    private static CompatibilityBaseline Published(string version) => new("published", version, Tag: "v" + version);

    private static Task<Compatibility> Check(string version, string native, string baseline, List<string> candidate)
        => Task.FromResult(Candidate.Check(version, native, Published(baseline), BaselineEntries, candidate));

    [Test]
    public async Task ARecordedRemovalIsNotDeclared()
    {
        await Assert.That(Candidate.DeclaredApi(Shipped, Unshipped)).IsEquivalentTo(["api " + Kept, "api " + Replacement]);
    }

    [Test]
    public async Task VersionsOrderNumericallyPartByPartWithLegacyPrereleasesBelowMirroredReleases()
    {
        string[] ordered = ["0.1.0-preview.1", "0.2.0-preview.1", "10.9.1.2", "10.10.0-rc.1", "10.10.0", "10.10.0.1-preview.1", "10.10.0.1-preview.2", "10.10.0.1-preview.10", "10.10.0.1", "10.10.0.2", "10.10.0.10", "10.11.0.1"];
        await Assert.That(string.Join(" < ", ordered.Reverse().OrderBy(PackageVersion.Parse))).IsEqualTo(string.Join(" < ", ordered));
        // NuGet semantics: a missing fourth part is 0.
        await Assert.That(PackageVersion.Parse("10.10.0").CompareTo(PackageVersion.Parse("10.10.0.0"))).IsEqualTo(0);
    }

    [Test]
    public async Task TheBaselineIsTheLatestEarlierReleaseTag()
    {
        string[] tags = ["v0.1.0-preview.1", "v0.2.0-preview.1", "vnext", "v10.10.0.1", "v10.10.0.2"];
        await Assert.That(Candidate.Baseline(tags[..3], "10.10.0.1")).IsEqualTo("v0.2.0-preview.1");
        await Assert.That(Candidate.Baseline(tags, "10.10.0.2")).IsEqualTo("v10.10.0.1");
        await Assert.That(Candidate.Baseline(tags, "10.11.0.1")).IsEqualTo("v10.10.0.2");
        await Assert.That(Candidate.Baseline(tags, "0.1.0-preview.1")).IsNull();
    }

    [Test]
    public async Task APreviewFollowsTheLegacyReleasesAndPrecedesItsRevision()
    {
        string[] tags = ["v0.2.0-preview.1", "v10.10.0.1-preview.1", "v10.10.0.1-preview.2"];
        await Assert.That(Candidate.Baseline(tags[..1], "10.10.0.1-preview.1")).IsEqualTo("v0.2.0-preview.1");
        await Assert.That(Candidate.Baseline(tags, "10.10.0.1-preview.2")).IsEqualTo("v10.10.0.1-preview.1");
        await Assert.That(Candidate.Baseline(tags, "10.10.0.1")).IsEqualTo("v10.10.0.1-preview.2");
    }

    [Test]
    public async Task ABaselineBindsTheNativeReleaseItMirrorsOrThatTheLegacyTableRecords()
    {
        await Assert.That(Candidate.NativeOf("0.1.0-preview.1")).IsEqualTo("10.9.0");
        await Assert.That(Candidate.NativeOf("0.2.0-preview.1")).IsEqualTo("10.10.0");
        await Assert.That(Candidate.NativeOf("10.11.2.3")).IsEqualTo("10.11.2");
        await Assert.That(Candidate.NativeOf("10.10.0.1-preview.1")).IsEqualTo("10.10.0");
        await Assert.ThrowsAsync<QualificationException>(() => Task.FromResult(Candidate.NativeOf("10.10.0.1-rc.1")));
        await Assert.ThrowsAsync<QualificationException>(() => Task.FromResult(Candidate.NativeOf("0.3.0-preview.1")));
    }

    [Test]
    [Arguments("10.10.0.0", "has revision 0")]
    [Arguments("10.10.0", "is not <native major>")]
    [Arguments("10.10.0.1-rc.1", "is not <native major>")]
    [Arguments("10.10.0.1-preview.0", "is not <native major>")]
    [Arguments("10.10.0.1-preview", "is not <native major>")]
    [Arguments("10.10.0-preview.1", "is not <native major>")]
    [Arguments("10.10.0.0-preview.1", "has revision 0")]
    [Arguments("10.9.1.1", "does not mirror the pinned native 10.10.0")]
    [Arguments("10.10.1.1", "does not mirror the pinned native 10.10.0")]
    public async Task OnlyAMirroredVersionOfThePinnedNativeReleaseQualifies(string version, string reason)
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check(version, "10.10.0", "0.2.0-preview.1", ["api " + Kept, "api " + Dropped]));
        await Assert.That(refusal!.Message).Contains(reason);
    }

    [Test]
    public async Task TheFirstMirroredReleaseIsASameNativeStepFromTheLastLegacyRelease()
    {
        var result = await Check("10.10.0.1", "10.10.0", "0.2.0-preview.1", ["api " + Kept, "api " + Dropped]);
        await Assert.That(result.BaselineNative).IsEqualTo("10.10.0");
        await Assert.That(result.Missing).IsEmpty();
    }

    [Test]
    public async Task APreviewOfTheFirstMirroredReleaseKeepsAnUnchangedContract()
    {
        var result = await Check("10.10.0.1-preview.1", "10.10.0", "0.2.0-preview.1", ["api " + Kept, "api " + Dropped]);
        await Assert.That(result.BaselineNative).IsEqualTo("10.10.0");
        await Assert.That(result.Missing).IsEmpty();
    }

    [Test]
    public async Task ARevisionOfTheSameNativeReleaseWithARecordedRemovalIsRefused()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check("10.10.0.2", "10.10.0", "10.10.0.1", Candidate.DeclaredApi(Shipped, Unshipped)));
        await Assert.That(refusal!.Message).IsEqualTo("1 published baseline entries (above) are missing; native 10.10.0 revisions must keep them.");
    }

    // A preview promised no compatibility, so the first mirrored release may break it, but still only with notes.
    [Test]
    public async Task ARemovalFromAPreviewBaselineNeedsMigrationNotesForTheSameNativeRelease()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check("10.10.0.1", "10.10.0", "0.2.0-preview.1", Candidate.DeclaredApi(Shipped, Unshipped)));
        await Assert.That(refusal!.Message).IsEqualTo("Breaking changes (above) need migration notes at docs/migration/10.10.0.md.");
    }

    // Every preview of a revision, and the revision itself, may break the preview before it, with notes.
    [Test]
    [Arguments("10.10.0.1-preview.1", "0.2.0-preview.1")]
    [Arguments("10.10.0.1-preview.2", "10.10.0.1-preview.1")]
    [Arguments("10.10.0.1", "10.10.0.1-preview.3")]
    public async Task ARemovalFromAMirroredPreviewBaselineNeedsMigrationNotes(string version, string baseline)
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check(version, "10.10.0", baseline, Candidate.DeclaredApi(Shipped, Unshipped)));
        await Assert.That(refusal!.Message).IsEqualTo("Breaking changes (above) need migration notes at docs/migration/10.10.0.md.");
    }

    [Test]
    public async Task APreviewOfALaterRevisionKeepsTheReleasedContract()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check("10.10.0.2-preview.1", "10.10.0", "10.10.0.1", Candidate.DeclaredApi(Shipped, Unshipped)));
        await Assert.That(refusal!.Message).IsEqualTo("1 published baseline entries (above) are missing; native 10.10.0 revisions must keep them.");
    }

    [Test]
    public async Task ALaterNativeReleaseWithARecordedRemovalNeedsMigrationNotes()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check("10.11.0.1", "10.11.0", "10.10.0.3", Candidate.DeclaredApi(Shipped, Unshipped)));
        await Assert.That(refusal!.Message).IsEqualTo("Breaking changes (above) need migration notes at docs/migration/10.11.0.md.");
    }

    [Test]
    public async Task ANativeReleaseEarlierThanTheBaselinesIsRefused()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check("10.9.1.1", "10.9.1", "0.2.0-preview.1", ["api " + Kept, "api " + Dropped]));
        await Assert.That(refusal!.Message).Contains("precedes native 10.10.0 of its baseline 0.2.0-preview.1");
    }

    [Test]
    public async Task TheFirstReleaseForANewNativeReleaseIsRevisionOne()
    {
        var refusal = await Assert.ThrowsAsync<QualificationException>(() => Check("10.11.0.3", "10.11.0", "10.10.0.2", ["api " + Kept, "api " + Dropped]));
        await Assert.That(refusal!.Message).StartsWith("Version 10.11.0.3 is the first release for").And.EndsWith("its revision must be 1.");
    }
}
