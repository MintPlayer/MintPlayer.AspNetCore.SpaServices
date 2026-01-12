import { Component, Inject, PLATFORM_ID, Optional } from '@angular/core';
import { RouterModule } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { isPlatformServer, CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';

@Component({
	selector: 'app-person-list',
	templateUrl: './list.component.html',
	styleUrls: ['./list.component.scss'],
	imports: [
		CommonModule,
		FormsModule,
		RouterModule,
		TranslateModule,
		SlugifyPipe
	],
	providers: [SlugifyPipe]
})
export class PersonListComponent {

	constructor(private personService: PersonService, private titleService: Title, @Inject(PLATFORM_ID) private platformId: Object, @Optional() @Inject('PEOPLE') private peopleInj?: Person[]) {
		this.titleService.setTitle('People');
		if (isPlatformServer(platformId)) {
			this.people = peopleInj!;
		} else {
			this.loadPeople();
		}
	}

	private loadPeople() {
		this.personService.getPeople(false).subscribe(people => {
			this.people = people;
		});
	}

	people: Person[] = [];
}
