using System.Diagnostics;

namespace ZeroWiki.Content;

/// <summary>
/// Runs <c>git</c> as a subprocess against a working directory, capturing stdout/stderr rather than
/// inheriting the parent's console. The single place every content-repository git invocation goes
/// through — repository bootstrap here, and later <c>log</c>/<c>blame</c>, commit-on-save, and the
/// Smart HTTP remote — so a process is only ever started one way.
/// </summary>
public sealed class GitProcessRunner
{
    /// <summary>
    /// Starts <c>git</c> with <paramref name="arguments"/> as a genuine argument array — never a
    /// concatenated string a shell would re-split — and waits for it to exit.
    /// </summary>
    /// <param name="environmentVariables">
    /// Variables added on top of the inherited environment, e.g. <c>GIT_AUTHOR_*</c>/
    /// <c>GIT_COMMITTER_*</c> so a commit's identity never depends on ambient git configuration.
    /// </param>
    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        return new GitProcessResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>Runs git and throws <see cref="GitProcessException"/> if it exits non-zero.</summary>
    public async Task<GitProcessResult> RunOrThrowAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(workingDirectory, arguments, environmentVariables, cancellationToken);
        if (!result.Succeeded)
        {
            throw new GitProcessException(arguments, result.ExitCode, result.StandardError);
        }

        return result;
    }
}
