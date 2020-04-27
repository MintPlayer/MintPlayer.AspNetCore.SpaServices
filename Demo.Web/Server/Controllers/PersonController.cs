using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using AspNetCoreSpaPrerendering.Data.Dtos;
using AspNetCoreSpaPrerendering.Data.Repositories.Interfaces;
using AspNetSpaPrerendering.Server.ViewModels.Person;
using Spa.SpaRoutes.CurrentSpaRoute;

namespace AspNetSpaPrerendering.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PersonController : Controller
    {
        private IPersonRepository personRepository;
        private ISpaRouteService spaRouteService;
        public PersonController(IPersonRepository personRepository, ISpaRouteService spaRouteService)
        {
            this.personRepository = personRepository;
            this.spaRouteService = spaRouteService;
        }

        // GET: api/Person
        [HttpGet]
        public IEnumerable<Person> Get()
        {
            var people = personRepository.GetPeople();
            return people.ToList();
        }

        // GET: api/Person/5
        [HttpGet("{id}", Order = 1)]
        public Person Get(int id)
        {
            var person = personRepository.GetPerson(id);

            //var parms = new Dictionary<string, object>();
            //parms["memberid"] = 2;
            //parms["personid"] = 5;
            //parms["action"] = "show";

            var parms = new
            {
                memberid = 2,
                personid = 5,
                action = "show"
            };
            var route = spaRouteService.GenerateUrl("manage-members-person-edit", parms);

            return person;
        }

        // POST: api/Person
        [HttpPost]
        public async Task<Person> Post([FromBody] PersonCreateVM personCreateVM)
        {
            var person = await personRepository.InsertPerson(personCreateVM.Person);
            return person;
        }

        // PUT: api/Person/5
        [HttpPut("{id}")]
        public async Task Put(int id, [FromBody] PersonUpdateVM personUpdateVM)
        {
            await personRepository.UpdatePerson(personUpdateVM.Person);
            await personRepository.SaveChangesAsync();
        }

        // DELETE: api/ApiWithActions/5
        [HttpDelete("{id}")]
        public async Task Delete(int id)
        {
            await personRepository.DeletePerson(id);
            await personRepository.SaveChangesAsync();
        }
    }
}