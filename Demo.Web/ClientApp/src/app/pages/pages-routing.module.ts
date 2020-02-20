import { NgModule } from '@angular/core';
import { Routes, RouterModule } from '@angular/router';


const routes: Routes = [
  { path: '', redirectTo: '/manage/members/3/person', pathMatch: 'full' },
  { path: 'manage/members/:memberid/person', loadChildren: './person/person.module#PersonModule' }
];

@NgModule({
  imports: [RouterModule.forChild(routes)],
  exports: [RouterModule]
})
export class PagesRoutingModule { }
