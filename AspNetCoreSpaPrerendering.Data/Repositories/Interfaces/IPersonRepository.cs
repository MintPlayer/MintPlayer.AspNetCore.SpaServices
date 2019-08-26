using System.Collections.Generic;
using System.Threading.Tasks;

namespace AspNetCoreSpaPrerendering.Data.Repositories.Interfaces
{
    public interface IPersonRepository
    {
        IEnumerable<Dtos.Person> GetPeople();
        IEnumerable<Dtos.Person> GetPeople(int count, int page);
        Dtos.Person GetPerson(int id);
        Task<Dtos.Person> InsertPerson(Dtos.Person person);
        Task<Dtos.Person> UpdatePerson(Dtos.Person person);
        Task DeletePerson(int person_id);
        Task SaveChangesAsync();
    }
}
