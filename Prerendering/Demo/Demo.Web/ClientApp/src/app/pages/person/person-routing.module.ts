import { NgModule } from '@angular/core';
import { Routes, RouterModule } from '@angular/router';

const routes: Routes = [
	{ path: '', loadChildren: () => import('./list/list.module').then(m => m.PersonListModule) },
	{ path: 'create', loadChildren: () => import('./create/create.module').then(m => m.PersonCreateModule) },
	{ path: ':id/:name', loadChildren: () => import('./show/show.module').then(m => m.PersonShowModule) },
	{ path: ':id/:name/edit', loadChildren: () => import('./edit/edit.module').then(m => m.PersonEditModule) },
];

@NgModule({
	imports: [RouterModule.forChild(routes)],
	exports: [RouterModule]
})
export class PersonRoutingModule { }
