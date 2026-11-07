import { InjectionToken } from '@angular/core';
import { Person } from './entities/person';

/**
 * Everything ISpaPrerenderingService.OnSupplyData put in its dictionary, as one object. Provided only while
 * the page is rendered on the server (main.server.ts, prerenderProviders); in the browser it is absent, so
 * inject it with { optional: true } and fall back to HTTP.
 */
export interface DataFromServer {
  message?: string;
  people?: Person[];
  person?: Person;
}

export const DATA_FROM_SERVER = new InjectionToken<DataFromServer>('DATA_FROM_SERVER');
