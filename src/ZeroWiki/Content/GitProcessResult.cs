namespace ZeroWiki.Content;

/// <summary>The outcome of one <see cref="GitProcessRunner"/> invocation.</summary>
public sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
