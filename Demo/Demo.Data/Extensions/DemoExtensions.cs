using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Demo.Data.Dal.Repositories;
using Demo.Data.Dal.Services;
using Demo.Data.Options;

namespace Demo.Data.Extensions
{
    public static class DemoExtensions
    {
        public static IServiceCollection AddDemo(this IServiceCollection services, Action<DemoOptions> options)
        {
            var opt = new DemoOptions();
            options(opt);
            
            return services
                .AddDbContext<DemoContext>(db_options =>
                {
                    db_options.UseSqlServer(opt.ConnectionString, b => b.MigrationsAssembly("Demo.Data"));
                })
                .AddScoped<IPersonRepository, PersonRepository>()
                .AddScoped<IPersonService, PersonService>();
        }
    }
}
