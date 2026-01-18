import { bootstrapApplication } from '@angular/platform-browser';
import { config } from './app/app.config.browser';
import { App } from './app/app';

bootstrapApplication(App, config)
  .catch((err) => console.error(err));
