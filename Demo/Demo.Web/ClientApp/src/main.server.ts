/***************************************************************************************************
 * Initialize the server environment - for example, adding DOM built-in types to the global scope.
 *
 * NOTE:
 * This import must come before any imports (direct or transitive) that rely on DOM built-ins being
 * available, such as `@angular/elements`.
 */

import 'zone.js/dist/zone-node';
import '@angular/platform-server/init';

import { renderModule, renderModuleFactory } from '@angular/platform-server';
import { APP_BASE_HREF } from '@angular/common';
import { enableProdMode, StaticProvider, Inject } from '@angular/core';
import { createServerRenderer, BootFuncParams } from 'aspnet-prerendering';
import { environment } from './environments/environment';

if (environment.production) {
	enableProdMode();
}

const getBaseUrl = (params: BootFuncParams) => {
	return params.origin + params.baseUrl.slice(0, -1);
}

export default createServerRenderer(params => {
	const { AppServerModule, AppServerModuleNgFactory, LAZY_MODULE_MAP } = (module as any).exports;

	const providers: StaticProvider[] = [
		{ provide: APP_BASE_HREF, useValue: params.baseUrl },
		{ provide: 'BOOT_PARAMS', useValue: params },
		{ provide: 'BASE_URL', useFactory: getBaseUrl, deps: ['BOOT_PARAMS'] },
		{ provide: 'SERVERSIDE', useValue: true },
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
		extraProviders: providers
	};

	// Bypass ssr api call cert warnings in development
	process.env['NODE_TLS_REJECT_UNAUTHORIZED'] = "0";

	const renderPromise = AppServerModuleNgFactory
		? /* AoT */ renderModuleFactory(AppServerModuleNgFactory, options)
		: /* dev */ renderModule(AppServerModule, options);

	return renderPromise.then(html => ({ html }));
});

export { renderModule } from '@angular/platform-server';
export { AppServerModule } from './app/app.server.module';
