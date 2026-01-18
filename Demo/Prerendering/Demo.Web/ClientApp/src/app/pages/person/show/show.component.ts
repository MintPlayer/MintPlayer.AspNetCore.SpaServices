import { Component, inject, PLATFORM_ID, signal, ChangeDetectionStrategy } from '@angular/core';
import { Router, ActivatedRoute, RouterModule } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { isPlatformServer } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';
import { PERSON_TOKEN } from '../../../tokens';

@Component({
	selector: 'app-person-show',
	templateUrl: './show.component.html',
	styleUrls: ['./show.component.scss'],
	changeDetection: ChangeDetectionStrategy.OnPush,
	imports: [
		FormsModule,
		RouterModule,
		TranslateModule,
		SlugifyPipe
	],
	providers: [SlugifyPipe]
})
export class PersonShowComponent {
	private readonly personService = inject(PersonService);
	private readonly router = inject(Router);
	private readonly route = inject(ActivatedRoute);
	private readonly titleService = inject(Title);
	private readonly platformId = inject(PLATFORM_ID);
	private readonly personInj = inject(PERSON_TOKEN, { optional: true });

	person = signal<Person | null>(null);

	constructor() {
		if (isPlatformServer(this.platformId)) {
			this.setPerson(this.personInj!);
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
		this.person.set(person);
		if (person !== null) {
			this.titleService.setTitle(`${person.firstName} ${person.lastName}`);
		}
	}

	deletePerson() {
		const person = this.person();
		if (person) {
			this.personService.deletePerson(person).subscribe(() => {
				this.router.navigate(['/person']);
			});
		}
	}
}
