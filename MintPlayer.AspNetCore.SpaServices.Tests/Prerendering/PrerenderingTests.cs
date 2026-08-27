using MintPlayer.AspNetCore.SpaServices.Prerendering;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

public class AngularPrerendererBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_an_empty_npm_script(string? npmScript)
    {
        Assert.Throws<ArgumentException>(() => new AngularPrerendererBuilder(npmScript!));
    }

    [Fact]
    public void Accepts_an_npm_script_name()
    {
        Assert.NotNull(new AngularPrerendererBuilder("build:ssr"));
    }

    [Fact]
    public void Accepts_an_explicit_finished_regex_and_occurrence()
    {
        Assert.NotNull(new AngularPrerendererBuilder("build:ssr", "Entrypoint main", 1));
    }

    [Fact]
    public void Validates_the_npm_script_before_the_finished_regex()
    {
        // The npm-script guard runs first, so an empty script name is reported even when the regex
        // is also absent - the caller gets the actionable error rather than a NullReferenceException.
        Assert.Throws<ArgumentException>(() => new AngularPrerendererBuilder("", null!, 1));
    }
}

public class RenderToStringResultTests
{
    [Fact]
    public void Produces_no_script_when_there_are_no_globals()
    {
        var result = new RenderToStringResult();

        Assert.Equal(string.Empty, result.CreateGlobalsAssignmentScript());
    }

    [Fact]
    public void Assigns_each_global_onto_the_window_object()
    {
        var result = new RenderToStringResult
        {
            Globals = JObject.Parse("""{ "answer": 42 }"""),
        };

        Assert.Equal("""window["answer"] = JSON.parse("42");""", result.CreateGlobalsAssignmentScript());
    }

    [Fact]
    public void Emits_one_assignment_per_global()
    {
        var result = new RenderToStringResult
        {
            Globals = JObject.Parse("""{ "a": 1, "b": 2 }"""),
        };

        var script = result.CreateGlobalsAssignmentScript();

        Assert.Contains("""window["a"] = JSON.parse("1");""", script);
        Assert.Contains("""window["b"] = JSON.parse("2");""", script);
    }

    [Fact]
    public void Serializes_a_nested_object_as_json()
    {
        var result = new RenderToStringResult
        {
            Globals = JObject.Parse("""{ "config": { "url": "/api" } }"""),
        };

        var script = result.CreateGlobalsAssignmentScript();

        Assert.StartsWith("""window["config"] = JSON.parse(""", script);
        Assert.Contains("url", script);
    }

    [Fact]
    public void Escapes_a_value_that_would_otherwise_close_the_script_tag()
    {
        // The script is emitted into an HTML page, so a global containing "</script>" must not be
        // able to break out of it.
        var result = new RenderToStringResult
        {
            Globals = JObject.Parse("""{ "payload": "</script><script>alert(1)</script>" }"""),
        };

        var script = result.CreateGlobalsAssignmentScript();

        Assert.DoesNotContain("</script>", script);
    }

    [Fact]
    public void Escapes_a_global_name_as_well_as_its_value()
    {
        var result = new RenderToStringResult
        {
            Globals = JObject.Parse("""{ "a\"b": 1 }"""),
        };

        var script = result.CreateGlobalsAssignmentScript();

        // The raw quote would terminate the property-name string literal.
        Assert.DoesNotContain("""window["a"b"]""", script);
    }
}
