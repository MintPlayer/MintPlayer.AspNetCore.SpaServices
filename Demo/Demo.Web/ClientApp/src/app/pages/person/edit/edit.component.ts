import { Component, OnInit, Inject, PLATFORM_ID } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { isPlatformServer } from '@angular/common';

@Component({
	selector: 'app-person-edit',
	templateUrl: './edit.component.html',
	styleUrls: ['./edit.component.scss']
})
export class PersonEditComponent implements OnInit {

	constructor(private personService: PersonService, @Inject(PLATFORM_ID) private platformId: Object, @Inject('PERSON') private personInj: Person, private router: Router, private route: ActivatedRoute, private titleService: Title) {
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
			this.router.navigate(["/person", this.person.id]);
		});
	}

	person: Person = {
		id: 0,
		firstName: '',
		lastName: '',
	};
	oldPersonName: string = "";

	ngOnInit() {
	}

}
