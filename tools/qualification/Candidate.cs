using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Builds once, packs the library and the CLI tool from that one build, checks the packages and the compatibility
/// contract, and writes <c>candidate.json</c> beside the two packages. Later jobs only ever receive these bytes.
/// </summary>
internal static class Candidate
{
    private const string ContractFile = "tools/qualification/contract.txt";
    private const string PrototypeFile = "tools/qualification/prototype-contract.txt";
    private const string PrototypeVersion = "0.1.0-preview.1";
    private const string PrototypeDecision = "https://github.com/faviann/zeroshot-dotnet-sdk/issues/7#issuecomment-5852136561";
    private const string ShippedFile = "src/Zeroshot.Sdk/PublicAPI.Shipped.txt";
    private const string UnshippedFile = "src/Zeroshot.Sdk/PublicAPI.Unshipped.txt";
    private const string Removed = "*REMOVED*";

    public static int Pack(string output)
    {
        if (!File.Exists("Zeroshot.sln")) throw new QualificationException("Run from the repository root.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new QualificationException($"{output} must be empty: a candidate is packed exactly once.");
        Directory.CreateDirectory(output);
        var commit = Tools.Git("rev-parse", "HEAD");
        var clean = Tools.Git("status", "--porcelain").Length == 0;
        var tags = Tools.Git("tag", "--points-at", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // One build: the library package and the tool package are both packed from its output. The public API
        // analyzer fails this build if the declared API (the PublicAPI files) differs from the compiled one.
        Tools.Checked(Tools.DotnetHost, ["build", "src/Zeroshot.Cli/Zeroshot.Cli.csproj", "-c", "Release", "--no-incremental", "-p:ContinuousIntegrationBuild=true"]);
        foreach (var project in new[] { "src/Zeroshot.Sdk/Zeroshot.Sdk.csproj", "src/Zeroshot.Cli/Zeroshot.Cli.csproj" })
            Tools.Checked(Tools.DotnetHost, ["pack", project, "-c", "Release", "--no-build", "-p:ContinuousIntegrationBuild=true", "-o", output]);
        var clientPath = Directory.GetFiles(output, "Zeroshot.Client.*.nupkg").Single();
        var cliPath = Directory.GetFiles(output, "Zeroshot.Cli.*.nupkg").Single();

        using var client = ZipFile.OpenRead(clientPath);
        using var cli = ZipFile.OpenRead(cliPath);
        var clientSpec = Nuspec(client);
        var cliSpec = Nuspec(cli);
        var version = Value(clientSpec, "version") ?? throw new QualificationException("The library package has no version.");
        if (Value(cliSpec, "version") != version) throw new QualificationException($"The CLI package version {Value(cliSpec, "version")} differs from the library's {version}.");
        Mirrored(version, Required.NativeVersion);
        foreach (var tag in tags.Where(tag => tag.StartsWith('v')))
            if (tag != "v" + version) throw new QualificationException($"Tag {tag} does not name version {version}.");
        // A tag-triggered run qualifies exactly the release that tag names.
        if (Environment.GetEnvironmentVariable("GITHUB_REF") is { } gitRef && gitRef.StartsWith("refs/tags/v", StringComparison.Ordinal)
            && (gitRef != $"refs/tags/v{version}" || !tags.Contains("v" + version)))
            throw new QualificationException($"{gitRef} does not name version {version} at the checked-out commit.");
        NativePin.Check();
        foreach (var spec in new[] { clientSpec, cliSpec })
        {
            var repository = spec.Elements().Single(e => e.Name.LocalName == "repository");
            if ((string?)repository.Attribute("commit") != commit)
                throw new QualificationException($"{Value(spec, "id")} records commit {(string?)repository.Attribute("commit")}, not {commit}.");
        }

        // The tool runs the very library the library package ships.
        var library = Tools.Sha256(client.GetEntry("lib/net10.0/Zeroshot.Client.dll") ?? throw new QualificationException("The library package has no lib/net10.0/Zeroshot.Client.dll."));
        var bundled = Tools.Sha256(cli.GetEntry("tools/net10.0/any/Zeroshot.Client.dll") ?? throw new QualificationException("The tool package bundles no Zeroshot.Client.dll."));
        if (bundled != library) throw new QualificationException("The tool package's Zeroshot.Client.dll differs from the library package's.");

        var tool = Path.Combine(Path.GetTempPath(), "zeroshot-candidate-tool-" + Guid.NewGuid().ToString("N"));
        foreach (var entry in cli.Entries.Where(e => e.FullName.StartsWith("tools/net10.0/any/", StringComparison.Ordinal)))
            entry.ExtractToFile(Path.Combine(Directory.CreateDirectory(tool).FullName, entry.Name));
        var command = Path.Combine(tool, "zeroshot-dotnet.dll");
        var expectedVersion = $"{version}+{commit}";
        var reported = Tools.Checked(Tools.DotnetHost, [command, "--version"]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!reported.SequenceEqual([$"zeroshot-dotnet {expectedVersion}", $"Zeroshot.Client {expectedVersion}", Required.NativeLine]))
            throw new QualificationException($"zeroshot-dotnet --version reports '{string.Join(" / ", reported)}', not {expectedVersion} / {Required.NativeLine}.");

        // The contract: package metadata and the CLI grammar read from the candidate itself. Any change must be
        // committed to tools/qualification/contract.txt, so it is reviewed rather than discovered.
        var contract = PackageLines(client, clientSpec).Concat(PackageLines(cli, cliSpec)).Concat(CliLines(command))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        File.WriteAllLines(Path.Combine(output, "contract.txt"), contract);
        Compare("tools/qualification/contract.txt", Entries(File.ReadAllLines(ContractFile)), contract);

        var api = DeclaredApi(File.ReadAllLines(ShippedFile), File.ReadAllLines(UnshippedFile));
        var compatibility = Compatibility(version, api.Concat(contract.Where(line => line.StartsWith("cli ", StringComparison.Ordinal))).ToList());

        var manifest = new JsonObject
        {
            ["schema"] = "zeroshot-dotnet/qualification-candidate/v1",
            ["version"] = version,
            ["sourceCommit"] = commit,
            ["worktreeClean"] = clean,
            ["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode?)tag)]),
            ["releaseTag"] = tags.FirstOrDefault(tag => tag == "v" + version),
            ["ref"] = Environment.GetEnvironmentVariable("GITHUB_REF"),
            ["workflowRun"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { } run
                ? $"{Environment.GetEnvironmentVariable("GITHUB_SERVER_URL")}/{Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")}/actions/runs/{run}" : null,
            ["dotnetSdk"] = Tools.Checked(Tools.DotnetHost, ["--version"]).Trim(),
            ["informationalVersion"] = expectedVersion,
            ["packages"] = new JsonObject
            {
                ["client"] = new JsonObject { ["id"] = "Zeroshot.Client", ["file"] = Path.GetFileName(clientPath), ["sha256"] = Tools.Sha256(clientPath), ["librarySha256"] = library },
                ["cli"] = new JsonObject
                {
                    ["id"] = "Zeroshot.Cli", ["file"] = Path.GetFileName(cliPath), ["sha256"] = Tools.Sha256(cliPath),
                    ["commandSha256"] = Tools.Sha256(cli.GetEntry("tools/net10.0/any/zeroshot-dotnet.dll")!), ["bundledLibrarySha256"] = bundled,
                },
            },
            ["compatibility"] = compatibility,
            // Every declared output kind and path; each platform's CLI suites must emit them all and nothing else.
            ["cliOutput"] = new JsonArray([.. contract.Where(line => line.StartsWith("cli output ", StringComparison.Ordinal)).Select(line => (JsonNode?)line)]),
        };
        Tools.WriteJson(Path.Combine(output, "candidate.json"), manifest);
        Console.WriteLine(manifest.ToJsonString(Tools.Indented));
        return 0;
    }

    private static XElement Nuspec(ZipArchive package)
    {
        var entry = package.Entries.Single(e => !e.FullName.Contains('/') && e.Name.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = entry.Open();
        return XDocument.Load(stream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
    }

    private static string? Value(XElement metadata, string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>Identity, license, repository association, target frameworks, dependencies, tool shape and files.</summary>
    private static IEnumerable<string> PackageLines(ZipArchive package, XElement spec)
    {
        var id = Value(spec, "id");
        var prefix = $"package {id}";
        yield return $"{prefix} authors {Value(spec, "authors")}";
        var license = spec.Elements().Single(e => e.Name.LocalName == "license");
        yield return $"{prefix} license {(string?)license.Attribute("type")} {license.Value}";
        yield return $"{prefix} project {Value(spec, "projectUrl")}";
        var repository = spec.Elements().Single(e => e.Name.LocalName == "repository");
        yield return $"{prefix} repository {(string?)repository.Attribute("type")} {(string?)repository.Attribute("url")}";
        if (Value(spec, "readme") is { } readme) yield return $"{prefix} readme {readme}";
        foreach (var type in spec.Descendants().Where(e => e.Name.LocalName == "packageType"))
            yield return $"{prefix} type {(string?)type.Attribute("name")}";
        foreach (var group in spec.Descendants().Where(e => e.Name.LocalName == "group"))
        {
            var framework = (string?)group.Attribute("targetFramework");
            yield return $"{prefix} framework {framework}";
            foreach (var dependency in group.Elements())
                yield return $"{prefix} dependency {framework} {(string?)dependency.Attribute("id")} {(string?)dependency.Attribute("version")}";
        }
        foreach (var entry in package.Entries)
        {
            if (entry.FullName is "[Content_Types].xml" || entry.FullName.StartsWith("_rels/", StringComparison.Ordinal)
                || entry.FullName.StartsWith("package/", StringComparison.Ordinal) || entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)) continue;
            yield return $"{prefix} file {entry.FullName}";
            if (entry.Name == "DotnetToolSettings.xml")
            {
                using var stream = entry.Open();
                foreach (var command in XDocument.Load(stream).Descendants("Command"))
                    yield return $"{prefix} command {(string?)command.Attribute("Name")} {(string?)command.Attribute("EntryPoint")} {(string?)command.Attribute("Runner")}";
            }
        }
    }

    /// <summary>
    /// The CLI contract, read from the packed tool itself: the command grammar the parser enforces (commands, their
    /// positional arguments and accepted options, the options every command accepts and those given alone), the declared
    /// exit codes, the record catalog that the CLI enforces on every cli/v1 record it writes, every versioned file or
    /// record schema the CLI and library declare, and the native binding that configurations and run files must declare.
    /// </summary>
    internal static IEnumerable<string> CliLines(string command)
    {
        var cli = Assembly.LoadFrom(command);
        var contract = cli.GetType("Zeroshot.Cli.CliContract", throwOnError: true)!;
        T Catalog<T>(string name) => (T)contract.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        foreach (var (name, arguments) in Catalog<IReadOnlyDictionary<string, string[]>>("Commands"))
        {
            if (name is not ("" or "*")) yield return $"cli command {name}";
            foreach (var argument in arguments)
                yield return argument.StartsWith("--", StringComparison.Ordinal)
                    ? $"cli option {(name.Length == 0 ? "" : name + " ")}{argument}" : $"cli argument {name} {argument}";
        }
        foreach (var exit in cli.GetType("Zeroshot.Cli.ExitCodes", throwOnError: true)!.GetFields(BindingFlags.Public | BindingFlags.Static))
            if (exit.IsLiteral) yield return $"cli exit {exit.GetRawConstantValue()}";
        foreach (var (kind, fields) in Catalog<IReadOnlyDictionary<string, string[]>>("Records"))
        {
            if (kind != "*") yield return $"cli output {kind}";
            foreach (var field in fields) yield return $"cli output {kind} {field}";
        }
        var library = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(command)!, "Zeroshot.Client.dll"));
        // A cli line, so the compatibility gate treats another native binding as a removed baseline entry.
        var schemas = library.GetType("Zeroshot.Native.NativeSchemas", throwOnError: true)!;
        yield return $"cli native-binding {schemas.GetField("NativeVersion")!.GetValue(null)} {schemas.GetField("SourceRevision")!.GetValue(null)}";
        foreach (var type in cli.GetTypes().Concat(library.GetTypes()))
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (field is { Name: "Schema", IsLiteral: true } && field.GetRawConstantValue() is string schema && schema.StartsWith("zeroshot-dotnet/", StringComparison.Ordinal))
                    yield return $"cli schema {schema}";
    }

    private static bool IsApiLine(string line) => line.Length > 0 && !line.StartsWith('#');

    /// <summary>
    /// The API a PublicAPI file pair declares: the Shipped entries that Unshipped does not mark <c>*REMOVED*</c>, plus the
    /// Unshipped additions. A removal stays in Shipped until the release commit moves the Unshipped entries.
    /// </summary>
    internal static List<string> DeclaredApi(IEnumerable<string> shipped, IEnumerable<string> unshipped)
    {
        var pending = Entries(unshipped);
        var removed = pending.Where(line => line.StartsWith(Removed, StringComparison.Ordinal)).Select(line => line[Removed.Length..]).ToHashSet(StringComparer.Ordinal);
        return [.. Entries(shipped).Where(line => !removed.Contains(line))
            .Concat(pending.Where(line => !line.StartsWith(Removed, StringComparison.Ordinal))).Select(line => "api " + line)];
    }

    private static List<string> Entries(IEnumerable<string> lines) => [.. lines.Select(line => line.Trim()).Where(IsApiLine)];

    private static void Compare(string committed, List<string> expected, List<string> actual)
    {
        var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
        var added = actual.Except(expected, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && added.Count == 0) return;
        foreach (var line in missing) Console.Error.WriteLine($"- {line}");
        foreach (var line in added) Console.Error.WriteLine($"+ {line}");
        throw new QualificationException($"The candidate differs from {committed} (lines above). Commit the candidate's contract.txt if the change is intended.");
    }

    /// <summary>
    /// Every baseline entry must still be present. The baseline is the latest release tag (v<version>) that precedes
    /// the candidate, whose recorded API and CLI contract are read from that tag. Only when no release precedes it is
    /// the baseline the accepted usage prototype. Entries may disappear only with a later native release, and only with
    /// migration notes for it.
    /// </summary>
    private static JsonObject Compatibility(string version, List<string> candidate)
    {
        var releases = Tools.Git("tag", "--list", "v*").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A shallow or tagless clone would silently fall back to the prototype baseline.
        if (releases.Length == 0) Console.WriteLine("::warning::No v* tags are present; fetch tags if any release has been published.");
        var prior = Baseline(releases, version);
        var baseline = prior is null
            ? new JsonObject { ["kind"] = "accepted-prototype", ["version"] = PrototypeVersion, ["contract"] = PrototypeFile, ["decision"] = PrototypeDecision }
            : new JsonObject { ["kind"] = "published", ["version"] = prior[1..], ["tag"] = prior };
        List<string> entries = prior is null
            ? Entries(File.ReadAllLines(PrototypeFile))
            : [.. DeclaredApi(Tools.Git("show", $"{prior}:{ShippedFile}").Split('\n'), Tools.Git("show", $"{prior}:{UnshippedFile}").Split('\n')),
                .. Entries(Tools.Git("show", $"{prior}:{ContractFile}").Split('\n')).Where(line => line.StartsWith("cli ", StringComparison.Ordinal))];
        Console.WriteLine(prior is null ? "Compatibility baseline: the accepted usage prototype (no earlier v* release tag)."
            : $"Compatibility baseline: release tag {prior}.");
        return Check(version, Required.NativeVersion, baseline, entries, candidate);
    }

    /// <summary>The latest <c>v*</c> tag naming a package version that precedes <paramref name="version"/>, or null.</summary>
    internal static string? Baseline(IEnumerable<string> tags, string version)
    {
        var candidate = PackageVersion.Parse(version);
        return tags.Select(tag => (Tag: tag, Version: tag.StartsWith('v') ? PackageVersion.TryParse(tag[1..]) : null))
            .Where(release => release.Version is not null && release.Version.CompareTo(candidate) < 0)
            .OrderBy(release => release.Version).LastOrDefault().Tag;
    }

    /// <summary>
    /// Refuses a candidate that is not a mirrored version of <paramref name="native"/>, binds an earlier native release
    /// than its baseline, starts a new native release above revision 1, or lacks a baseline entry. Only a later native release may drop one, with migration notes.
    /// </summary>
    internal static JsonObject Check(string version, string native, JsonObject baseline, List<string> entries, List<string> candidate)
    {
        Mirrored(version, native);
        var baselineVersion = (string)baseline["version"]!;
        var baselineNative = NativeOf(baselineVersion);
        var order = PackageVersion.Parse(native).CompareTo(PackageVersion.Parse(baselineNative));
        if (order < 0) throw new QualificationException($"Version {version} binds native {native}, which precedes native {baselineNative} of its baseline {baselineVersion}.");
        if (order > 0 && PackageVersion.Parse(version).Revision != 1)
            throw new QualificationException($"Version {version} is the first release for native {native}; its revision must be 1.");
        var missing = entries.Where(entry => !candidate.Any(line => Matches(entry, line))).ToList();
        var notes = $"docs/migration/{native}.md";
        if (missing.Count > 0)
        {
            foreach (var entry in missing) Console.Error.WriteLine($"- {entry}");
            if (order == 0) throw new QualificationException($"{missing.Count} {baseline["kind"]} baseline entries (above) are missing; native {native} revisions must keep them.");
            if (!File.Exists(notes)) throw new QualificationException($"Breaking changes (above) need migration notes at {notes}.");
        }
        return new JsonObject
        {
            ["baseline"] = baseline,
            ["baselineNative"] = baselineNative,
            ["entries"] = entries.Count,
            ["missing"] = new JsonArray([.. missing.Select(entry => (JsonNode?)entry)]),
            ["migrationNotes"] = missing.Count > 0 ? notes : null,
        };
    }

    /// <summary>
    /// A release version mirrors the native release it binds, <c>native major.minor.patch.revision</c>: revision 1 or
    /// later (NuGet normalizes <c>.0</c> away) and no prerelease label.
    /// </summary>
    internal static void Mirrored(string version, string native)
    {
        if (version.Split('.').Length != 4 || PackageVersion.TryParse(version) is not { Prerelease: null } release)
            throw new QualificationException($"Version {version} is not <native major>.<native minor>.<native patch>.<revision> with revision 1 or later"
                + " (NuGet drops a revision 0, and a release carries no prerelease label).");
        if ($"{release.Major}.{release.Minor}.{release.Patch}" != native)
            throw new QualificationException($"Version {version} does not mirror the pinned native {native} (native.props).");
        if (release.Revision < 1) throw new QualificationException($"Version {version} has revision 0; revisions start at 1.");
    }

    /// <summary>
    /// The native release a baseline binds: a mirrored version's first three parts. The two releases before mirroring
    /// are fixed history (README "Native compatibility"); the accepted prototype is 0.1.0-preview.1's.
    /// </summary>
    internal static string NativeOf(string version) => version switch
    {
        "0.1.0-preview.1" => "10.9.0",
        "0.2.0-preview.1" => "10.10.0",
        _ when version.Split('.').Length == 4 && PackageVersion.TryParse(version) is { Prerelease: null } release => $"{release.Major}.{release.Minor}.{release.Patch}",
        _ => throw new QualificationException($"Baseline {version} names no native release."),
    };

    /// <summary>
    /// A published baseline entry is an exact line. A prototype entry names a symbol: it matches any declared API line
    /// for that symbol or its members, whatever modifiers (static, override, ...) precede the name.
    /// </summary>
    private static bool Matches(string entry, string line)
    {
        if (entry == line) return true;
        if (!entry.StartsWith("api ", StringComparison.Ordinal) || !line.StartsWith("api ", StringComparison.Ordinal)) return false;
        var name = entry[4..];
        var start = line.IndexOf("Zeroshot.", StringComparison.Ordinal);
        if (start < 0) return false;
        var core = line[start..];
        return core == name || core.StartsWith(name, StringComparison.Ordinal) && core[name.Length] is '.' or '(' or ' ' or '<';
    }
}

/// <summary>
/// A NuGet package version: three or four numeric parts (a missing fourth is 0) and an optional prerelease label, which
/// sorts before the same numbers without one; labels compare by semantic-version identifier rules.
/// </summary>
internal sealed record PackageVersion(int Major, int Minor, int Patch, int Revision, string? Prerelease) : IComparable<PackageVersion>
{
    private static readonly Regex Shape = new(@"^(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:\.(\d{1,9}))?(?:-([0-9A-Za-z.-]+))?$");

    public static PackageVersion? TryParse(string text) => Shape.Match(text) is { Success: true } match
        ? new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
            match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0, match.Groups[5].Success ? match.Groups[5].Value : null)
        : null;

    public static PackageVersion Parse(string text) => TryParse(text) ?? throw new QualificationException($"'{text}' is not a package version.");

    public int CompareTo(PackageVersion? other)
    {
        if (other is null) return 1;
        if ((Major, Minor, Patch, Revision).CompareTo((other.Major, other.Minor, other.Patch, other.Revision)) is not 0 and var core) return core;
        if (Prerelease is null || other.Prerelease is null) return (Prerelease is null).CompareTo(other.Prerelease is null);
        var (x, y) = (Prerelease.Split('.'), other.Prerelease.Split('.'));
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var (xNumber, yNumber) = (int.TryParse(x[i], out var xn), int.TryParse(y[i], out var yn));
            var order = xNumber && yNumber ? xn.CompareTo(yn) : xNumber != yNumber ? (xNumber ? -1 : 1) : string.CompareOrdinal(x[i], y[i]);
            if (order != 0) return order;
        }
        return x.Length.CompareTo(y.Length);
    }
}
