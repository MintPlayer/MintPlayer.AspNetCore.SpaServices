import { ApplicationConfig } from '@angular/core';
import { provideRouter, withPreloading, PreloadAllModules } from '@angular/router';
import { provideHttpClient, withNoXsrfProtection } from '@angular/common/http';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideHttpClient(withNoXsrfProtection()),
    provideRouter(routes, withPreloading(PreloadAllModules)),
  ]
};
