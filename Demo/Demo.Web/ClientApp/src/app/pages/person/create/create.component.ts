import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';

@Component({
	selector: 'app-person-create',
	templateUrl: './create.component.html',
	styleUrls: ['./create.component.scss']
})
export class PersonCreateComponent implements OnInit {

	constructor(private personService: PersonService, private router: Router) {
	}

	person: Person = {
		id: 0,
		firstName: '',
		lastName: '',
	};

	savePerson() {
		this.personService.createPerson(this.person).subscribe((person) => {
			this.router.navigate(["/person", person.id]);
		});
	}

	ngOnInit() {
	}

}
