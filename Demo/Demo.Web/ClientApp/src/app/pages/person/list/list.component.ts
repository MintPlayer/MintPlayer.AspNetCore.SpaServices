import { Component, OnInit, Inject } from '@angular/core';
import { Person } from '../../../entities/person';
import { PersonService } from '../../../services/person.service';
import { Title } from '@angular/platform-browser';

@Component({
  selector: 'app-person-list',
  templateUrl: './list.component.html',
  styleUrls: ['./list.component.scss']
})
export class PersonListComponent implements OnInit {

  constructor(private personService: PersonService, @Inject('SERVERSIDE') private serverSide: boolean, @Inject('PEOPLE') private peopleInj: Person[], private titleService: Title) {
    this.titleService.setTitle('People');
    if (serverSide === false) {
      this.loadPeople();
    } else {
      this.people = peopleInj;
    }
  }

  private loadPeople() {
    this.personService.getPeople(false).subscribe(people => {
      this.people = people;
    });
  }

  people: Person[] = [];

  ngOnInit() {
  }

}
