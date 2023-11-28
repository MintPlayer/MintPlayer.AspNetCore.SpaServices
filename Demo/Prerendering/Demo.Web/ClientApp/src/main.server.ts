/***************************************************************************************************
 * Initialize the server environment - for example, adding DOM built-in types to the global scope.
 *
 * NOTE:
 * This import must come before any imports (direct or transitive) that rely on DOM built-ins being
 * available, such as `@angular/elements`.
 */

import 'zone.js/node';
import '@angular/platform-server/init';

import { renderApplication } from '@angular/platform-server';
import { APP_BASE_HREF } from '@angular/common';
import { enableProdMode, StaticProvider, Inject, Provider, ApplicationConfig, mergeApplicationConfig } from '@angular/core';
import { createServerRenderer, BootFuncParams } from 'aspnet-prerendering';
import { bootstrapApplication } from '@angular/platform-browser';
import { AppComponent } from './app/app.component';
import { config as serverConfig } from './app/app.config.server';

enableProdMode();

const getBaseUrl = (params: BootFuncParams) => {
	return params.origin + params.baseUrl.slice(0, -1);
}

export default createServerRenderer(params => {

  const providers: Provider[] = [
		{ provide: 'BOOT_PARAMS', useValue: params },
    { provide: APP_BASE_HREF, useFactory: getBaseUrl, deps: ['BOOT_PARAMS'] },
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
		//extraProviders: providers
  };

  const extraConfig: ApplicationConfig = { providers };
  const config = mergeApplicationConfig(serverConfig, extraConfig);

	// Bypass ssr api call cert warnings in development
	process.env['NODE_TLS_REJECT_UNAUTHORIZED'] = "0";

  const renderPromise = renderApplication(() => bootstrapApplication(AppComponent, config), options);

	return renderPromise.then(html => ({ html }));
});
