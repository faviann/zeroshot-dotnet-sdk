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
}
