using TUnit.Assertions;
using TUnit.Core;

namespace Zeroshot.Cli.Tests;

/// <summary>The declared command grammar, in process: what it parses, what it refuses and the synopsis it shows.</summary>
public sealed class CommandLineTests
{
    private static Invocation Parse(string command) => CommandLine.Parse(command.Split(' '));

    [Test]
    [Arguments("submit --request r", "Unknown command 'submit'.")]
    [Arguments("prepare --request r --out o --config c", "'prepare' does not accept --config.")]
    [Arguments("prepare --request r --out o --overwrite=yes", "'prepare' does not accept --overwrite=yes.")]
    [Arguments("watch RUN --config c --execution e", "'watch' does not accept --execution.")]
    [Arguments("prepare --request r --request s --out o", "--request was given more than once.")]
    [Arguments("prepare --request r --out --json", "--out requires a value.")]
    [Arguments("prepare extra --request r --out o", "Unexpected argument 'extra'.")]
    [Arguments("status RUN OTHER --config c", "Unexpected argument 'OTHER'.")]
    [Arguments("run --config c --request r --prepared p", "--request and --prepared cannot be combined.")]
    [Arguments("run --config c --request r --detach --timeout 1m", "--detach and --timeout cannot be combined.")]
    [Arguments("logs RUN --config c --checkpoint k --after a", "--after and --checkpoint cannot be combined.")]
    [Arguments("force-stop RUN --config c --request-only --wait-timeout 1m", "--wait-timeout and --request-only cannot be combined.")]
    [Arguments("prepare --out o", "'prepare' requires --request.")]
    [Arguments("prepare --request r", "'prepare' requires --out.")]
    [Arguments("run --request r", "'run' requires --config.")]
    [Arguments("run --config c", "'run' requires --request or --prepared.")]
    [Arguments("status --config c", "'status' takes one RUN_ID, or --run-file FILE instead.")]
    [Arguments("status RUN --run-file f", "'status' takes one RUN_ID, or --run-file FILE instead.")]
    [Arguments("attach RUN --config c", "'attach' takes RUN_ID EXECUTION, or EXECUTION with --run-file FILE.")]
    [Arguments("attach RUN EXECUTION --run-file f", "'attach' takes RUN_ID EXECUTION, or EXECUTION with --run-file FILE.")]
    [Arguments("status RUN", "RUN_ID requires --config FILE naming its target.")]
    // Exclusive options are refused first, then a wrong positional count, then a missing option.
    [Arguments("run --request r --prepared p", "--request and --prepared cannot be combined.")]
    [Arguments("watch --after a --checkpoint k", "--after and --checkpoint cannot be combined.")]
    [Arguments("attach RUN", "'attach' takes RUN_ID EXECUTION, or EXECUTION with --run-file FILE.")]
    public async Task AnInvocationOutsideTheGrammarIsRefused(string command, string message)
    {
        var failure = await Assert.ThrowsAsync<CliFailure>(() => Task.FromResult(Parse(command)));
        await Assert.That(failure!.Message).IsEqualTo(message);
        await Assert.That(failure.Category).IsEqualTo("invocation");
        await Assert.That(failure.ExitCode).IsEqualTo(ExitCodes.Invalid);
    }

    [Test]
    [Arguments("prepare --request r --out o --overwrite --json")]
    [Arguments("run --config c --prepared p --detach --save-run s --request-timeout 5s")]
    [Arguments("status RUN --config c")]
    [Arguments("status --run-file f")]
    [Arguments("wait --run-file f --config c --timeout infinite")]
    [Arguments("logs RUN --config c --execution e --checkpoint k --recovery none")]
    [Arguments("attach EXECUTION --run-file f")]
    [Arguments("force-stop RUN --config c --request-only")]
    // Help skips the grammar's checks.
    [Arguments("run --request r --prepared p --help")]
    [Arguments("attach -h")]
    public async Task AnInvocationTheGrammarDeclaresParses(string command)
        => await Assert.That(Parse(command).Command).IsEqualTo(command.Split(' ')[0]);

    [Test]
    public async Task AnInlineValueIsTakenVerbatim()
    {
        var invocation = Parse("watch RUN --config=c --after=--opaque=1");
        await Assert.That(invocation.Value("--after")).IsEqualTo("--opaque=1");
        await Assert.That(invocation.Value("--config")).IsEqualTo("c");
        await Assert.That(invocation.Positionals).IsEquivalentTo(["RUN"]);
    }

    [Test]
    public async Task TheSynopsisShowsEachCommandsGroupsWithinOneHundredColumns()
    {
        var usage = CommandLine.Usage.Split('\n');
        var synopsis = usage.SkipWhile(line => line != "Usage:").Skip(1).TakeWhile(line => line.Length > 0);
        await Assert.That(string.Join('\n', synopsis)).IsEqualTo("""
              zeroshot-dotnet prepare --request FILE --out FILE [--overwrite]
              zeroshot-dotnet run --config FILE (--request FILE | --prepared FILE) [--detach | --timeout WAIT]
                                  [--save-request FILE] [--save-run FILE] [--overwrite]
                                  [--request-timeout DURATION]
              zeroshot-dotnet status RUN [--request-timeout DURATION]
              zeroshot-dotnet wait RUN [--timeout WAIT] [--request-timeout DURATION]
              zeroshot-dotnet watch RUN [--after CURSOR | --checkpoint FILE] [--recovery MODE]
                                    [--request-timeout DURATION]
              zeroshot-dotnet logs RUN [--execution EXECUTION] [--after CURSOR | --checkpoint FILE]
                                   [--recovery MODE] [--request-timeout DURATION]
              zeroshot-dotnet attach RUN EXECUTION [--request-timeout DURATION]
              zeroshot-dotnet force-stop RUN [--wait-timeout WAIT | --request-only] [--request-timeout DURATION]
              zeroshot-dotnet --version
            """);
        await Assert.That(usage.Max(line => line.Length)).IsLessThanOrEqualTo(100);
    }
}
