namespace Zeroshot.Cli;

/// <summary>One parsed command: its name, flags, option values and positional arguments.</summary>
internal sealed class Invocation(string command, HashSet<string> flags, Dictionary<string, string> values, List<string> positionals)
{
    public string Command { get; } = command;
    public IReadOnlyList<string> Positionals { get; } = positionals;
    public bool Json => flags.Contains("--json");
    public bool Help => flags.Contains("--help");

    public bool Flag(string name) => flags.Contains(name);
    public string? Value(string name) => values.GetValueOrDefault(name);
    public bool Has(string name) => flags.Contains(name) || values.ContainsKey(name);

    public TimeSpan? Duration(string name)
    {
        if (Value(name) is not { } text) return null;
        return CliDuration.TryParse(text, out var value) ? value
            : throw CliFailure.Invocation($"{name} must be {CliDuration.Rule}.");
    }

    /// <summary>A wait budget; absent or <c>infinite</c> is indefinite (null).</summary>
    public TimeSpan? WaitBudget(string name)
    {
        if (Value(name) is not { } text) return null;
        return CliDuration.TryParseWaitBudget(text, out var value) ? value
            : throw CliFailure.Invocation($"{name} must be 'infinite' or {CliDuration.Rule}, {CliDuration.MaxWaitRule}.");
    }
}

/// <summary>A flag, or an option whose value the synopsis shows as <see cref="Value"/>.</summary>
internal sealed record Option(string Name, string? Value = null)
{
    public override string ToString() => Value is null ? Name : $"{Name} {Value}";
}

/// <summary>Options shown and checked together: at most one of them may be given, and one must be when required.</summary>
internal sealed record Group(bool Required, params Option[] Options);

/// <summary>
/// One command's grammar in synopsis order: positional arguments, then option groups. RUN is RUN_ID with --config, or
/// --run-file instead with an optional matching --config, so a command that takes it accepts both options.
/// </summary>
internal sealed record Command(string Name, string[] Positionals, params Group[] Groups)
{
    public bool TakesRun => Positionals is ["RUN", ..];
    public IEnumerable<Option> Options => [.. TakesRun ? CommandLine.RunOptions : [], .. Groups.SelectMany(group => group.Options)];

    /// <summary>The rules beyond single options, in this order: exclusive options, the positional count, required options.</summary>
    public void Check(Invocation invocation)
    {
        foreach (var group in Groups)
            if (group.Options.Where(option => invocation.Has(option.Name)).ToList() is [var first, var second, ..])
                throw CliFailure.Invocation($"{first.Name} and {second.Name} cannot be combined.");
        var runFile = TakesRun && invocation.Has(CommandLine.RunFile.Name);
        if (invocation.Positionals.Count != Positionals.Length - (runFile ? 1 : 0))
        {
            var rest = string.Join(' ', Positionals.Skip(1));
            throw CliFailure.Invocation(!TakesRun ? $"'{Name}' takes {string.Join(' ', Positionals)}."
                : rest.Length == 0 ? $"'{Name}' takes one RUN_ID, or --run-file FILE instead."
                : $"'{Name}' takes RUN_ID {rest}, or {rest} with --run-file FILE.");
        }
        foreach (var group in Groups.Where(group => group.Required))
            if (!group.Options.Any(option => invocation.Has(option.Name)))
                throw CliFailure.Invocation($"'{Name}' requires {string.Join(" or ", group.Options.Select(option => option.Name))}.");
        if (TakesRun && !runFile && !invocation.Has(CommandLine.Config.Name))
            throw CliFailure.Invocation("RUN_ID requires --config FILE naming its target.");
    }
}

/// <summary>
/// The accepted command grammar, declared once: the parser enforces it, the help synopsis shows it and release
/// qualification reads it from the packed tool through <see cref="CliContract.Commands"/>.
/// </summary>
internal static class CommandLine
{
    public static readonly Option Config = new("--config", "FILE"), RunFile = new("--run-file", "FILE");
    public static readonly Option[] RunOptions = [RunFile, Config];
    private static readonly Option Request = new("--request", "FILE"), Out = new("--out", "FILE"), Overwrite = new("--overwrite"),
        Prepared = new("--prepared", "FILE"), Detach = new("--detach"), Timeout = new("--timeout", "WAIT"),
        SaveRequest = new("--save-request", "FILE"), SaveRun = new("--save-run", "FILE"), RequestTimeout = new("--request-timeout", "DURATION"),
        After = new("--after", "CURSOR"), Checkpoint = new("--checkpoint", "FILE"), Recovery = new("--recovery", "MODE"),
        Execution = new("--execution", "EXECUTION"), WaitTimeout = new("--wait-timeout", "WAIT"), RequestOnly = new("--request-only");

    /// <summary>Flags every command accepts.</summary>
    public static readonly Option[] Every = [new("--json"), new("--help")];
    /// <summary>Options given alone instead of a command.</summary>
    public static readonly Option[] Alone = [new("--version")];

    public static readonly Command[] Commands =
    [
        new("prepare", [], Required(Request), Required(Out), Optional(Overwrite)),
        new("run", [], Required(Config), Required(Request, Prepared), Optional(Detach, Timeout), Optional(SaveRequest), Optional(SaveRun),
            Optional(Overwrite), Optional(RequestTimeout)),
        new("status", ["RUN"], Optional(RequestTimeout)),
        new("wait", ["RUN"], Optional(Timeout), Optional(RequestTimeout)),
        new("watch", ["RUN"], Optional(After, Checkpoint), Optional(Recovery), Optional(RequestTimeout)),
        new("logs", ["RUN"], Optional(Execution), Optional(After, Checkpoint), Optional(Recovery), Optional(RequestTimeout)),
        new("attach", ["RUN", "EXECUTION"], Optional(RequestTimeout)),
        new("force-stop", ["RUN"], Optional(WaitTimeout, RequestOnly), Optional(RequestTimeout)),
    ];

    private static Group Required(params Option[] options) => new(true, options);
    private static Group Optional(params Option[] options) => new(false, options);

    public static bool IsCommand(string name) => Commands.Any(command => command.Name == name);

    /// <summary>Parses one command and, unless it asks for help, checks it against the command's grammar.</summary>
    public static Invocation Parse(string[] args)
    {
        var command = Commands.FirstOrDefault(command => command.Name == args[0])
            ?? throw CliFailure.Invocation($"Unknown command '{args[0]}'.");
        var options = Every.Concat(command.Options).ToDictionary(option => option.Name, StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i] == "-h" ? "--help" : args[i];
            // --name=value takes any value verbatim, including one that begins with "--" (opaque cursors and
            // references allow it); the separate form refuses such a value as a probably forgotten one.
            var (name, inline) = arg.IndexOf('=') is > 2 and var at && arg.StartsWith("--", StringComparison.Ordinal)
                ? (arg[..at], arg[(at + 1)..]) : (arg, null);
            var option = options.GetValueOrDefault(name);
            if (inline is null && option is { Value: null })
            {
                if (!flags.Add(arg)) throw CliFailure.Invocation($"{arg} was given more than once.");
            }
            else if (option is { Value: not null })
            {
                if (inline is null && (i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                    throw CliFailure.Invocation($"{name} requires a value.");
                if (!values.TryAdd(name, inline ?? args[++i])) throw CliFailure.Invocation($"{name} was given more than once.");
            }
            else if (arg.StartsWith('-'))
                throw CliFailure.Invocation($"'{args[0]}' does not accept {arg}.");
            else if (positionals.Count == command.Positionals.Length)
                throw CliFailure.Invocation($"Unexpected argument '{arg}'.");
            else positionals.Add(arg);
        }
        var invocation = new Invocation(args[0], flags, values, positionals);
        if (!invocation.Help) command.Check(invocation);
        return invocation;
    }

    /// <summary>Each command's usage line, wrapped at 100 columns under its first argument.</summary>
    private static IEnumerable<string> Synopsis()
    {
        foreach (var command in Commands)
        {
            var lead = $"  zeroshot-dotnet {command.Name}";
            var lines = new List<string> { lead };
            foreach (var part in command.Positionals.Concat(command.Groups.Select(Show)))
            {
                if (lines[^1].Length + 1 + part.Length > 100) lines.Add(new string(' ', lead.Length));
                lines[^1] += " " + part;
            }
            yield return string.Join('\n', lines);
        }
        foreach (var option in Alone) yield return $"  zeroshot-dotnet {option}";
    }

    private static string Show(Group group)
    {
        var options = string.Join(" | ", group.Options);
        return !group.Required ? $"[{options}]" : group.Options.Length > 1 ? $"({options})" : options;
    }

    public static readonly string Usage = $"""
        zeroshot-dotnet: thin command line over the Zeroshot .NET SDK (native {Native.NativeSchemas.NativeVersion}).

        Usage:
        {string.Join('\n', Synopsis())}

        Every command accepts --json (versioned zeroshot-dotnet/cli/v1 records) and --help. An option
        value can also be given as --option=VALUE, which is required for a value that begins with --.

          RUN        RUN_ID with --config FILE, or --run-file FILE with an optional matching --config FILE.
          --config   Target configuration: address, caller-supplied native binding, transport/observation
                     settings and the names of environment variables that hold credentials.
          DURATION   A whole number with a unit ms, s, m or h, such as 1500ms, 45s or 10m.
          WAIT       A DURATION or 'infinite' (the default).
          CURSOR     An opaque native cursor from an earlier watch or logs record.
          MODE       established-interruptions (default) or none.

        prepare needs no target, credentials or network. run submits once and waits for the result
        unless --detach is given; --save-request is written before submitting and --save-run only
        after an acknowledgement. force-stop sends one force request, then waits unless
        --request-only is given. Existing output files are refused unless --overwrite is given.
        watch and logs replay retained history exclusively after --after or --checkpoint, then follow
        it live until native closes the stream, reopening an interrupted stream after the last record
        written unless --recovery none is given. attach streams one active execution live, with no
        replay and no reopen. A normal close exits 0 and says nothing about the run's outcome.
        Ctrl+C detaches or abandons the pending request; it never stops a run.

        Exit codes: 0 success (including status of a failed run), 1 operational failure or native
        rejection, 2 invalid invocation, configuration, input or binding, 3 run, wait or force-stop
        observed a failed run, 4 wait timeout, 5 unknown mutation outcome, 130 cancelled.
        """;
}
