import { Component, OnInit, Inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';

@Component({
  selector: 'app-person-edit',
  templateUrl: './edit.component.html',
  styleUrls: ['./edit.component.scss']
})
export class PersonEditComponent implements OnInit {

  constructor(private personService: PersonService, @Inject('PERSON') private personInj: Person, private router: Router, private route: ActivatedRoute, private titleService: Title) {
    if (personInj === null) {
      var id = parseInt(this.route.snapshot.paramMap.get("personid"));
      this.personService.getPerson(id, true).subscribe(person => {
        this.setPerson(person);
      });
    } else {
      this.setPerson(personInj);
    }
  }

  private setPerson(person: Person) {
    this.person = person;
    if (person !== null) {
      this.titleService.setTitle(`Edit person: ${person.firstName} ${person.lastName}`);
      this.oldPersonName = person.firstName + " " + person.lastName;
    }
  }

  updatePerson() {
    this.personService.updatePerson(this.person).subscribe(() => {
      this.router.navigate(["/manage", "members", 3, "person", this.person.id]);
    });
  }

  person: Person = new Person();
  oldPersonName: string = "";

  ngOnInit() {
  }

}
