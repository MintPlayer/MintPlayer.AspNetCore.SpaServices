import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';

import { PersonRoutingModule } from './person-routing.module';
import { CreateComponent } from './create/create.component';
import { EditComponent } from './edit/edit.component';
import { ShowComponent } from './show/show.component';
import { ListComponent } from './list/list.component';


@NgModule({
  declarations: [CreateComponent, EditComponent, ShowComponent, ListComponent],
  imports: [
    CommonModule,
    PersonRoutingModule
  ],
  exports: [CreateComponent, EditComponent, ShowComponent, ListComponent]
})
export class PersonModule { }
