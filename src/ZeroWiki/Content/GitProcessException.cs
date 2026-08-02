namespace ZeroWiki.Content;

/// <summary>A git invocation exited non-zero; carries its stderr rather than swallowing it.</summary>
public sealed class GitProcessException : Exception
{
    public GitProcessException(IReadOnlyList<string> arguments, int exitCode, string standardError)
        : base($"git {string.Join(' ', arguments)} exited with code {exitCode}: {standardError.Trim()}")
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; }
}
