# MintPlayer.AspNetCore.SpaServices.Routing
This project facilitates server-side prerendering in ASP.NET Core.

## Server-side rendering
If you haven't setup SSR yet, please consult [this manual](https://medium.com/@pieterjandeclippel/server-side-rendering-in-asp-net-core-angular-6df7adacbdaa).

## Installation
### NuGet package manager
Open the NuGet package manager and install `MintPlayer.AspNetCore.SpaServices.Routing` in your project
### Package manager console
Install-Package MintPlayer.AspNetCore.SpaServices.Routing

## Usage
### Register SPA routes
The ASP.NET Core application needs to be aware of your angular/react SPA routes.
Therefor you need to provide these with the SpaRouteBuilder. For example:

    public void ConfigureServices(IServiceCollection services)
    {
        // Define the SPA-routes for our helper
        services.AddSpaRoutes(routes => routes
            .Route("", "home")
            .Group("person", "person", person_routes => person_routes
                .Route("", "list")
                .Route("create", "create")
                .Route("{id}", "show")
                .Route("{id}/edit", "edit")
            )
        );
    }
    
You can define routing parameters in your paths as well.

### Adding SPA prerendering middleware
To enable SPA prerendering you'd normally use the following middleware registration code:

    app.UseSpa(spa =>
    {
        ...

        spa.UseSpaPrerendering(options =>
        {
            options.BootModulePath = $"{spa.Options.SourcePath}/dist/server/main.js";
            options.BootModuleBuilder = env.IsDevelopment()
                ? new AngularCliBuilder(npmScript: "build:ssr")
                : null;
            options.ExcludeUrls = new[] { "/sockjs-node" };
        });

        ...
    });
    
### Supplying data
You probably want to pass data based on which url the visitor opens the first time.
With this package you can easily determine which angular component is to be rendered and what data needs to be provided to the angular app.

    public void Configure(IApplicationBuilder app, IHostingEnvironment env, ISpaRouteService spaRouteService)
    {
        ...

        app.UseSpa(spa =>
        {
            ...
            
            spa.UseSpaPrerendering(options =>
            {
                ...

                options.SupplyData = (context, data) =>
                {
                    var route = spaRouteService.GetCurrentRoute(context);
                    var personRepository = context.RequestServices.GetRequiredService<IPersonRepository>();

                    switch (route?.Name)
                    {
                        case "person-list":
                            {
                                var people = personRepository.GetPeople();
                                data["people"] = people;
                            }
                            break;
                        case "person-show":
                        case "person-edit":
                            {
                                var id = System.Convert.ToInt32(route.Parameters["id"]);
                                var person = personRepository.GetPerson(id);
                                data["person"] = person;
                            }
                            break;
                    }
                };
            });
        }
    }

You can't perform dependecy injection here since the SupplyData is a delegate.
You can however retrieve an instance from the service-container through `context.RequestServices` or `context.ApplicationServices`.

### main.server.ts
The data you passed in the SupplyData delegate is made available on the params.data object in the `main.server.ts`.
The refactored code can look like this:

    const providers: StaticProvider[] = [
      provideModuleMap(LAZY_MODULE_MAP),
      { provide: APP_BASE_HREF, useValue: params.baseUrl },
      { provide: 'BASE_URL', useValue: params.origin + params.baseUrl },
      { provide: 'MESSAGE', useValue: params.data.message }
    ];

    if ('people' in params.data) {
      providers.push({ provide: 'PEOPLE', useValue: params.data.people })
    }
    if ('person' in params.data) {
      providers.push({ provide: 'PERSON', useValue: params.data.person })
    }

    const options = {
      document: params.data.originalHtml,
      url: params.url,
      extraProviders: providers
    };

### main.ts
Each key you pass in the main.server.ts must also be provided in the main.ts:

    const providers = [
      { provide: 'BASE_URL', useFactory: getBaseUrl, deps: [] },
      { provide: 'MESSAGE', useValue: 'Message from the client' },
      { provide: 'PEOPLE', useValue: null },
      { provide: 'PERSON', useValue: null }
    ];

### Use in components
You can then use this value by using dependency injection in your components:

    constructor(private personService: PersonService, @Inject('PERSON') private personInj: Person, private route: ActivatedRoute) {
      if (personInj === null) {
        var id = parseInt(this.route.snapshot.paramMap.get("id"));
        this.personService.getPerson(id, true).subscribe(person => {
          this.setPerson(person);
        });
      } else {
        this.setPerson(personInj);
      }
    }

### Generate SPA routes
If necessary, you can generate an application URL on the server-side through c# code. Examples  for this use are when using a redirect from OpenSearch straight to your ShowComponent, or when generating an XML sitemap.

To do so, there are 2 approaches:

#### Using a dictionary:

    var parms = new Dictionary<string, object>();
    parms["id"] = 5;
    var route = spaRouteService.GenerateUrl("person-edit", parms);

#### Using an anonymous type:

    var route = spaRouteService.GenerateUrl("person-edit", new {
        id = 5
    });
