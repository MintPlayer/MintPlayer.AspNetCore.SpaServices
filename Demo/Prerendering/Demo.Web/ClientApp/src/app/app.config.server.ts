import { of } from 'rxjs';
import { mergeApplicationConfig, ApplicationConfig, importProvidersFrom } from '@angular/core';
import { provideServerRendering } from '@angular/platform-server';
import { TranslateModule, TranslateLoader } from '@ngx-translate/core';
import { appConfig } from './app.config';
import { MESSAGE_TOKEN } from './tokens';

import * as translationEn from '../assets/i18n/en.json';
import * as translationNl from '../assets/i18n/nl.json';

class TranslateJsonLoader implements TranslateLoader {
  public getTranslation(lang: string) {
    switch (lang) {
      case 'nl': return of(translationNl);
      default: return of(translationEn);
    }
  }
}

const serverConfig: ApplicationConfig = {
  providers: [
    provideServerRendering(),
    importProvidersFrom(
      TranslateModule.forRoot({
        loader: {
          provide: TranslateLoader,
          useFactory: () => {
            return new TranslateJsonLoader();
          }
        }
      })
    ),
    { provide: MESSAGE_TOKEN, useValue: 'MESS_FROM_SERV' }
  ]
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
