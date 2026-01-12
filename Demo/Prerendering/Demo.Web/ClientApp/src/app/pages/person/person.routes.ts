import { Routes } from '@angular/router';

export const ROUTES: Routes = [
	{ path: '', loadComponent: () => import('./list/list.component').then(m => m.PersonListComponent) },
	{ path: 'create', loadComponent: () => import('./create/create.component').then(m => m.PersonCreateComponent) },
	{ path: ':id/:name', loadComponent: () => import('./show/show.component').then(m => m.PersonShowComponent) },
	{ path: ':id/:name/edit', loadComponent: () => import('./edit/edit.component').then(m => m.PersonEditComponent) },
];
