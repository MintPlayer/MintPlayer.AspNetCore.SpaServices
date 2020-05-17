using Demo.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Demo.Data
{
    internal class DemoContext : DbContext
    {
        // dotnet ef migrations add AddPeople --project ..\Demo.Data
        // dotnet ef database update --project ..\Demo.Data

        private readonly IConfiguration configuration;
        public DemoContext(IConfiguration configuration) : base()
        {
            this.configuration = configuration;
        }

        internal DbSet<Person> People { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.UseSqlServer(configuration.GetConnectionString("Demo"));
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        }
    }
}
