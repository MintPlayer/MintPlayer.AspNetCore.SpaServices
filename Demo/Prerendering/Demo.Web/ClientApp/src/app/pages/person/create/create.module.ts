import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';

import { PersonCreateRoutingModule } from './create-routing.module';
import { PersonCreateComponent } from './create.component';
import { PipesModule } from '../../../pipes/pipes.module';


@NgModule({
  declarations: [
    PersonCreateComponent
  ],
  imports: [
		CommonModule,
		FormsModule,
		PipesModule,
    PersonCreateRoutingModule
  ]
})
export class PersonCreateModule { }
