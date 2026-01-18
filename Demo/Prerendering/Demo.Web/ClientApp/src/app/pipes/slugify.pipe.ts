import { Pipe, PipeTransform } from '@angular/core';

@Pipe({
	name: 'slugify'
})
export class SlugifyPipe implements PipeTransform {
	transform(value: any) {
		return value.toString().toLowerCase()
			.replace(/\s+/g, '-')                             // Replace spaces with -
			.normalize("NFD").replace(/[\u0300-\u036f]/g, "") // Remove diacritics
			.replace(/[^\w\-]+/g, '')                         // Remove all non-word chars
			.replace(/\-\-+/g, '-')                           // Replace multiple - with single -
			.replace(/^-+/, '')                               // Trim - from start of text
			.replace(/-+$/, '');                              // Trim - from end of text
	}
}
