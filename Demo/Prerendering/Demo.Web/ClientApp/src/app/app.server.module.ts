import { of } from 'rxjs';
import { NgModule } from '@angular/core';
import { ServerModule } from '@angular/platform-server';
import { TranslateModule, TranslateLoader } from '@ngx-translate/core';

import { AppModule } from './app.module';
import { AppComponent } from './app.component';

import * as translationEn from '../assets/i18n/en.json';
import * as translationNl from '../assets/i18n/nl.json';

@NgModule({
	imports: [
		AppModule,
		ServerModule,
		TranslateModule.forRoot({
			loader: {
				provide: TranslateLoader,
				useFactory: () => {
					return new TranslateJsonLoader();
				}
			}
		})
	],
	bootstrap: [AppComponent],
})
export class AppServerModule { }


export class TranslateJsonLoader implements TranslateLoader {
	constructor() {
	}

	public getTranslation(lang: string) {
		switch (lang) {
			case 'nl': {
				return of(translationNl);
			} break;
			default: {
				return of(translationEn);
			} break;
		}
	}
}
