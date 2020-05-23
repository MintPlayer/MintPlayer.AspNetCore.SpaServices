import { Injectable, Inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Person } from '../entities/person';

@Injectable({
  providedIn: 'root'
})
export class PersonService {
  constructor(private httpClient: HttpClient, @Inject('BASE_URL') private baseUrl: string) {
  }

  public getPeople(include_relations: boolean, count: number = 20, page: number = 1) {
    return this.httpClient.get<Person[]>(`${this.baseUrl}/api/person`, {
      headers: {
        'include_relations': String(include_relations),
        'count': String(count),
        'page': String(page)
      }
    });
  }

  public getPerson(id: number, include_relations: boolean) {
    return this.httpClient.get<Person>(`${this.baseUrl}/api/person/${id}`, {
      headers: {
        'include_relations': String(include_relations)
      }
    });
  }

  public createPerson(person: Person) {
    return this.httpClient.post<Person>(`${this.baseUrl}/api/person`, { person });
  }

  public updatePerson(person: Person) {
    return this.httpClient.put(`${this.baseUrl}/api/person/${person.id}`, { person });
  }

  public deletePerson(person: Person) {
    return this.httpClient.delete(`${this.baseUrl}/api/person/${person.id}`);
  }
}
