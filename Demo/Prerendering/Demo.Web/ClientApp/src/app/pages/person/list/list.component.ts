import { Component, inject, PLATFORM_ID, signal, ChangeDetectionStrategy } from '@angular/core';
import { RouterModule } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { isPlatformServer } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';
import { DATA_FROM_SERVER } from '../../../tokens';

@Component({
	selector: 'app-person-list',
	templateUrl: './list.component.html',
	styleUrls: ['./list.component.scss'],
	changeDetection: ChangeDetectionStrategy.OnPush,
	imports: [
		FormsModule,
		RouterModule,
		TranslateModule,
		SlugifyPipe,
		BsGridModule
	],
	providers: [SlugifyPipe]
})
export class PersonListComponent {
	private readonly personService = inject(PersonService);
	private readonly titleService = inject(Title);
	private readonly platformId = inject(PLATFORM_ID);
	private readonly peopleInj = inject(DATA_FROM_SERVER, { optional: true })?.people ?? null;

	people = signal<Person[]>([]);

	constructor() {
		this.titleService.setTitle('People');
		if (isPlatformServer(this.platformId)) {
			this.people.set(this.peopleInj ?? []);
		} else {
			this.loadPeople();
		}
	}

	private loadPeople() {
		this.personService.getPeople(false).subscribe(people => {
			this.people.set(people);
		});
	}
}
