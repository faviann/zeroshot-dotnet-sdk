using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class ContractRuleTests
{
    // MergePlanSubmitRequest's own rule reads Runs; an omitted array must still surface as the missing member.
    [Test]
    public void AnOmittedRequiredArrayIsReportedAsMissing()
    {
        var plan = JsonNode.Parse(NativeJson.SerializeUtf8(new MergePlanSubmitRequest
        {
            SubmissionKey = new("plan-key"), Title = new("Release"), ExpiresAt = "2026-09-28T00:00:00Z",
            Source = new() { Repository = new("acme/project"), Branch = new("main") },
            Profile = new() { Scope = RunProfileScope.Org, Name = new("software-change") },
            Runs = [new() { Name = new("build"), InitialInput = JsonDocument.Parse("{}").RootElement.Clone() }]
        }))!.AsObject();
        plan.Remove("runs");
        try { NativeJson.DeserializeUtf8<MergePlanSubmitRequest>(Encoding.UTF8.GetBytes(plan.ToJsonString())); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("A merge plan without runs was accepted.");
    }

    private sealed record Probe : NativeContract
    {
        [JsonPropertyName("note")]
        public required string? Note { get; init; }
        [JsonPropertyName("ok")]
        public required bool Ok { get; init; }
        internal override void Validate() { if (!Ok) throw new JsonException(); }
    }

    // A required nullable member is present when it is null, so the record's rules still run.
    [Test]
    public void RulesRunWhenARequiredNullableMemberIsNull()
    {
        try { JsonSerializer.Deserialize<Probe>("""{"note":null,"ok":false}""", NativeJson.Options); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("A rule was skipped for a null required nullable member.");
    }

    private sealed record Wrapped : NativeContract
    {
        [JsonPropertyName("one")]
        public Optional<RuntimeEnvironment> One { get; init; }
        [JsonPropertyName("many")]
        public ImmutableArray<RuntimeEnvironment> Many { get; init; } = [];
        [JsonPropertyName("maybe")]
        public Optional<RuntimeEnvironment?> Maybe { get; init; }
        [JsonPropertyName("note")]
        public Optional<string> Note { get; init; }
    }

    // Typed decoding accepts an empty declared connection and explicit null for any Optional; native refuses both.
    [Test]
    public void WrappedMembersFollowNativeRules()
    {
        const string empty = """{"connections":{"github":[]}}""";
        JsonSerializer.Deserialize<Wrapped>("""{"one":{},"many":[{}],"maybe":null,"note":"n"}""", NativeJson.Options);
        foreach (var invalid in new[] { $$"""{"one":{{empty}}}""", $$"""{"many":[{},{{empty}}]}""", $$"""{"maybe":{{empty}}}""", """{"one":null}""", """{"note":null}""" })
        {
            try { JsonSerializer.Deserialize<Wrapped>(invalid, NativeJson.Options); }
            catch (JsonException) { continue; }
            throw new InvalidOperationException("Accepted: " + invalid);
        }
    }

    // A handwritten member the resolver does not own escapes both rules.
    [Test]
    public void EveryHandwrittenMemberThatNeedsAMemberRuleHasOne()
    {
        var nullability = new NullabilityInfoContext();
        foreach (var type in typeof(NativeContract).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(NativeContract).IsAssignableFrom(t) && !Pinned(t)))
            foreach (var property in NativeJson.Options.GetTypeInfo(type).Properties)
            {
                var member = nullability.Create((PropertyInfo)property.AttributeProvider!);
                if ((Holds(property.PropertyType) || member.Type.IsGenericType && member.Type.GetGenericTypeDefinition() == typeof(Optional<>) &&
                    member.GenericTypeArguments[0] is { Type.IsValueType: false, ReadState: NullabilityState.NotNull }) && property.CustomConverter is null)
                    throw new InvalidOperationException($"{type.Name}.{property.Name} has no member rule.");
            }

        static bool Pinned(Type type) => type.GetCustomAttribute<WireContractAttribute>() is not null;
        static bool Holds(Type type) => Pinned(type) || type.IsArray && Holds(type.GetElementType()!) || type.IsGenericType && type.GetGenericArguments().Any(Holds);
    }

    // Consumers switch over the variants a union declares; only the SDK may add one. The record copy constructor
    // stays protected, as C# requires of a non-sealed record.
    [Test]
    public void UnionsAreClosedToOutsideDerivation()
    {
        foreach (var type in typeof(NativeContract).Assembly.GetExportedTypes().Where(t => t.IsAbstract && !t.IsSealed))
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if ((constructor.IsPublic || constructor.IsFamily || constructor.IsFamilyOrAssembly)
                    && !constructor.GetParameters().Select(p => p.ParameterType).SequenceEqual([type]))
                    throw new InvalidOperationException($"{type.Name} can be derived outside the SDK.");
    }

    [Test]
    public void ARemoteFreesTheRepositoryIdWhileALegacySourceKeepsOwnerAndName()
    {
        var revision = string.Concat(Enumerable.Repeat("ab", 32));
        var generic = $$"""{"branch":"main","remote":{"connection":"source","forge":{"provider":"gitlab","repository":"group/sub/project"},"url":"https://gitlab.example/group/sub/project.git"},"repository":"gitlab.example/group/sub/project","revision":"{{revision}}"}""";
        var source = NativeJson.DeserializeUtf8<ResolvedSource>(Encoding.UTF8.GetBytes(generic));
        Check(source.Remote.Value is { Url: "https://gitlab.example/group/sub/project.git", Connection.Value.Value: "source", Forge.Value.Provider: "gitlab" });
        Check(source.Revision.Value == revision && Encoding.UTF8.GetString(NativeJson.SerializeUtf8(source)) == generic);

        var legacy = JsonNode.Parse(generic)!.AsObject();
        legacy.Remove("remote");
        Rejects<ResolvedSource>(legacy.ToJsonString());
        legacy["repository"] = "acme/project";
        Check(NativeJson.DeserializeUtf8<ResolvedSource>(Encoding.UTF8.GetBytes(legacy.ToJsonString())).Remote is { HasValue: false });
        Rejects(() => NativeJson.SerializeUtf8(source with { Remote = Optional<ResolvedGitRemote?>.Omitted }));
        Rejects(() => NativeJson.SerializeUtf8(source with { Remote = null }));
        Check(new SourceRepositoryId("gitlab.example/group/sub/project").Value.Length > 0);
        foreach (var invalid in new[] { "", "bad\u0001id", new string('é', 128) })
            Rejects(() => _ = new SourceRepositoryId(invalid));
        foreach (var invalid in new[] { new string('a', 41), new string('A', 40), new string('a', 63) })
            Rejects(() => _ = new SourceRevisionId(invalid));

        foreach (var url in new[] { "ssh://git@gitlab.example:2222/group/project.git", "git@github.com:acme/project.git", "https://[::1]:8443/repo.git" })
            Check(NativeJson.DeserializeUtf8<ResolvedGitRemote>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { url }))).Url == url, url);
        foreach (var url in new[]
        {
            "http://gitlab.example/repo.git", "file:///srv/repo.git", "C:/repos/project", "https://user@gitlab.example/repo.git",
            "https://gitlab.example/repo.git?ref=main", "-oProxyCommand=x:repo", "https://gitlab.example/repo with space",
            "https://gitlab.example/" + new string('é', 2000)
        })
            Rejects<ResolvedGitRemote>(JsonSerializer.Serialize(new { url }));
    }

    private static void Rejects<T>(string json) => Rejects(() => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json)));
    private static void Rejects(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is JsonException or ArgumentException) { return; }
        throw new InvalidOperationException("An invalid native source value was accepted.");
    }
}
