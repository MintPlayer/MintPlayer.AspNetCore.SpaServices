import { mergeApplicationConfig, ApplicationConfig, importProvidersFrom, provideBrowserGlobalErrorListeners } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { APP_BASE_HREF } from '@angular/common';
import { provideClientHydration, withEventReplay } from '@angular/platform-browser';
import { TranslateModule, TranslateLoader } from '@ngx-translate/core';
import { TranslateHttpLoader } from '@ngx-translate/http-loader';
import { appConfig } from './app.config';
import { MESSAGE_TOKEN } from './tokens';

const getBaseUrl = () => {
  return document.getElementsByTagName('base')[0].href.slice(0, -1);
}

const browserConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideClientHydration(withEventReplay()),
    { provide: APP_BASE_HREF, useFactory: getBaseUrl },
    importProvidersFrom(
      TranslateModule.forRoot({
        loader: {
          provide: TranslateLoader,
          useFactory: (http: HttpClient) => {
            return new TranslateHttpLoader(http);
          },
          deps: [
            HttpClient
          ]
        }
      })
    ),
    { provide: MESSAGE_TOKEN, useValue: 'Message from browser' }
  ]
};

export const config = mergeApplicationConfig(appConfig, browserConfig);
