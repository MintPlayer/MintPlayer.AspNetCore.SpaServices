using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.NodeServices;
using MintPlayer.AspNetCore.SpaServices.Prerendering;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Prerendering;

/// <summary>
/// The embedded prerenderer.cjs and entrypoint-http.cjs, run by a real Node process against small boot
/// modules of every supported shape. Requires <c>node</c> on PATH (CI installs it with actions/setup-node).
/// </summary>
public class PrerendererNodeIntegrationTests : IClassFixture<PrerendererNodeIntegrationTests.NodeFixture>
{
    private const string Template = "<!doctype html><html><head><base href=\"/\"></head><body><app-root></app-root></body></html>";

    private readonly NodeFixture fixture;

    public PrerendererNodeIntegrationTests(NodeFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Renders_an_es_module_that_imports_a_chunk_and_uses_top_level_await()
    {
        var result = await Render("esm/main.mjs", "/people/1?x=2");

        Assert.Equal("<p>esm|/people/1?x=2|from-dotnet|/|2</p>", result.Html);
    }

    [Fact]
    public async Task Sends_dictionary_keys_camel_cased_as_before()
    {
        // MyKey reaches JavaScript as myKey, as Newtonsoft's CamelCasePropertyNamesContractResolver did.
        var result = await Render("esm/main.mjs", "/");

        Assert.Contains("|from-dotnet|", result.Html);
    }

    [Fact]
    public async Task Hands_a_createServerRenderer_function_the_raw_arguments()
    {
        var result = await Render("cjs/flagged.cjs", "/flagged");

        Assert.Equal("flagged:/flagged", result.Html);
    }

    [Fact]
    public async Task Renders_an_angular_bundle_into_the_captured_template_with_the_supplied_providers()
    {
        var result = await Render("angular/main.server.mjs", "/about");

        Assert.Contains("<app-root>bootstrapped|/about|", result.Html);
        Assert.Contains("\"useValue\":\"hello\"", result.Html);
        Assert.Contains("\"useValue\":\"https://example.test\"", result.Html);
        Assert.StartsWith("<!doctype html>", result.Html);
    }

    [Fact]
    public async Task Names_the_missing_line_when_an_angular_bundle_does_not_export_renderApplication()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Render("angular-missing/main.server.mjs", "/"));

        Assert.Contains("export { renderApplication } from '@angular/platform-server';", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_renderer_that_does_not_resolve_to_an_object()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Render("plain-bad/main.mjs", "/"));

        Assert.Contains("must resolve to an object { html }", ex.Message);
    }

    private Task<RenderToStringResult> Render(string bootModule, string pathAndQuery)
        => Prerenderer.RenderToString(
            fixture.Root,
            fixture.NodeServices,
            CancellationToken.None,
            new JavaScriptModuleExport(bootModule),
            "https://example.test" + pathAndQuery,
            pathAndQuery,
            new Dictionary<string, object>
            {
                ["originalHtml"] = Template,
                ["MyKey"] = "from-dotnet",
                ["message"] = "hello",
            },
            0,
            string.Empty,
            CancellationToken.None);

    public sealed class NodeFixture : IDisposable
    {
        public NodeFixture()
        {
            // A space in the path: import() needs a file URL, which a bare path with spaces is not.
            Root = Path.Combine(Path.GetTempPath(), "mintplayer prerender " + Guid.NewGuid().ToString("N"));
            Write("esm/chunk-ABC123.mjs", "export const greet = (p, u, k, b, q) => `<p>${p}|${u}|${k}|${b}|${q}</p>`;");
            Write("esm/main.mjs", """
                import { greet } from './chunk-ABC123.mjs';
                const prefix = await Promise.resolve('esm');
                export default async (params) => ({ html: greet(prefix, params.url, params.data.myKey, params.baseUrl, params.location.query.x ?? '') });
                """);
            Write("cjs/flagged.cjs", """
                const renderer = (callback, applicationBasePath, bootModule, absoluteUrl, pathAndQuery) =>
                    callback(null, { html: 'flagged:' + pathAndQuery });
                renderer.isServerRenderer = true;
                module.exports = renderer;
                """);
            Write("angular/main.server.mjs", """
                const bootstrap = () => 'bootstrapped';
                export default bootstrap;
                export function renderApplication(fn, options) {
                    return Promise.resolve(options.document.replace('<app-root></app-root>',
                        `<app-root>${fn()}|${options.url}|${JSON.stringify(options.platformProviders)}</app-root>`));
                }
                export const prerenderProviders = (data, params) => [
                    { provide: 'MESSAGE', useValue: data.message },
                    { provide: 'ORIGIN', useValue: params.origin },
                ];
                export function ɵgetOrCreateAngularServerApp() {}
                """);
            Write("angular-missing/main.server.mjs", """
                export default () => 'bootstrapped';
                export function ɵgetOrCreateAngularServerApp() {}
                """);
            Write("plain-bad/main.mjs", "export default async () => 'not an object';");

            var options = new NodeServicesOptions(new ServiceCollection().BuildServiceProvider())
            {
                ProjectPath = Root,
                // No file watcher: nothing here changes the fixtures while Node runs.
                WatchFileExtensions = [],
            };
            NodeServices = NodeServicesFactory.CreateNodeServices(options);
        }

        public string Root { get; }

        public INodeServices NodeServices { get; }

        private void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            NodeServices.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
