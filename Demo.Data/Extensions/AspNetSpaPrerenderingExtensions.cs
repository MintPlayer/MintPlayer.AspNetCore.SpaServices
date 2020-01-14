using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AspNetCoreSpaPrerendering.Data.Options;
using AspNetCoreSpaPrerendering.Data.Repositories;
using AspNetCoreSpaPrerendering.Data.Repositories.Interfaces;

namespace AspNetCoreSpaPrerendering.Data.Extensions
{
    public static class AspNetSpaPrerenderingExtensions
    {
        public static IServiceCollection AddServices(this IServiceCollection services, Action<AspNetSpaPrerenderingOptions> options)
        {
            var opt = new AspNetSpaPrerenderingOptions();
            options(opt);

            services.AddDbContext<AspNetCoreSpaPrerenderingDbContext>(db_options =>
            {
                db_options.UseSqlServer(opt.ConnectionString, b => b.MigrationsAssembly("AspNetCoreSpaPrerendering.Data"));
            });
            services.AddScoped<IPersonRepository, PersonRepository>();

            return services;
        }
    }
}
