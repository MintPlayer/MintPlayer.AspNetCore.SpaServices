import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { form, FormField } from '@angular/forms/signals';
import { Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsForDirective } from '@mintplayer/ng-bootstrap/for';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';

@Component({
	selector: 'app-person-create',
	templateUrl: './create.component.html',
	styleUrls: ['./create.component.scss'],
	changeDetection: ChangeDetectionStrategy.OnPush,
	imports: [
		FormField,
		TranslateModule,
		BsButtonTypeDirective,
		BsForDirective,
		BsFormModule,
		BsGridModule
	],
	providers: [SlugifyPipe]
})
export class PersonCreateComponent {
	private readonly router = inject(Router);
	private readonly slugifyPipe = inject(SlugifyPipe);
	private readonly personService = inject(PersonService);

	colors = Color;
	person = signal<Person>({
		id: 0,
		firstName: '',
		lastName: '',
	});
	personForm = form(this.person);

	savePerson() {
		this.personService.createPerson(this.person()).subscribe((createdPerson) => {
			this.router.navigate(['/person', createdPerson.id, this.slugifyPipe.transform(`${createdPerson.firstName} ${createdPerson.lastName}`)]);
		});
	}
}
