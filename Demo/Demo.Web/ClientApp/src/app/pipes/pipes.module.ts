import { CommonModule } from "@angular/common";
import { NgModule } from "@angular/core";
import { SlugifyPipe } from "./slugify.pipe";

@NgModule({
  declarations: [
    SlugifyPipe
  ],
  imports: [
    CommonModule
  ],
  exports: [
    SlugifyPipe
  ]
})
export class PipesModule { }
