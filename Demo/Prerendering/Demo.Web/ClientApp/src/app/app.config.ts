import { ApplicationConfig, importProvidersFrom } from '@angular/core';
import { provideRouter, withPreloading, PreloadAllModules } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { provideHttpClient, withNoXsrfProtection } from '@angular/common/http';

export const appConfig: ApplicationConfig = {
  providers: [
    provideHttpClient(withNoXsrfProtection()),
    provideRouter(
      [
        { path: '', loadChildren: () => import('./pages/pages.config').then(m => m.ROUTES) }
      ],
      withPreloading(PreloadAllModules)
    ),
    //importProvidersFrom(TranslateModule.forRoot())
  ]
};
