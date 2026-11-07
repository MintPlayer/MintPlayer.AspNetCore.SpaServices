using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.NodeServices.HostingModels;
using MintPlayer.AspNetCore.SpaServices.AngularCli;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.NodeServices;

/// <summary>
/// The contracts the workspace modernization introduced that need no Node process: the embedded
/// scripts, the System.Text.Json RPC encoding and the dev-server default regex.
/// </summary>
public class WorkspaceModernizationTests
{
    [Theory]
    [InlineData(typeof(HttpNodeInstance), "MintPlayer.AspNetCore.NodeServices/Content/Node/entrypoint-http.cjs", "/Content/Node/entrypoint-http.cjs")]
    [InlineData(typeof(RenderToStringResult), "MintPlayer.AspNetCore.SpaServices.Prerendering/Content/Node/prerenderer.cjs", "/Content/Node/prerenderer.cjs")]
    public void Embeds_the_hand_written_script_unchanged(Type assemblyType, string sourcePath, string resourcePath)
    {
        // There is no build step any more: the .cjs in the repository is exactly what ships.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), sourcePath));

        var embedded = EmbeddedResourceReader.Read(assemblyType, resourcePath);

        Assert.Equal(source, embedded);
    }

    [Fact]
    public async Task Sends_property_names_and_dictionary_keys_camel_cased()
    {
        // What Newtonsoft's CamelCasePropertyNamesContractResolver produced, which existing boot
        // modules read SupplyData keys by.
        var json = await NodeRpcJson.SerializeAsync(new NodeInvocationInfo
        {
            ModuleName = "m",
            ExportedFunctionName = "f",
            Args = [new Dictionary<string, object> { ["PersonId"] = 1 }],
        }, CancellationToken.None);

        var root = JsonNode.Parse(json)!;
        Assert.Equal("m", (string?)root["moduleName"]);
        Assert.Equal("f", (string?)root["exportedFunctionName"]);
        Assert.Equal(1, (int?)root["args"]![0]!["personId"]);
    }

    [Fact]
    public async Task Enumerates_an_async_sequence_such_as_an_entity_framework_query()
    {
        // An EF IQueryable is an IAsyncEnumerable, which the synchronous serializer refuses.
        static async IAsyncEnumerable<int> People()
        {
            await Task.Yield();
            yield return 1;
            yield return 2;
        }

        var json = await NodeRpcJson.SerializeAsync(new NodeInvocationInfo
        {
            ModuleName = "m",
            Args = [new Dictionary<string, object> { ["people"] = People() }],
        }, CancellationToken.None);

        Assert.Equal("[1,2]", JsonNode.Parse(json)!["args"]![0]!["people"]!.ToJsonString());
    }

    [Fact]
    public void Reads_a_render_result_case_insensitively()
    {
        var result = NodeRpcJson.Deserialize<RenderToStringResult>("""{ "html": "<p>x</p>", "statusCode": 404, "globals": { "a": 1 } }""")!;

        Assert.Equal("<p>x</p>", result.Html);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(1, (int?)result.Globals!["a"]);
    }

    [Theory]
    // Vite, the dev server of the application builder (Angular 17+), after ANSI stripping.
    [InlineData("  ➜  Local:   http://localhost:4200/", "http://localhost:4200/")]
    // The webpack dev server, kept for older workspaces.
    [InlineData("** Angular Live Development Server is listening on localhost:4200, open your browser on http://localhost:4200/ **", "http://localhost:4200/")]
    public void Recognises_the_dev_server_address_by_default(string line, string expected)
    {
        var match = new Regex(AngularCliMiddleware.DefaultFinishedPattern).Match(line);

        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups["openbrowser"].Value);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MintPlayer.AspNetCore.SpaServices.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
