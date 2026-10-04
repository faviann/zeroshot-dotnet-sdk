namespace Qualification.Tests;

public sealed class ContractTests
{
    [Test]
    public async Task TheBuiltCliDeclaresExactlyTheCommittedCliContract()
    {
        // Pack reads these lines from the packed tool by the same code; package lines need the packages themselves.
        var built = Candidate.CliLines(Path.Combine(AppContext.BaseDirectory, "zeroshot-dotnet.dll")).Distinct().Order(StringComparer.Ordinal);
        var committed = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "contract.txt")).Where(line => line.StartsWith("cli ", StringComparison.Ordinal));
        await Assert.That(string.Join('\n', built)).IsEqualTo(string.Join('\n', committed));
    }
}
