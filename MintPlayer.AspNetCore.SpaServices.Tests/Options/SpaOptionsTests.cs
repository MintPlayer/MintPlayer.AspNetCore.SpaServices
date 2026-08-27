using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.SpaServices.Core;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Options;

public class SpaOptionsTests
{
    [Fact]
    public void Defaults_to_the_conventional_spa_entry_point()
    {
        var options = new SpaOptions();

        Assert.Equal("/index.html", options.DefaultPage.Value);
        Assert.Equal("npm", options.PackageManagerCommand);
        Assert.Equal(TimeSpan.FromSeconds(120), options.StartupTimeout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_an_empty_default_page(string? value)
    {
        var options = new SpaOptions();

        Assert.Throws<ArgumentException>(() => options.DefaultPage = value!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_an_empty_package_manager_command(string? value)
    {
        var options = new SpaOptions();

        Assert.Throws<ArgumentException>(() => options.PackageManagerCommand = value!);
    }

    [Fact]
    public void Accepts_a_custom_default_page_and_package_manager()
    {
        var options = new SpaOptions
        {
            DefaultPage = "/app.html",
            PackageManagerCommand = "yarn",
        };

        Assert.Equal("/app.html", options.DefaultPage.Value);
        Assert.Equal("yarn", options.PackageManagerCommand);
    }

    [Fact]
    public void Copy_constructor_carries_every_property()
    {
        // UseSpaImproved clones the options so that multiple UseSpa calls do not interfere. Before
        // this was fixed, StartupTimeout and CliRegexes were the two properties the copy left
        // behind, so a caller-configured startup timeout was silently lost exactly where it mattered.
        var regexes = new[] { new Regex("ready") };
        var source = new SpaOptions
        {
            DefaultPage = "/app.html",
            SourcePath = "ClientApp",
            DevServerPort = 4200,
            PackageManagerCommand = "yarn",
            StartupTimeout = TimeSpan.FromSeconds(5),
            CliRegexes = regexes,
        };

        var copy = Clone(source);

        Assert.Equal("/app.html", copy.DefaultPage.Value);
        Assert.Equal("ClientApp", copy.SourcePath);
        Assert.Equal(4200, copy.DevServerPort);
        Assert.Equal("yarn", copy.PackageManagerCommand);
        Assert.Equal(TimeSpan.FromSeconds(5), copy.StartupTimeout);
        Assert.Same(regexes, copy.CliRegexes);
    }

    [Fact]
    public void Copy_constructor_carries_the_static_file_options()
    {
        var fileOptions = new Microsoft.AspNetCore.Builder.StaticFileOptions();
        var source = new SpaOptions { DefaultPageStaticFileOptions = fileOptions };

        Assert.Same(fileOptions, Clone(source).DefaultPageStaticFileOptions);
    }

    /// <summary>Reachable because the library grants this assembly InternalsVisibleTo.</summary>
    private static SpaOptions Clone(SpaOptions source) => new(source);
}
