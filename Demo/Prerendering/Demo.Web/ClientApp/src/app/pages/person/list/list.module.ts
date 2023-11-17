import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TranslateModule } from '@ngx-translate/core';

import { PersonListRoutingModule } from './list-routing.module';
import { PersonListComponent } from './list.component';
import { PipesModule } from '../../../pipes/pipes.module';


@NgModule({
  declarations: [
    PersonListComponent
  ],
  imports: [
		CommonModule,
		PipesModule,
		TranslateModule,
    PersonListRoutingModule
  ]
})
export class PersonListModule { }
