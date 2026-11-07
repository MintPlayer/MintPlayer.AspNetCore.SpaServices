import { BootstrapContext, bootstrapApplication } from '@angular/platform-browser';
import { APP_BASE_HREF } from '@angular/common';
import { StaticProvider } from '@angular/core';
import { App } from './app/app';
import { config } from './app/app.config.server';
import { DATA_FROM_SERVER } from './app/tokens';

// The stock Angular server entry: a bootstrap function, not a renderer.
const bootstrap = (context: BootstrapContext) => bootstrapApplication(App, config, context);

export default bootstrap;

// Lets MintPlayer.AspNetCore.SpaServices.Prerendering render this bundle itself, with the index.html that
// ASP.NET Core captured and the request URL.
export { renderApplication } from '@angular/platform-server';

// Optional. Hands the data from ISpaPrerenderingService.OnSupplyData to Angular DI as one object, so nothing
// has to be fetched over HTTP while the page is rendered on the server. Each page takes what it needs from it.
export const prerenderProviders = (data: any, params: { baseUrl: string }): StaticProvider[] => [
  { provide: DATA_FROM_SERVER, useValue: data },
  // Relative: an absolute base href (origin + path) makes Angular reject the URL as changing origin (NG05703).
  { provide: APP_BASE_HREF, useValue: params.baseUrl },
];
