import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withPreloading, PreloadAllModules } from '@angular/router';
import { provideHttpClient, withNoXsrfProtection } from '@angular/common/http';
import { provideClientHydration, withEventReplay } from '@angular/platform-browser';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withNoXsrfProtection()),
    provideRouter(routes, withPreloading(PreloadAllModules)),
    provideClientHydration(withEventReplay())
  ]
};
