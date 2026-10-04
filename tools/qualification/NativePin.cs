using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Refuses a tracked file that names a native Zeroshot release other than the one native.props pins. A mention is an
/// earlier release from README.md's native compatibility table, as an exact version or revision token, or any version or
/// revision in an unambiguous native context: after "native" or "native Zeroshot", in a release archive name or in an
/// upstream source or release URL. Only the table's own rows and <see cref="Allowed"/> may name an earlier release.
/// </summary>
internal static class NativePin
{
    private const string Table = "README.md";

    /// <summary>Files that legitimately name an earlier binding: the SDK release whose binding, or null for any earlier one.</summary>
    private static readonly (string Path, string? Sdk, string Reason)[] Allowed =
    [
        ("docs/migration/", null, "Migration notes describe the move away from an earlier binding."),
        ("docs/contracts/native-inventory.md", "0.1.0-preview.1", "The interface inventory cites the native sources it was read from."),
        ("tests/Zeroshot.Sdk.Tests/SubmitTests.cs", "0.1.0-preview.1", "Proves a client configured with the previous binding is refused."),
        ("tests/Zeroshot.Sdk.Tests/DashboardContractTests.cs", "0.1.0-preview.1", "Names the stock release a fixture was trimmed from."),
        ("examples/RunHandleConsumer/Program.cs", "0.1.0-preview.1", "Demonstrates that a run declared with the previous binding is refused."),
    ];

    private static readonly Regex Row = new(@"^\| `(?<sdk>[^`]+)` \| (?<version>\d+\.\d+\.\d+) \| `(?<revision>[0-9a-f]{40})` \|$");
    private static readonly Regex Context = new(
        @"(?i:\bnative(?:\s+zeroshot)?|zeroshot-v|/the-open-engine/zeroshot/(?:tree|blob|commit)/|/releases/(?:tag|download)/v)[\s*`:]*(?<value>\d+\.\d+\.\d+|[0-9a-f]{40})(?!\w|\.\d)");

    public static void Check(string version)
    {
        string[] pin = [Required.NativeVersion, Required.NativeSourceRevision];
        var rows = new Dictionary<string, string[]>();
        foreach (var row in File.ReadLines(Table).Select(line => Row.Match(line.TrimEnd('\r'))).Where(row => row.Success))
            if (!rows.TryAdd(row.Groups["sdk"].Value, [row.Groups["version"].Value, row.Groups["revision"].Value]))
                throw new QualificationException($"The native compatibility table in {Table} lists {row.Groups["sdk"].Value} twice.");
        if (!rows.TryGetValue(version, out var current) || !current.SequenceEqual(pin))
            throw new QualificationException($"The native compatibility table in {Table} must list {version} with native {pin[0]} at {pin[1]}.");
        var earlier = rows.Values.SelectMany(row => row).Except(pin).ToHashSet();
        var known = earlier.Select(value => new Regex($@"(?<![\w.]){Regex.Escape(value)}(?!\w|\.\d)")).ToList();

        var stale = new List<string>();
        foreach (var path in Tools.Git("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Contains((byte)0)) continue;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (path == Table && Row.IsMatch(lines[i].TrimEnd('\r'))) continue;
                var mentions = Context.Matches(lines[i]).Select(match => match.Groups["value"].Value)
                    .Concat(known.SelectMany(token => token.Matches(lines[i]).Select(match => match.Value)));
                foreach (var value in mentions.Distinct().Except(pin))
                    if (!Allowed.Any(entry => path.StartsWith(entry.Path, StringComparison.Ordinal)
                        && (entry.Sdk is null ? earlier.Contains(value) : rows.TryGetValue(entry.Sdk, out var row) && row.Contains(value))))
                        stale.Add($"{path}:{i + 1}: {value}");
            }
        }
        if (stale.Count == 0) return;
        foreach (var mention in stale) Console.Error.WriteLine($"- {mention}");
        throw new QualificationException($"{stale.Count} mentions (above) name a native release other than native.props' {pin[0]} at {pin[1]}."
            + " Update them, or allow a legitimate reference to an earlier release in tools/qualification/NativePin.cs.");
    }
}
