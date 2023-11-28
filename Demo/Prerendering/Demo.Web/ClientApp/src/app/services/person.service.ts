import { APP_BASE_HREF } from '@angular/common';
import { Injectable, Inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Person } from '../entities/person';

@Injectable({
	providedIn: 'root'
})
export class PersonService {
	constructor(private httpClient: HttpClient, @Inject(APP_BASE_HREF) private baseUrl: string) {
	}

	public getPeople(include_relations: boolean, count: number = 20, page: number = 1) {
		console.log(this.baseUrl);
		return this.httpClient.get<Person[]>(`${this.baseUrl}/web/person`, {
			headers: {
				'include_relations': String(include_relations),
				'count': String(count),
				'page': String(page)
			}
		});
	}

	public getPerson(id: number, include_relations: boolean) {
		return this.httpClient.get<Person>(`${this.baseUrl}/web/person/${id}`, {
			headers: {
				'include_relations': String(include_relations)
			}
		});
	}

	public createPerson(person: Person) {
    return this.httpClient.post<Person>(`${this.baseUrl}/web/person`, person);
	}

	public updatePerson(person: Person) {
		return this.httpClient.put(`${this.baseUrl}/web/person/${person.id}`, person);
	}

	public deletePerson(person: Person) {
		return this.httpClient.delete(`${this.baseUrl}/web/person/${person.id}`);
	}
}
