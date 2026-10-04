using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// <summary>
/// Qualifies the candidate only if every required platform and the Linux x64 native witness passed for exactly these
/// package bytes. A missing, duplicated, failed or foreign piece of evidence refuses qualification. Writes the combined
/// qualification manifest either way.
/// </summary>
internal static class Gate
{
    public static int Run(string artifacts, string output)
    {
        var failures = new List<string>();
        var candidate = CandidateFiles.Load(Path.Combine(artifacts, "candidate"));
        var identity = candidate.Identity();
        if (!candidate.Manifest.WorktreeClean) failures.Add("The candidate was packed from a modified worktree.");

        var legs = new List<LegEvidence>();
        foreach (var file in Directory.GetFiles(artifacts, "evidence.json", SearchOption.AllDirectories))
            try { legs.Add(JsonFile.Read<LegEvidence>(file)); }
            catch (QualificationException malformed) { failures.Add(malformed.Message); }
        foreach (var leg in legs.Where(leg => !Required.Platforms.Any(platform => platform.Runner == leg.Runner)))
            failures.Add($"Evidence from {leg.Runner}, which is not a required platform.");
        var platforms = new List<PlatformSummary>();
        foreach (var (runner, os, architecture) in Required.Platforms)
        {
            var matching = legs.Where(leg => leg.Runner == runner).ToList();
            if (matching.Count != 1) { failures.Add($"{runner}: {matching.Count} evidence files, expected exactly one."); continue; }
            var leg = matching[0];
            var platform = leg.Platform;
            void Require(bool condition, string failure) { if (!condition) failures.Add($"{runner}: {failure}"); }
            Require(leg.Candidate == identity, "tested other package bytes than the candidate.");
            Require(platform.Os == os, $"ran on {platform.Os}, not {os}.");
            Require(platform.ProcessArchitecture == architecture.ToString() && platform.OsArchitecture == architecture.ToString(),
                $"ran a {platform.ProcessArchitecture} process on {platform.OsArchitecture}, not native {architecture}.");
            Require(platform.RuntimeVersion.StartsWith("10.", StringComparison.Ordinal), $"ran .NET {platform.RuntimeVersion}, not .NET 10.");
            foreach (var name in PlatformLeg.Checks)
                Require(leg.Checks.Count(check => check.Name == name && check.Passed) == 1, $"check {name} did not pass.");
            foreach (var check in leg.Checks.Where(check => check.Detail is not null && PlatformLeg.Suites.Contains(check.Name)))
            {
                try
                {
                    foreach (var skipped in JsonFile.Parse<SuiteSummary>(check.Detail!, $"{runner} {check.Name} detail").SkippedTests)
                        Require(Required.MaySkip(check.Name, os, skipped), $"{check.Name} skipped {skipped}, which must run on {os}.");
                }
                catch (QualificationException malformed) { Require(false, malformed.Message); }
            }
            Require(leg.Passed, "the leg did not pass.");
            platforms.Add(new(runner, platform.Os, platform.OsDetail, platform.ProcessArchitecture, platform.OsArchitecture, platform.RuntimeVersion, platform.SdkVersion,
                [.. leg.Checks.Select(check => new CheckSummary(check.Name, check.Passed, check.Detail))]));
        }

        var native = Native(Path.Combine(artifacts, "native-witness"), candidate, failures);
        var qualified = failures.Count == 0;
        JsonFile.Write(output, new QualificationManifest(qualified, candidate.Manifest, platforms, native, failures));
        foreach (var failure in failures) Console.Error.WriteLine($"Refused: {failure}");
        return qualified ? 0 : 1;
    }

    /// <summary>The witness's own provenance: pinned native build, test assets and the candidate packages it restored.</summary>
    private static NativeWitness? Native(string directory, CandidateFiles candidate, List<string> failures)
    {
        var provenancePath = Path.Combine(directory, "provenance.txt");
        var resultPath = Path.Combine(directory, "result.txt");
        if (!File.Exists(provenancePath) || !File.Exists(resultPath)) { failures.Add("No native witness provenance and result."); return null; }
        var lines = File.ReadAllLines(provenancePath);
        string? Value(string key) => lines.FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        var hashes = lines.Select(line => Regex.Match(line, @"^([0-9a-f]{64})\s+(\S+)$")).Where(match => match.Success)
            .Select(match => (Sha256: match.Groups[1].Value, File: Regex.Replace(match.Groups[2].Value, @"^.*/zeroshot-native-witness\.[^/]+/", ""))).ToList();
        void Require(bool condition, string failure) { if (!condition) failures.Add($"native witness: {failure}"); }
        Require(File.ReadAllText(resultPath).StartsWith("PASS:", StringComparison.Ordinal), "did not pass.");
        Require(Value("nativeVersion") == Required.NativeVersion && Value("sourceRevision") == Required.NativeSourceRevision,
            $"exercised native {Value("nativeVersion")} at {Value("sourceRevision")}, not the supported {Required.NativeVersion} at {Required.NativeSourceRevision}.");
        Require(Value("sdkCandidate") == "supplied" && Value("sdkCommit") == candidate.Commit, "did not run the supplied candidate's commit.");
        Require(hashes.Contains((candidate.ClientSha256, "./" + Path.GetFileName(candidate.ClientFile))), "did not restore the candidate library package.");
        Require(hashes.Contains((candidate.CliSha256, "./" + Path.GetFileName(candidate.CliFile))), "did not install the candidate tool package.");
        Require(lines.Contains($"zeroshot-dotnet {candidate.Version}+{candidate.Commit}"), "did not run the candidate CLI.");
        var executable = hashes.Where(hash => hash.File == "bin/zeroshot").Select(hash => hash.Sha256).FirstOrDefault();
        Require(executable is not null, "recorded no native executable identity.");
        return new(Value("nativeVersion"), Value("sourceRevision"), Value("release"), Value("archiveSha256"), executable,
            [.. hashes.Select(hash => new FileHash(hash.File, hash.Sha256))], File.ReadAllText(resultPath).Trim());
    }
}
