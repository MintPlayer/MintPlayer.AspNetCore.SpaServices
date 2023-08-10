import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { PersonShowComponent } from './show.component';

const routes: Routes = [{ path: '', component: PersonShowComponent }];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class PersonShowRoutingModule { }
