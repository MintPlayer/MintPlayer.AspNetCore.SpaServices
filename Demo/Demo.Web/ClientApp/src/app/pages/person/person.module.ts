import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { TranslateModule } from '@ngx-translate/core';

import { PersonRoutingModule } from './person-routing.module';
import { PersonCreateComponent } from './create/create.component';
import { PersonEditComponent } from './edit/edit.component';
import { PersonShowComponent } from './show/show.component';
import { PersonListComponent } from './list/list.component';


@NgModule({
  declarations: [
    PersonCreateComponent,
    PersonEditComponent,
    PersonShowComponent,
    PersonListComponent
  ],
  imports: [
    CommonModule,
    FormsModule,
    TranslateModule,
    PersonRoutingModule
  ]
})
export class PersonModule { }
