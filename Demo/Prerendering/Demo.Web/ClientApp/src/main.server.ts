import 'reflect-metadata';
import { renderApplication } from '@angular/platform-server';
import { enableProdMode, StaticProvider } from '@angular/core';
import { createServerRenderer } from 'aspnet-prerendering';
import { APP_BASE_HREF } from '@angular/common';
import { App } from './app/app';
import { bootstrapApplication } from '@angular/platform-browser';
import { config as serverConfig } from './app/app.config.server';

enableProdMode();

export default createServerRenderer(params => {
  const providers: StaticProvider[] = [
    { provide: APP_BASE_HREF, useValue: params.origin + params.baseUrl.slice(0, -1) },
    { provide: 'MESSAGE', useValue: params.data.message },
  ];

  if ('people' in params.data) {
    providers.push({ provide: 'PEOPLE', useValue: params.data.people });
  } else {
    providers.push({ provide: 'PEOPLE', useValue: null });
  }
  if ('person' in params.data) {
    providers.push({ provide: 'PERSON', useValue: params.data.person });
  } else {
    providers.push({ provide: 'PERSON', useValue: null });
  }

  const options = {
    document: params.data.originalHtml,
    url: params.url,
    platformProviders: providers
  };

  // Bypass ssr api call cert warnings in development
  process.env['NODE_TLS_REJECT_UNAUTHORIZED'] = "0";

  const renderPromise = renderApplication((context) => bootstrapApplication(App, serverConfig, context), options);

  return renderPromise.then(html => ({ html }));
});
