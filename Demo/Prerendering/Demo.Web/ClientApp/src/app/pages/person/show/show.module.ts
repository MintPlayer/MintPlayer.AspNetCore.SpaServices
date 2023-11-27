import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TranslateModule } from '@ngx-translate/core';

import { PersonShowRoutingModule } from './show-routing.module';
import { PersonShowComponent } from './show.component';
import { PipesModule } from '../../../pipes/pipes.module';


@NgModule({
  declarations: [
    PersonShowComponent
  ],
  imports: [
		CommonModule,
		TranslateModule,
		PipesModule,
    PersonShowRoutingModule
  ]
})
export class PersonShowModule { }
