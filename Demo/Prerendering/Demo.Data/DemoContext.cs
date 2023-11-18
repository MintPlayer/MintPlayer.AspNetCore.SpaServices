using Demo.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Demo.Data;

internal class DemoContext : DbContext
{
	// From Data project:
	// dotnet ef migrations add AddPeople
	// dotnet ef database update
	// From Web project:
	// dotnet ef migrations script --output "MigrationScripts\AddPeople.sql" --context DemoContext

	private readonly IConfiguration? configuration;
	public DemoContext(IConfiguration? configuration) : base()
	{
		this.configuration = configuration;
	}

	internal DbSet<Person> People { get; set; }

	protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
	{
		base.OnConfiguring(optionsBuilder);
		if (configuration == null)
		{
			// Only used when generating migrations
			var migrationsConnectionString = @"Server=(localdb)\mssqllocaldb;Database=RoutingDemo;Trusted_Connection=True;ConnectRetryCount=0";
			optionsBuilder.UseSqlServer(migrationsConnectionString, options => options.MigrationsAssembly("Demo.Data"));
		}
		else
		{
			optionsBuilder.UseSqlServer(configuration.GetConnectionString("Demo"));
		}
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
	}
}
