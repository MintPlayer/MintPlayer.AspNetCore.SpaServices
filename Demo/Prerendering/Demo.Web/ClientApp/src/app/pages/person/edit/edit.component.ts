import { isPlatformServer } from '@angular/common';
import { Component, inject, PLATFORM_ID, signal, ChangeDetectionStrategy } from '@angular/core';
import { form, FormField } from '@angular/forms/signals';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { TranslateModule } from '@ngx-translate/core';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { BsForDirective } from '@mintplayer/ng-bootstrap/for';
import { FocusOnLoadDirective } from '@mintplayer/ng-focus-on-load';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';
import { PERSON_TOKEN } from '../../../tokens';

@Component({
	selector: 'app-person-edit',
	templateUrl: './edit.component.html',
	styleUrls: ['./edit.component.scss'],
	changeDetection: ChangeDetectionStrategy.OnPush,
	imports: [
		FormField,
		TranslateModule,
		BsFormModule,
		BsGridModule,
		BsForDirective,
		BsButtonTypeDirective,
		FocusOnLoadDirective
	],
	providers: [SlugifyPipe]
})
export class PersonEditComponent {
	private readonly personService = inject(PersonService);
	private readonly platformId = inject(PLATFORM_ID);
	private readonly personInj = inject(PERSON_TOKEN, { optional: true });
	private readonly router = inject(Router);
	private readonly route = inject(ActivatedRoute);
	private readonly titleService = inject(Title);
	private readonly slugifyPipe = inject(SlugifyPipe);

	colors = Color;
	person = signal<Person>({
		id: 0,
		firstName: '',
		lastName: '',
	});
	personForm = form(this.person);
	oldPersonName = signal('');

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
		if (person !== null) {
			this.person.set(person);
			this.titleService.setTitle(`Edit person: ${person.firstName} ${person.lastName}`);
			this.oldPersonName.set(`${person.firstName} ${person.lastName}`);
		}
	}

	updatePerson() {
		const person = this.person();
		this.personService.updatePerson(person).subscribe(() => {
			this.router.navigate(["/person", person.id, this.slugifyPipe.transform(`${person.firstName} ${person.lastName}`)]);
		});
	}
}
