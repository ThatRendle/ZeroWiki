using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises the real configuration-binding pipeline (<see cref="ContentStorageStartupExtensions.AddContentStorage"/>)
/// rather than constructing <see cref="ContentPaths"/> by hand, so a broken section name or
/// binder wiring would actually fail these tests.
/// </summary>
public class ContentStorageOptionsTests
{
    [Fact]
    public void DefaultsDataRootToSlashDataWhenUnconfigured()
    {
        var paths = BuildContentPaths([]);

        Assert.Equal(Path.GetFullPath("/data"), paths.DataRoot);
    }

    [Fact]
    public void ConfigurationSectionOverridesDataRoot()
    {
        var paths = BuildContentPaths(new Dictionary<string, string?>
        {
            ["ContentStorage:DataRoot"] = "/srv/zerowiki",
        });

        Assert.Equal(Path.GetFullPath("/srv/zerowiki"), paths.DataRoot);
    }

    [Fact]
    public void EnvironmentVariableOverridesDataRoot()
    {
        const string variable = "ContentStorage__DataRoot";

        Environment.SetEnvironmentVariable(variable, "/env/zerowiki");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var services = new ServiceCollection();
            services.AddContentStorage(configuration);
            using var provider = services.BuildServiceProvider();

            var paths = provider.GetRequiredService<ContentPaths>();

            Assert.Equal(Path.GetFullPath("/env/zerowiki"), paths.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void DerivesRepositoryRootAndWorkingTreeFromDataRoot()
    {
        var paths = BuildContentPaths(new Dictionary<string, string?>
        {
            ["ContentStorage:DataRoot"] = "/data",
        });

        Assert.Equal(Path.GetFullPath("/data/wiki"), paths.RepositoryRoot);
        Assert.Equal(Path.GetFullPath("/data/wiki/docs"), paths.WorkingTree);
    }

    [Fact]
    public void DerivesKeysDirectoryAsSiblingOfRepositoryRoot()
    {
        var paths = BuildContentPaths(new Dictionary<string, string?>
        {
            ["ContentStorage:DataRoot"] = "/data",
        });

        Assert.Equal(Path.GetFullPath("/data/keys"), paths.KeysDirectory);
    }

    [Fact]
    public void KeysDirectoryIsOutsideTheRepositoryRoot()
    {
        // The DataProtection key ring must never land inside the git repository's own tree — a key
        // file written under RepositoryRoot would be picked up by commit-on-save (D9) and pushed to
        // every Obsidian vault that clones the remote.
        var paths = BuildContentPaths(new Dictionary<string, string?>
        {
            ["ContentStorage:DataRoot"] = "/data",
        });

        Assert.False(
            paths.KeysDirectory.StartsWith(paths.RepositoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"'{paths.KeysDirectory}' must not live under the repository root '{paths.RepositoryRoot}'.");
    }

    [Fact]
    public void ResolveContentPathsMatchesTheDiRegisteredSingleton()
    {
        // ResolveContentPaths (used by Program.cs to configure DataProtection before the DI
        // container is built) must derive the exact same paths as the AddContentStorage-registered
        // singleton — one definition of the data root, not two that can drift.
        var configurationValues = new Dictionary<string, string?>
        {
            ["ContentStorage:DataRoot"] = "/srv/zerowiki",
        };

        var diPaths = BuildContentPaths(configurationValues);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var resolvedPaths = ContentStorageStartupExtensions.ResolveContentPaths(configuration);

        Assert.Equal(diPaths.DataRoot, resolvedPaths.DataRoot);
        Assert.Equal(diPaths.KeysDirectory, resolvedPaths.KeysDirectory);
    }

    private static ContentPaths BuildContentPaths(Dictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var services = new ServiceCollection();
        services.AddContentStorage(configuration);
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<ContentPaths>();
    }
}
