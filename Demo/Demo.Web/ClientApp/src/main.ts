import { enableProdMode } from '@angular/core';
import { platformBrowserDynamic } from '@angular/platform-browser-dynamic';

import { environment } from './environments/environment';
import { AppBrowserModule } from './app/app.browser.module';

export function getBaseUrl() {
  return document.getElementsByTagName('base')[0].href;
}

const providers = [
  { provide: 'BASE_URL', useFactory: getBaseUrl, deps: [] },
  { provide: 'MESSAGE', useValue: 'Message from the client' },
  { provide: 'PEOPLE', useValue: null },
  { provide: 'PERSON', useValue: null },
];

if (environment.production) {
  enableProdMode();
}

document.addEventListener('DOMContentLoaded', () => {
  platformBrowserDynamic(providers).bootstrapModule(AppBrowserModule)
  .catch(err => console.error(err));
});
