import { APP_BASE_HREF } from '@angular/common';
import { bootstrapApplication } from '@angular/platform-browser';
import { config as browserConfig } from './app/app.config.browser';
import { AppComponent } from './app/app.component';
import { ApplicationConfig, mergeApplicationConfig } from '@angular/core';

const getBaseUrl = () => {
  return document.getElementsByTagName('base')[0].href.slice(0, -1);
}

const config: ApplicationConfig = {
  providers: [
    { provide: APP_BASE_HREF, useFactory: getBaseUrl },
    { provide: 'MESSAGE', useValue: 'Message from the client' },
  ]
};

const mergedConfig = mergeApplicationConfig(browserConfig, config);

bootstrapApplication(AppComponent, mergedConfig)
  .catch((err) => console.error(err));
