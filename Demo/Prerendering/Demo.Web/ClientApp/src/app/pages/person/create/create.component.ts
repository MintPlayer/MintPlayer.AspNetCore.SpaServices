import { Component, Optional } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { SlugifyPipe } from '../../../pipes/slugify.pipe';

@Component({
	selector: 'app-person-create',
	templateUrl: './create.component.html',
  styleUrls: ['./create.component.scss'],
  standalone: true,
  imports: [
    FormsModule,
    TranslateModule,
    SlugifyPipe
  ]
})
export class PersonCreateComponent {

  constructor(private router: Router, private slugifyPipe: SlugifyPipe, @Optional() private personService?: PersonService) {
	}

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
