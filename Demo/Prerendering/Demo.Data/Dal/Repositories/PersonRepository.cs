using Demo.Dtos.Dtos;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Pagination;
using MintPlayer.Pagination.Extensions;
using MintPlayer.SourceGenerators.Attributes;

namespace Demo.Data.Dal.Repositories;

internal interface IPersonRepository
{
	Task<PaginationResponse<Person>> PagePeople(PaginationRequest<Person> request);
	Task<IEnumerable<Person>> GetPeople(bool includeRelations = false);
	Task<Person> GetPerson(int id, bool includeRelations = false);
	Task<Person> InsertPerson(Person person);
	Task<Person> UpdatePerson(Person person);
	Task DeletePerson(int personId);
	Task SaveChangesAsync();
}

internal partial class PersonRepository : IPersonRepository
{
	[Inject] private readonly DemoContext demoContext;

	public async Task<PaginationResponse<Person>> PagePeople(PaginationRequest<Person> request)
	{
		var people = demoContext.People;

		// 1) Sort
		var sortedPeople = request.SortDirection == System.ComponentModel.ListSortDirection.Descending
			? people.OrderByDescending(request.SortProperty)
			: people.OrderBy(request.SortProperty);

		// 2) Page
		var pagedPeople = sortedPeople
			.Skip((request.Page - 1) * request.PerPage)
			.Take(request.PerPage);

		// 3) Convert to DTO
		var dtoPeople = pagedPeople.Select(p => ToDto(p));

		var countPeople = await people.CountAsync();
		return new PaginationResponse<Person>(request, countPeople, dtoPeople);
	}

	public Task<IEnumerable<Person>> GetPeople(bool includeRelations = false)
	{
		var people = demoContext.People
			.Select(p => ToDto(p));
		return Task.FromResult<IEnumerable<Person>>(people);
	}

	public async Task<Person> GetPerson(int id, bool includeRelations = false)
	{
		var person = await demoContext.People.FindAsync(id);
		return ToDto(person);
	}

	public async Task<Person> InsertPerson(Person person)
	{
		// 1) Convert to entity
		var entityPerson = ToEntity(person, demoContext);

		// 2) Add to database
		await demoContext.People.AddAsync(entityPerson);
		await demoContext.SaveChangesAsync();

		// 3) Convert to DTO
		var newPerson = ToDto(entityPerson);

		return newPerson;
	}

	public async Task<Person> UpdatePerson(Person person)
	{
		// 1) Find existing person
		var entityPerson = await demoContext.People
			.FindAsync(person.Id);

		// 2) Set new properties
		entityPerson.FirstName = person.FirstName;
		entityPerson.LastName = person.LastName;

		// 3) Update in database
		demoContext.Entry(entityPerson).State = EntityState.Modified;
		await demoContext.SaveChangesAsync();

		// 4) Convert to DTO
		var updatedPerson = ToDto(entityPerson);
		return updatedPerson;
	}

	public async Task DeletePerson(int personId)
	{
		// 1) Find existing person
		var entityPerson = await demoContext.People
			.FindAsync(personId);

		// 2) Delete from database
		demoContext.People.Remove(entityPerson);
	}

	public async Task SaveChangesAsync()
	{
		await demoContext.SaveChangesAsync();
	}

	#region Conversion methods
	internal static Person ToDto(Entities.Person person)
	{
		if (person == null)
		{
			return null;
		}
		else
		{
			return new Person
			{
				Id = person.Id,
				FirstName = person.FirstName,
				LastName = person.LastName
			};
		}
	}
	internal static Entities.Person ToEntity(Person person, DemoContext demoContext)
	{
		if (person == null)
		{
			return null;
		}
		else
		{
			return new Entities.Person
			{
				Id = person.Id,
				FirstName = person.FirstName,
				LastName = person.LastName
			};
		}
	}
	#endregion
}
