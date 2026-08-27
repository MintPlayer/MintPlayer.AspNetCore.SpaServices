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
    public void Copy_constructor_drops_StartupTimeout_and_CliRegexes()
    {
        // Current behaviour, and a real bug worth knowing about: the copy constructor carries every
        // property EXCEPT StartupTimeout and CliRegexes, which silently revert to their defaults.
        // UseSpaImproved clones the options so that multiple UseSpa calls do not interfere, so a
        // caller-configured startup timeout is lost exactly where it matters. Pinned so that fixing
        // it is a deliberate change with a failing test to point at.
        var source = new SpaOptions
        {
            DefaultPage = "/app.html",
            SourcePath = "ClientApp",
            DevServerPort = 4200,
            PackageManagerCommand = "yarn",
            StartupTimeout = TimeSpan.FromSeconds(5),
            CliRegexes = [new Regex("ready")],
        };

        var copy = Clone(source);

        Assert.Equal("/app.html", copy.DefaultPage.Value);
        Assert.Equal("ClientApp", copy.SourcePath);
        Assert.Equal(4200, copy.DevServerPort);
        Assert.Equal("yarn", copy.PackageManagerCommand);

        Assert.NotEqual(source.StartupTimeout, copy.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), copy.StartupTimeout);
        Assert.Null(copy.CliRegexes);
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
