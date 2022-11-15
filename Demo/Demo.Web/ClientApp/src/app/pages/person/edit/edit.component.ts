import { isPlatformServer } from '@angular/common';
import { Component, Inject, PLATFORM_ID } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';

@Component({
	selector: 'app-person-edit',
	templateUrl: './edit.component.html',
	styleUrls: ['./edit.component.scss']
})
export class PersonEditComponent {

	constructor(private personService: PersonService, @Inject(PLATFORM_ID) private platformId: Object, @Inject('PERSON') private personInj: Person, private router: Router, private route: ActivatedRoute, private titleService: Title, private slugifyPipe: SlugifyPipe) {
		if (isPlatformServer(platformId)) {
			this.setPerson(personInj);
		} else {
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
			this.titleService.setTitle(`Edit person: ${person.firstName} ${person.lastName}`);
			this.oldPersonName = `${person.firstName} ${person.lastName}`;
		}
	}

	updatePerson() {
		this.personService.updatePerson(this.person).subscribe(() => {
			this.router.navigate(["/person", this.person.id, this.slugifyPipe.transform(`${this.person.firstName} ${this.person.lastName}`)]);
		});
	}

	person: Person = {
		id: 0,
		firstName: '',
		lastName: '',
	};
	oldPersonName: string = '';

}
