namespace NexusFree.Tests;

/// <summary>The engine's command line: an argument it does not know is refused instead of starting a silent MCP server.</summary>
public sealed class CommandLineTests
{
    private static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData("")]
    [InlineData("--mcp")]
    [InlineData("--mcp --agent antigravity")]
    [InlineData("--exec --help")]
    [InlineData("--install --apply")]
    [InlineData("--status")]
    [InlineData("--result req-0123456789abcdef0123456789abcdef")]
    [InlineData("-v")]
    public void Known_arguments_are_accepted(string line) => Assert.Null(Program.UnknownArgument(Split(line)));

    [Theory]
    [InlineData("--helpp", "--helpp")]
    [InlineData("--mcp --debug", "--debug")]
    [InlineData("whoami", "whoami")]
    public void An_unknown_argument_is_named(string line, string expected) => Assert.Equal(expected, Program.UnknownArgument(Split(line)));

    [Fact]
    public async Task Help_answers_at_once_instead_of_waiting_on_stdin()
    {
        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        try { Assert.Equal(0, await Program.Main(["--help"])); }
        finally { Console.SetOut(previous); }
        Assert.Contains("--exec", output.ToString(), StringComparison.Ordinal);
    }
}
