import { Component, Inject, PLATFORM_ID } from '@angular/core';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { Router, ActivatedRoute } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { isPlatformServer } from '@angular/common';

@Component({
	selector: 'app-person-show',
	templateUrl: './show.component.html',
	styleUrls: ['./show.component.scss']
})
export class PersonShowComponent {

	constructor(private personService: PersonService, @Inject(PLATFORM_ID) private platformId: Object, @Inject('PERSON') private personInj: Person, private router: Router, private route: ActivatedRoute, private titleService: Title) {
		if (isPlatformServer(platformId)) {
			this.setPerson(personInj);
		} else {
			console.log(this.route.paramMap);
			const strId = this.route.snapshot.paramMap.get('id');
			if (strId) {
				const id = parseInt(strId);
				this.personService.getPerson(id, true).subscribe(person => {
					this.setPerson(person);
				});
			}
		}
	}

	private setPerson(person: Person) {
		this.person = person;
		if (person !== null) {
			this.titleService.setTitle(`${person.firstName} ${person.lastName}`);
		}
	}

	public deletePerson() {
		this.personService.deletePerson(this.person).subscribe(() => {
			this.router.navigate(['/person']);
		});
	}

	person: Person = {
		id: 0,
		firstName: '',
		lastName: '',
	};

}
