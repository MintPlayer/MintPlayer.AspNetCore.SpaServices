import { CommonModule } from "@angular/common";
import { NgModule } from "@angular/core";
import { SlugifyPipe } from "./slugify.pipe";

@NgModule({
	declarations: [ SlugifyPipe ],
	imports: [ CommonModule ],
	providers: [ SlugifyPipe ],
	exports: [ SlugifyPipe ]
})
export class PipesModule { }
