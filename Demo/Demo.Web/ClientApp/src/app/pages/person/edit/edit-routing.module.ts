import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PersonEditComponent } from './edit.component';

const routes: Routes = [{ path: '', component: PersonEditComponent }];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class PersonEditRoutingModule { }
