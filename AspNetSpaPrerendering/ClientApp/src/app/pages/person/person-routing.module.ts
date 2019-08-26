import { NgModule } from '@angular/core';
import { Routes, RouterModule } from '@angular/router';
import { PersonListComponent } from './list/list.component';
import { PersonCreateComponent } from './create/create.component';
import { PersonEditComponent } from './edit/edit.component';
import { PersonShowComponent } from './show/show.component';

const routes: Routes = [
  { path: '', component: PersonListComponent },
  { path: 'create', component: PersonCreateComponent },
  { path: ':id/edit', component: PersonEditComponent },
  { path: ':id', component: PersonShowComponent }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class PersonRoutingModule { }
