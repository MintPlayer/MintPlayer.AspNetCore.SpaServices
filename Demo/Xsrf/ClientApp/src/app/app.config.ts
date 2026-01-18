import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient, withXsrfConfiguration } from '@angular/common/http';

import { routes } from './app.routes';
import { BASE_URL_TOKEN } from './tokens';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    // XSRF protection is enabled by default
    // Use withNoXsrfProtection to test the failing request
    provideHttpClient(withXsrfConfiguration({
      cookieName: 'XSRF-TOKEN',
      headerName: 'X-XSRF-TOKEN'
    })),
    //provideHttpClient(withNoXsrfProtection()),
    provideAnimations(),
    {
      provide: BASE_URL_TOKEN,
      useFactory: () => {
        let baseHref = document.getElementsByTagName('base')[0].href;

        // Trim the scheme
        baseHref = baseHref.replace(/^https?\:\/\//gi, '//');

        // Slice the trailing /
        if (baseHref.endsWith('/')) {
          baseHref = baseHref.slice(0, -1);
        }

        return baseHref;
      }
    }
  ]
};
