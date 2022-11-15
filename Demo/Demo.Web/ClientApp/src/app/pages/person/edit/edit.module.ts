import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';

import { PersonEditRoutingModule } from './edit-routing.module';
import { PersonEditComponent } from './edit.component';
import { PipesModule } from '../../../pipes/pipes.module';


@NgModule({
  declarations: [
    PersonEditComponent
  ],
  imports: [
		CommonModule,
		FormsModule,
		PipesModule,
    PersonEditRoutingModule
  ]
})
export class PersonEditModule { }
