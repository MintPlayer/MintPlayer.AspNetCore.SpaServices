using AspNetCoreSpaPrerendering.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AspNetCoreSpaPrerendering.Data
{
    internal class AspNetCoreSpaPrerenderingDbContext : DbContext
    {
        internal DbSet<Person> People { get; set; }

        public AspNetCoreSpaPrerenderingDbContext() : base()
        {
        }

        public AspNetCoreSpaPrerenderingDbContext(DbContextOptions options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            var connection_string = @"Server=(localdb)\mssqllocaldb;Database=AspNetCoreSpaPrerendering;Trusted_Connection=True;ConnectRetryCount=0";
            optionsBuilder.UseSqlServer(connection_string);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
        }
    }
}
