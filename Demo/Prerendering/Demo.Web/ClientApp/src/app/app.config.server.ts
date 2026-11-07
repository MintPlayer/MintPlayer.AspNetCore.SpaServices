import { of } from 'rxjs';
import { mergeApplicationConfig, ApplicationConfig, importProvidersFrom } from '@angular/core';
import { provideServerRendering, withRoutes, RenderMode } from '@angular/ssr';
import { TranslateModule, TranslateLoader } from '@ngx-translate/core';
import { appConfig } from './app.config';

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
    // RenderMode.Server: nothing is prerendered at build time, so dist/browser keeps a plain template
    // (index.csr.html) for ASP.NET Core to capture and hand to the renderer.
    provideServerRendering(withRoutes([{ path: '**', renderMode: RenderMode.Server }])),
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
    // MESSAGE_TOKEN is not provided here: an application-level provider would shadow the platform provider
    // main.server.ts's prerenderProviders creates from the data ASP.NET Core supplied.
  ]
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
