using Demo.Data.Dal.Repositories;
using Demo.Dtos.Dtos;
using MintPlayer.Pagination;
using MintPlayer.SourceGenerators.Attributes;

namespace Demo.Data.Dal.Services;

public interface IPersonService
{
	Task<PaginationResponse<Person>> PagePeople(PaginationRequest<Person> request);
	Task<IEnumerable<Person>> GetPeople(bool includeRelations = false);
	Task<Person> GetPerson(int id, bool includeRelations = false);
	Task<Person> InsertPerson(Person person);
	Task<Person> UpdatePerson(Person person);
	Task DeletePerson(int personId);
}

internal partial class PersonService : IPersonService
{
	[Inject] private readonly IPersonRepository personRepository;

	public Task<PaginationResponse<Person>> PagePeople(PaginationRequest<Person> request)
	{
		return personRepository.PagePeople(request);
	}

	public Task<IEnumerable<Person>> GetPeople(bool includeRelations = false)
	{
		return personRepository.GetPeople(includeRelations);
	}

	public Task<Person> GetPerson(int id, bool includeRelations = false)
	{
		return personRepository.GetPerson(id, includeRelations);
	}

	public Task<Person> InsertPerson(Person person)
	{
		return personRepository.InsertPerson(person);
	}

	public Task<Person> UpdatePerson(Person person)
	{
		return personRepository.UpdatePerson(person);
	}

	public async Task DeletePerson(int personId)
	{
		await personRepository.DeletePerson(personId);
		await personRepository.SaveChangesAsync();
	}
}
