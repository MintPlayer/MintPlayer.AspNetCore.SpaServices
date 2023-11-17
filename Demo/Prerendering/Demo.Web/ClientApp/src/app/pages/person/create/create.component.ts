import { Component } from '@angular/core';
import { Router } from '@angular/router';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';

@Component({
	selector: 'app-person-create',
	templateUrl: './create.component.html',
	styleUrls: ['./create.component.scss']
})
export class PersonCreateComponent {

	constructor(private personService: PersonService, private router: Router, private slugifyPipe: SlugifyPipe) {
	}

	person: Person = {
		id: 0,
		firstName: '',
		lastName: '',
	};

	savePerson() {
		this.personService.createPerson(this.person).subscribe((person) => {
			this.router.navigate(['/person', person.id, this.slugifyPipe.transform(`${person.firstName} ${person.lastName}`)]);
		});
	}

}
