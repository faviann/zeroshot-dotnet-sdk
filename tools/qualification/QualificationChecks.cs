/// <summary>
/// What a passing platform leg must prove. The leg and the gate apply the same suite rules, so a successful flag
/// cannot substitute for complete, consistent test evidence. The artifact records retain their existing shape.
/// </summary>
internal static class QualificationChecks
{
    private enum Kind { Check, Example, Suite, CliSuite }

    private static readonly Dictionary<string, Kind> Catalog = new(StringComparer.Ordinal)
    {
        ["platform"] = Kind.Check,
        ["fresh-consumers-restore-candidate"] = Kind.Check,
        ["sdk-tests"] = Kind.Suite,
        ["ContractsConsumer"] = Kind.Example,
        ["BindingsConsumer"] = Kind.Example,
        ["RunHandleConsumer"] = Kind.Example,
        ["cli-repository"] = Kind.CliSuite,
        ["cli-global-tool"] = Kind.CliSuite,
        ["cli-local-manifest"] = Kind.CliSuite,
        ["cli-explicit-path"] = Kind.CliSuite,
        ["no-python-or-native-used"] = Kind.Check,
    };

    public static IEnumerable<string> Examples => Catalog.Where(check => check.Value == Kind.Example).Select(check => check.Key);

    /// <summary>Records a successful check only when its evidence supports that verdict.</summary>
    public static CheckResult Passed(string name, object? detail, string os)
    {
        if (!Catalog.TryGetValue(name, out var kind)) throw new QualificationException($"Undeclared qualification check {name}.");
        var record = new CheckResult { Name = name, Detail = JsonFile.Node(detail), Passed = true };
        var failures = Validate(record, kind, os);
        if (failures.Count > 0) throw new QualificationException($"{string.Join(" ", failures)} Detail: {JsonFile.Compact(record.Detail)}");
        return record;
    }

    /// <summary>Every required check must occur exactly once, pass, and carry the evidence its kind requires.</summary>
    public static List<string> Failures(IReadOnlyList<CheckResult> checks, string os)
    {
        var failures = new List<string>();
        foreach (var (name, kind) in Catalog)
        {
            var matching = checks.Where(check => check?.Name == name).ToList();
            if (matching.Count != 1)
            {
                failures.Add($"check {name} occurs {matching.Count} times, expected exactly one.");
                continue;
            }
            failures.AddRange(Validate(matching[0], kind, os));
        }
        foreach (var check in checks)
            if (check is null) failures.Add("The check list contains null.");
            else if (!Catalog.ContainsKey(check.Name)) failures.Add($"Undeclared qualification check {check.Name}.");
        return failures;
    }

    private static List<string> Validate(CheckResult check, Kind kind, string os)
    {
        var failures = new List<string>();
        void Require(bool condition, string failure) { if (!condition) failures.Add($"{check.Name}: {failure}"); }
        Require(check.Passed, "the check did not pass.");
        Require(!check.Passed || check.Failure is null, "a passing check reports failure information.");
        if (!check.Passed || kind is not (Kind.Suite or Kind.CliSuite)) return failures;
        if (check.Detail is null)
        {
            Require(false, "a passing suite must include its detail.");
            return failures;
        }

        SuiteSummary summary;
        try { summary = JsonFile.Parse<SuiteSummary>(check.Detail, $"{check.Name} detail"); }
        catch (QualificationException malformed) { failures.Add(malformed.Message); return failures; }
        Require(summary.Total > 0 && summary.Failed >= 0 && summary.Succeeded >= 0 && summary.Skipped >= 0,
            "test counts must be non-negative with a positive total.");
        Require(summary.Total == (long)summary.Failed + summary.Succeeded + summary.Skipped, "test counts do not sum to total.");
        Require(summary.Failed == 0 && summary.ExitCode == 0, $"the suite did not pass (failed={summary.Failed}, exitCode={summary.ExitCode}).");
        Require(summary.SkippedTests.Length == summary.Skipped, "the skipped test count differs from skippedTests.");
        foreach (var skipped in summary.SkippedTests)
            if (skipped is null) Require(false, "skippedTests contains null.");
            else Require(MaySkip(check.Name, os, skipped), $"skipped {skipped}, which must run on {os}.");
        if (kind == Kind.CliSuite)
        {
            Require(summary.UnobservedOutput is { Count: 0 }, "unobservedOutput must be present and empty.");
            Require(summary.UndeclaredOutput is { Count: 0 }, "undeclaredOutput must be present and empty.");
        }
        return failures;
    }

    /// <summary>The only tests a platform may skip: named-pipe tests run only on Windows, Ctrl+C tests only off it.</summary>
    private static bool MaySkip(string check, string os, string test)
    {
        var method = test.Split('(')[0];
        return os.StartsWith("Windows", StringComparison.Ordinal)
            ? Catalog[check] == Kind.CliSuite && WindowsSkips.Contains(method)
            : check == "sdk-tests" && PosixSkips.Contains(method);
    }

    private static readonly string[] PosixSkips =
    [
        "OnlyExactLocalPipesAreAcceptedAndConnectingIsCancellableAndBounded", "OwnedPipeIsClosedOnDisposalAndABorrowedPipeStaysOpen",
        "PipeThatIsNotPrivateToThisUserIsRefusedBeforeAnythingIsSent", "PrivatePipeCarriesOecpUntilTheControllerDisconnects",
    ];

    private static readonly string[] WindowsSkips =
    [
        "CtrlCDetachesWithExit130AndTheLastDeliveredCursor", "CtrlCAfterTheSubmissionWasSentIsAnUnknownOutcome",
        "CtrlCWhileWaitingAfterAcknowledgementIsExit130AndKeepsTheAcknowledgedRun", "CtrlCBeforeTheForceIsSentIsExit130WithNothingSent",
    ];

    /// <summary>Declared output only Ctrl+C tests produce, which cannot run on Windows.</summary>
    public static bool MayBeUnobserved(bool windows, string entry) => windows && entry == "error attempt.cancelled";
}
