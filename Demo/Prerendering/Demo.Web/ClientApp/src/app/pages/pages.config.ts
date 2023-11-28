import { Routes } from '@angular/router';

export const ROUTES: Routes = [
	{ path: '', pathMatch: 'full', redirectTo: '/person' },
	{ path: 'person', loadChildren: () => import('./person/person.config').then(m => m.ROUTES) }
];
