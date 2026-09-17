using Microsoft.Extensions.DependencyInjection;
using MintPlayer.AspNetCore.SpaServices.Prerendering.Services;
using MintPlayer.AspNetCore.SpaServices.Routing;
using Xunit;

namespace MintPlayer.AspNetCore.SpaServices.Tests.Routing;

/// <summary>
/// The absolute-URL overloads of <c>GenerateUrl</c>, the group-with-builder overload of
/// <c>SpaRouteItem</c>, and the DI registration extension. All of these are thin wrappers over logic
/// that is already covered - they were simply never called.
/// </summary>
public class GenerateUrlOverloadTests
{
	private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
		=> values.ToDictionary(v => v.Key, v => v.Value);

	[Fact]
	public async Task Prefixes_protocol_and_host_from_a_dictionary()
	{
		var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

		var url = await service.GenerateUrl("person-edit", Params(("personid", 5)), "https", "example.com");

		Assert.Equal("https://example.com/person/5/edit", url);
	}

	[Fact]
	public async Task Prefixes_protocol_and_host_from_an_anonymous_object()
	{
		var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

		var url = await service.GenerateUrl("person-edit", new { personid = 5 }, "https", "example.com");

		Assert.Equal("https://example.com/person/5/edit", url);
	}

	[Fact]
	public async Task Appends_a_fragment_from_a_dictionary()
	{
		var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

		var url = await service.GenerateUrl("person-edit", Params(("personid", 5)), "https", "example.com", "section");

		Assert.Equal("https://example.com/person/5/edit#section", url);
	}

	[Fact]
	public async Task Appends_a_fragment_from_an_anonymous_object()
	{
		var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

		var url = await service.GenerateUrl("person-edit", new { personid = 5 }, "https", "example.com", "section");

		Assert.Equal("https://example.com/person/5/edit#section", url);
	}

	[Fact]
	public async Task Carries_the_query_string_through_the_absolute_overload()
	{
		var service = SpaRouteTestHost.Create(SpaRouteTestHost.DemoRoutes);

		// Excess parameters become a query string in the core path builder; the absolute overload
		// must not lose them when it prefixes scheme and host.
		var url = await service.GenerateUrl("person-show", Params(("personid", 5), ("tab", "info")), "http", "localhost:5000");

		Assert.Equal("http://localhost:5000/person/5?tab=info", url);
	}

	[Fact]
	public async Task Group_with_a_builder_nests_routes_under_the_group_path()
	{
		var service = SpaRouteTestHost.Create(routes => routes
			.Route("", "home")
			.Group("admin", "admin", admin => admin
				.Route("users", "users")
				.Route("users/{id}", "user")));

		Assert.Equal("/admin/users", await service.GenerateUrl("admin-users", Params()));
		Assert.Equal("/admin/users/7", await service.GenerateUrl("admin-user", Params(("id", 7))));
	}

	[Fact]
	public async Task Group_with_an_empty_path_keeps_the_parent_path()
	{
		var service = SpaRouteTestHost.Create(routes => routes
			.Group("", "bare", bare => bare.Route("thing", "thing")));

		// An empty group path contributes a name segment but no path segment, so the group itself
		// adds nothing to the URL.
		//
		// NOTE: the leading "//" is the CURRENT behaviour, and it looks wrong - a root-level group
		// with an empty path leaves the root's own empty FullPath in place, and the child then
		// concatenates "/" + "thing" onto it. Pinned rather than corrected: this suite is not allowed
		// to change shipped behaviour (NFR-4.1 in PRD-Coverage-Raise). If it is ever fixed to "/thing",
		// this assertion is the one to update, deliberately.
		Assert.Equal("//thing", await service.GenerateUrl("bare-thing", Params()));
	}

	[Fact]
	public async Task A_group_nested_inside_a_group_composes_both_segments()
	{
		// The top-level Group(...) is the route builder's; THIS exercises SpaRouteItem's own
		// Group-with-builder overload, which is only reached once a group nests inside another.
		var service = SpaRouteTestHost.Create(routes => routes
			.Group("admin", "admin", admin => admin
				.Group("users", "users", users => users
					.Route("", "list")
					.Route("{id}/edit", "edit"))));

		Assert.Equal("/admin/users", await service.GenerateUrl("admin-users-list", Params()));
		Assert.Equal("/admin/users/3/edit", await service.GenerateUrl("admin-users-edit", Params(("id", 3))));
	}

	[Fact]
	public async Task A_nested_group_with_an_empty_path_adds_only_a_name_segment()
	{
		var service = SpaRouteTestHost.Create(routes => routes
			.Group("admin", "admin", admin => admin
				.Group("", "bare", bare => bare.Route("thing", "thing"))));

		// The empty inner path contributes to the route NAME but not to the URL.
		Assert.Equal("/admin/thing", await service.GenerateUrl("admin-bare-thing", Params()));
	}

	[Fact]
	public void SpaRouteItem_ToString_is_the_full_name()
	{
		var item = new SpaServices.Routing.Data.SpaRouteItem { FullName = "person-edit" };

		Assert.Equal("person-edit", item.ToString());
	}

	[Fact]
	public void AddSpaPrerenderingService_registers_the_service_and_its_dependencies()
	{
		var services = new ServiceCollection();

		var returned = services.AddSpaPrerenderingService<StubPrerenderingService>();

		// Returns the same collection so it can be chained.
		Assert.Same(services, returned);

		var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();

		// The registration is scoped, and it pulls in the route services and the context accessor.
		Assert.IsType<StubPrerenderingService>(scope.ServiceProvider.GetRequiredService<ISpaPrerenderingService>());
		Assert.NotNull(scope.ServiceProvider.GetService<ISpaRouteService>());
		Assert.NotNull(scope.ServiceProvider.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>());
	}

	private sealed class StubPrerenderingService : ISpaPrerenderingService
	{
		public Task BuildRoutes(ISpaRouteBuilder routeBuilder) => Task.CompletedTask;

		public Task OnSupplyData(Microsoft.AspNetCore.Http.HttpContext context, IDictionary<string, object> data)
			=> Task.CompletedTask;
	}
}
