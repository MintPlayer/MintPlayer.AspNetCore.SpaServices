import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, withPreloading, PreloadAllModules } from '@angular/router';
import { provideHttpClient, withFetch, withNoXsrfProtection } from '@angular/common/http';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    // withFetch: Angular recommends it for SSR, and logs NG02801 on every server render without it.
    provideHttpClient(withFetch(), withNoXsrfProtection()),
    provideRouter(routes, withPreloading(PreloadAllModules)),
  ]
};
