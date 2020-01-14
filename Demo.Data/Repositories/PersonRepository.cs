using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using AspNetCoreSpaPrerendering.Data.Repositories.Interfaces;

namespace AspNetCoreSpaPrerendering.Data.Repositories
{
    internal class PersonRepository : IPersonRepository
    {
        private AspNetCoreSpaPrerenderingDbContext db_context;
        public PersonRepository(AspNetCoreSpaPrerenderingDbContext db_context)
        {
            this.db_context = db_context;
        }

        public IEnumerable<Dtos.Person> GetPeople()
        {
            var people = db_context.People
                .Select(person => ToDto(person));
            return people;
        }

        public IEnumerable<Dtos.Person> GetPeople(int count, int page)
        {
            var people = db_context.People
                .Skip((page - 1) * count)
                .Take(count)
                .Select(person => ToDto(person));
            return people;
        }

        public Dtos.Person GetPerson(int id)
        {
            var person = db_context.People
                .SingleOrDefault(p => p.Id == id);
            return ToDto(person);
        }

        public async Task<Dtos.Person> InsertPerson(Dtos.Person person)
        {
            // Convert to entity
            var entity_person = ToEntity(person, db_context);

            // Add to database
            db_context.People.Add(entity_person);
            await db_context.SaveChangesAsync();

            return ToDto(entity_person);
        }

        public async Task<Dtos.Person> UpdatePerson(Dtos.Person person)
        {
            // Find existing person
            var entity_person = db_context.People.Find(person.Id);

            // Set new properties
            entity_person.FirstName = person.FirstName;
            entity_person.LastName = person.LastName;

            // Update in database
            db_context.Entry(entity_person).State = EntityState.Modified;

            return ToDto(entity_person);
        }

        public async Task DeletePerson(int person_id)
        {
            // Find existing person
            var person = db_context.People.Find(person_id);

            // Delete
            db_context.People.Remove(person);
        }

        public async Task SaveChangesAsync()
        {
            await db_context.SaveChangesAsync();
        }

        #region Conversion methods
        internal static Dtos.Person ToDto(Entities.Person person)
        {
            if (person == null) return null;
            return new Dtos.Person
            {
                Id = person.Id,
                FirstName = person.FirstName,
                LastName = person.LastName
            };
        }
        internal static Entities.Person ToEntity(Dtos.Person person, AspNetCoreSpaPrerenderingDbContext db_context)
        {
            if (person == null) return null;
            return new Entities.Person
            {
                Id = person.Id,
                FirstName = person.FirstName,
                LastName = person.LastName
            };
        }
        #endregion
    }
}
