import { Component, Inject, PLATFORM_ID, Optional } from '@angular/core';
import { Router, ActivatedRoute, RouterModule } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { isPlatformServer, CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';

@Component({
	selector: 'app-person-show',
	templateUrl: './show.component.html',
  styleUrls: ['./show.component.scss'],
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterModule,
    TranslateModule,
    SlugifyPipe
  ],
  providers: [SlugifyPipe]
})
export class PersonShowComponent {

  constructor(private personService: PersonService, private router: Router, private route: ActivatedRoute, private titleService: Title, @Inject(PLATFORM_ID) private platformId: Object, @Optional() @Inject('PERSON') private personInj?: Person) {
		if (isPlatformServer(platformId)) {
			this.setPerson(personInj!);
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
