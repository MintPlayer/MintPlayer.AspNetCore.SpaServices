import { InjectionToken } from '@angular/core';
import { Person } from './entities/person';

export const MESSAGE_TOKEN = new InjectionToken<string>('MESSAGE');
export const PERSON_TOKEN = new InjectionToken<Person>('PERSON');
export const PEOPLE_TOKEN = new InjectionToken<Person[]>('PEOPLE');
