using System.Collections.Generic;
using System.Threading.Tasks;
using Demo.Data.Dal.Services;
using Demo.Dtos.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MintPlayer.Pagination;

namespace Demo.Web.Controllers
{
	[ApiController]
    [Route("[controller]")]
    public class PersonController : Controller
    {
        private readonly ILogger<PersonController> logger;
        private readonly IPersonService personService;

        public PersonController(ILogger<PersonController> logger, IPersonService personService)
        {
            this.logger = logger;
            this.personService = personService;
		}

		// POST: web/Person/page
		[HttpPost("page", Name = "web-v1-person-page")]
		public async Task<ActionResult<PaginationResponse<Person>>> PagePeople([FromBody] PaginationRequest<Person> request)
		{
			var people = await personService.PagePeople(request);
			return Ok(people);
		}

		// GET: web/Person
		[HttpGet(Name = "web-v1-person-list")]
		public async Task<ActionResult<IEnumerable<Person>>> Get([FromHeader]bool include_relations = false)
		{
			var people = await personService.GetPeople(include_relations);
			return Ok(people);
		}

		// GET: web/Person/5
		[HttpGet("{id}", Name = "web-v1-person-get", Order = 1)]
		public async Task<ActionResult<Person>> Get(int id, [FromHeader]bool include_relations = false)
		{
			var person = await personService.GetPerson(id, include_relations);
			return Ok(person);
		}

		// POST: web/Person
		[HttpPost(Name = "web-v1-person-create")]
		public async Task<ActionResult<Person>> Post([FromBody] Person person)
		{
			var new_person = await personService.InsertPerson(person);
			return Ok(new_person);
		}

		// PUT: web/Person/5
		[HttpPut("{id}", Name = "web-v1-person-update")]
		public async Task<ActionResult<Person>> Put(int id, [FromBody] Person person)
		{
			var updated_person = await personService.UpdatePerson(person);
			return Ok(updated_person);
		}

		// DELETE: web/Person/5
		[HttpDelete("{id}", Name = "web-v1-person-delete")]
		public async Task<ActionResult> Delete(int id)
		{
			await personService.DeletePerson(id);
			return Ok();
		}
	}
}
