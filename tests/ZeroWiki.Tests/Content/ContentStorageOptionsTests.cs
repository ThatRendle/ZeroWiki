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
