import { enableProdMode } from '@angular/core';
import { platformBrowserDynamic } from '@angular/platform-browser-dynamic';

import { environment } from './environments/environment';
import { AppBrowserModule } from './app/app.browser.module';

const getBaseUrl = () => {
	return document.getElementsByTagName('base')[0].href.slice(0, -1);
}

const providers = [
	{ provide: 'BASE_URL', useFactory: getBaseUrl, deps: [] },
	{ provide: 'SERVERSIDE', useValue: false },
	{ provide: 'MESSAGE', useValue: 'Message from the client' },
	{ provide: 'PEOPLE', useValue: null },
	{ provide: 'PERSON', useValue: null },
];

if (environment.production) {
	enableProdMode();
}

function bootstrap() {
	platformBrowserDynamic(providers).bootstrapModule(AppBrowserModule)
		.catch(err => console.error(err));
};


if (document.readyState === 'complete') {
	bootstrap();
} else {
	document.addEventListener('DOMContentLoaded', bootstrap);
}

