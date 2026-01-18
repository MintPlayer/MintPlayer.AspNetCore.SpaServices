import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
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
		FormsModule,
		TranslateModule,
		BsGridModule
	],
	providers: [SlugifyPipe]
})
export class PersonCreateComponent {
	private readonly router = inject(Router);
	private readonly slugifyPipe = inject(SlugifyPipe);
	private readonly personService = inject(PersonService, { optional: true });

	person: Person = {
		id: 0,
		firstName: '',
		lastName: '',
	};

	savePerson() {
		if (this.personService) {
			this.personService.createPerson(this.person).subscribe((person) => {
				this.router.navigate(['/person', person.id, this.slugifyPipe.transform(`${person.firstName} ${person.lastName}`)]);
			});
		}
	}
}
