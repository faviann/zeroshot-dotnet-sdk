using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Publication of a qualified candidate (README "Publication"). <see cref="Release"/> selects the one package a release
/// tag may push; <see cref="Verify"/> checks what the GitHub feed then serves.
/// </summary>
internal static class Publication
{
    public const string Owner = "faviann";
    public const string Feed = $"https://nuget.pkg.github.com/{Owner}/index.json";
    private const string RepositoryName = "faviann/zeroshot-dotnet-sdk";

    /// <summary>
    /// Copies the qualified library package, and nothing else, to <paramref name="output"/>. Refuses unless the gate
    /// qualified exactly this candidate, the candidate was packed for <paramref name="tag"/>, and the bytes are the
    /// ones the pack job reported.
    /// </summary>
    public static int Release(string artifacts, string tag, string output)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZEROSHOT_CANDIDATE_CLIENT_SHA256")))
            throw new QualificationException("ZEROSHOT_CANDIDATE_CLIENT_SHA256 (the pack job's library hash) is required.");
        var candidate = CandidateFiles.Load(Path.Combine(artifacts, "candidate"));
        var qualificationPath = Path.Combine(artifacts, "qualification", "qualification.json");
        if (!File.Exists(qualificationPath)) throw new QualificationException($"No qualification evidence at {qualificationPath}.");
        var text = File.ReadAllText(qualificationPath);
        if (!JsonFile.Parse<QualificationManifest>(text, qualificationPath).Qualified) throw new QualificationException("The candidate was not qualified.");
        // The JSON itself, not the records read from it: a field either file adds must also differ.
        if (!JsonNode.DeepEquals(JsonNode.Parse(text)!["candidate"], JsonNode.Parse(File.ReadAllText(candidate.ManifestPath))))
            throw new QualificationException("The qualification evidence names another candidate than the downloaded one.");
        if (tag != "v" + candidate.Version || candidate.Manifest.ReleaseTag != tag)
            throw new QualificationException($"Tag {tag} is not the candidate's release tag (version {candidate.Version}, releaseTag {candidate.Manifest.ReleaseTag}).");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new QualificationException($"{output} must be empty.");
        Directory.CreateDirectory(output);
        var selected = Path.Combine(output, Path.GetFileName(candidate.ClientFile));
        File.Copy(candidate.ClientFile, selected);
        if (Tools.Sha256(selected) != candidate.ClientSha256) throw new QualificationException("The copied package differs from the candidate.");
        Console.WriteLine($"Selected {Path.GetFileName(selected)} {candidate.ClientSha256} for {tag}.");
        return 0;
    }

    /// <summary>
    /// After publication: the package must be public and linked to this repository, and a fresh consumer with its own
    /// package cache must restore the exact version from the feed, with credentials only from the environment, and
    /// receive the candidate's bytes. Writes <c>publication.json</c> either way.
    /// </summary>
    /// <remarks>
    /// The workflow's own token can restore a private package linked to this repository, so the restore alone does not
    /// show that other consumers can read it: the visibility check does.
    /// </remarks>
    public static int Verify(string candidateDirectory, string feed, string output)
    {
        var candidate = CandidateFiles.Load(candidateDirectory);
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token)) throw new QualificationException("GITHUB_TOKEN (package read access) is required.");
        var user = Environment.GetEnvironmentVariable("GITHUB_ACTOR") is { Length: > 0 } actor ? actor : Owner;
        var version = candidate.Version;
        var failures = new List<string>();
        if (feed != Feed) failures.Add($"Restored from {feed}, not the publication feed {Feed}.");

        var package = PackageRecord(token, failures);
        var restore = new RestoreSteps();
        var work = Path.Combine(Path.GetTempPath(), "zeroshot-publication-" + Guid.NewGuid().ToString("N"));
        try { Restore(candidate, feed, user, token, work, restore, failures); }
        catch (QualificationException failure) { failures.Add(failure.Message); }

        JsonFile.Write(Path.Combine(output, "publication.json"), new PublicationManifest(failures.Count == 0, "Zeroshot.Client", version, candidate.Commit, feed,
            candidate.ClientSha256, package, restore, Tools.WorkflowRun, DateTimeOffset.UtcNow.ToString("O"), failures));
        foreach (var failure in failures) Console.Error.WriteLine($"Refused: {failure}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>The registry's own record of the package: its visibility and the repository it is linked to.</summary>
    private static object PackageRecord(string token, List<string> failures)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("zeroshot-dotnet-qualification");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var url = $"https://api.github.com/users/{Owner}/packages/nuget/Zeroshot.Client";
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            failures.Add($"GET {url} returned {(int)response.StatusCode}: {body.Trim()}");
            return new RegistryRefusal((int)response.StatusCode);
        }
        var record = JsonNode.Parse(body)!.AsObject();
        var visibility = (string?)record["visibility"];
        var repository = (string?)record["repository"]?["full_name"];
        if (visibility != "public")
            failures.Add($"The package is {visibility}, not public: the owner changes its visibility in the package settings, then re-runs this job.");
        if (repository != RepositoryName) failures.Add($"The package is linked to {repository ?? "no repository"}, not {RepositoryName}.");
        return new RegistryPackage(visibility, repository, (string?)record["html_url"]);
    }

    /// <summary>
    /// A fresh consumer outside the repository: package source mapping takes Zeroshot packages only from the feed, the
    /// feed credential comes from the environment, and the package and HTTP caches start empty.
    /// </summary>
    private static void Restore(CandidateFiles candidate, string feed, string user, string token, string work, RestoreSteps restore, List<string> failures)
    {
        var version = candidate.Version;
        var consumer = Directory.CreateDirectory(Path.Combine(work, "consumer")).FullName;
        var packages = Path.Combine(work, "packages");
        File.WriteAllText(Path.Combine(consumer, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="github" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="github"><package pattern="Zeroshot.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var project = File.ReadAllText("examples/ContractsConsumer/ContractsConsumer.csproj");
        var pinned = Regex.Replace(project, """Include="Zeroshot.Client" Version="[^"]*" """.Trim(), $"""Include="Zeroshot.Client" Version="[{version}]" """.Trim());
        if (pinned == project) throw new QualificationException("ContractsConsumer has no Zeroshot.Client reference to pin.");
        File.WriteAllText(Path.Combine(consumer, "ContractsConsumer.csproj"), pinned);
        File.Copy("examples/ContractsConsumer/Program.cs", Path.Combine(consumer, "Program.cs"));
        File.Copy("global.json", Path.Combine(consumer, "global.json"));
        var environment = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = packages,
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(work, "http-cache"),
            ["NuGetPackageSourceCredentials_github"] = $"Username={user};Password={token}",
        };
        var file = Path.Combine(consumer, "ContractsConsumer.csproj");
        foreach (var (step, arguments, record) in new (string, string[], Action<bool>)[]
        {
            ("restore", ["restore", file], ran => restore.Restore = ran),
            ("build", ["build", file, "-c", "Release", "--no-restore"], ran => restore.Build = ran),
            ("run", ["run", "--project", file, "-c", "Release", "--no-build"], ran => restore.Run = ran),
        })
        {
            var (exitCode, _) = Tools.Run(Tools.DotnetHost, arguments, consumer, environment);
            record(exitCode == 0);
            if (exitCode != 0) throw new QualificationException($"The fresh consumer's {step} exited {exitCode}.");
        }

        var directory = Path.Combine(packages, "zeroshot.client", version.ToLowerInvariant());
        var cached = Path.Combine(directory, $"zeroshot.client.{version.ToLowerInvariant()}.nupkg");
        var metadata = Path.Combine(directory, ".nupkg.metadata");
        if (!File.Exists(cached) || !File.Exists(metadata)) throw new QualificationException($"The isolated cache holds no Zeroshot.Client {version}.");
        var served = Tools.Sha256(cached);
        var source = (string?)JsonNode.Parse(File.ReadAllText(metadata))!["source"];
        restore.ServedSha256 = served;
        restore.Source = source;
        if (served != candidate.ClientSha256) failures.Add($"The feed served {served}, not the qualified {candidate.ClientSha256}.");
        if (source != feed) failures.Add($"Zeroshot.Client was restored from {source}, not {feed}.");
    }
}
