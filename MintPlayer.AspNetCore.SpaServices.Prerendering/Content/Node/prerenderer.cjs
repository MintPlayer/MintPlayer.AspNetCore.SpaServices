// Prerendering glue between MintPlayer.AspNetCore.SpaServices.Prerendering and the application's boot module.
// Hand-written CommonJS with no dependencies: it is embedded in the assembly as-is and loaded by the NodeServices
// entry point. There is no build step - edit this file directly.
//
// The boot module may be CommonJS or an ECMAScript module (.mjs, or a bundle split into chunk files). It is
// loaded with dynamic import(), which handles both. Supported shapes, in order of precedence:
//
//   1. A function flagged `isServerRenderer` - built by `createServerRenderer` from the aspnet-prerendering
//      npm package. It receives the raw invocation arguments, exactly as before.
//   2. An Angular application-builder server bundle: `export default` is the bootstrap function and the module
//      also re-exports `renderApplication` from '@angular/platform-server'. Optional: a `prerenderProviders`
//      export, `(data, params) => Provider[]`, to hand the .NET-supplied data to Angular DI.
//   3. A plain renderer function, `(params) => Promise<{ html, statusCode?, redirectUrl?, globals? }>`.
'use strict';

const path = require('node:path');
const { pathToFileURL } = require('node:url');

const defaultTimeoutMilliseconds = 30 * 1000;
const renderApplicationHint =
	"export { renderApplication } from '@angular/platform-server';";

function importModule(modulePath) {
	// A file URL, not a path: import() rejects Windows drive-letter paths.
	return import(pathToFileURL(modulePath).href);
}

// CommonJS loaded through import() exposes module.exports as `default`. Unwrap it so that transpiled
// `exports.default = ...` and `module.exports = fn` look the same as their ESM counterparts.
function getExports(loadedModule) {
	const def = loadedModule.default;
	if (def && typeof def === 'object' && def.__esModule) {
		return def;
	}
	return loadedModule;
}

function describe(value) {
	if (value === undefined) return 'nothing';
	if (value === null) return 'null';
	return typeof value;
}

/**
 * Picks the render strategy for a loaded boot module. Exported for the tests.
 * @returns {{ kind: 'serverRenderer' | 'angular' | 'renderer', fn: Function, exports: any }}
 */
function resolveRenderer(loadedModule, moduleName, exportName) {
	const moduleExports = getExports(loadedModule);
	const named = exportName
		? (moduleExports[exportName] !== undefined ? moduleExports[exportName] : loadedModule[exportName])
		: undefined;

	if (exportName && typeof named !== 'function') {
		throw new Error(`The boot module '${moduleName}' has no function export named '${exportName}' (found ${describe(named)}).`);
	}

	let candidate = named;
	if (!candidate) {
		if (typeof moduleExports === 'function') {
			candidate = moduleExports;
		} else if (typeof moduleExports.default === 'function') {
			candidate = moduleExports.default;
		}
	}

	if (typeof candidate !== 'function') {
		throw new Error(
			`The boot module '${moduleName}' does not export a default function, and no export name was configured. ` +
			`Export one of: a createServerRenderer(...) result, an Angular bootstrap function together with ` +
			`${renderApplicationHint}, or a function (params) => Promise<{ html }>.`);
	}

	if (candidate.isServerRenderer) {
		return { kind: 'serverRenderer', fn: candidate, exports: moduleExports };
	}

	if (typeof moduleExports.renderApplication === 'function') {
		return { kind: 'angular', fn: candidate, exports: moduleExports };
	}

	if (looksLikeAngularServerBundle(moduleExports)) {
		throw new Error(
			`The boot module '${moduleName}' is an Angular server bundle, but it does not export renderApplication, ` +
			`so it cannot be rendered with the HTML template captured by ASP.NET Core. Add this line to main.server.ts:\n` +
			`  ${renderApplicationHint}`);
	}

	return { kind: 'renderer', fn: candidate, exports: moduleExports };
}

// The Angular application builder injects these into every server bundle (angular-cli
// application-code-bundle.ts, createServerMainCodeBundleOptions).
function looksLikeAngularServerBundle(moduleExports) {
	return typeof moduleExports.ɵgetOrCreateAngularServerApp === 'function'
		|| typeof moduleExports.ɵresetCompiledComponents === 'function';
}

/**
 * The parameter object aspnet-prerendering's createServerRenderer passed to boot functions, minus
 * domainTasks (Node domains are deprecated; Angular waits for stability itself).
 */
function createParams(absoluteRequestUrl, requestPathAndQuery, customData, requestPathBase) {
	const absolute = new URL(absoluteRequestUrl);
	const relative = new URL(requestPathAndQuery, absolute);
	const query = {};
	for (const [key, value] of relative.searchParams) {
		query[key] = key in query ? [].concat(query[key], value) : value;
	}

	return {
		location: { pathname: relative.pathname, search: relative.search, query, path: requestPathAndQuery, href: requestPathAndQuery },
		origin: absolute.origin,
		url: requestPathAndQuery,
		baseUrl: (requestPathBase || '') + '/',
		absoluteUrl: absoluteRequestUrl,
		data: customData,
	};
}

async function renderAngular(resolved, params) {
	const data = params.data || {};
	const document = data.originalHtml;
	if (typeof document !== 'string' || document.length === 0) {
		throw new Error('Cannot render the Angular application: no HTML template (originalHtml) was supplied.');
	}

	const providerFactory = resolved.exports.prerenderProviders;
	let platformProviders = [];
	if (typeof providerFactory === 'function') {
		platformProviders = (await providerFactory(data, params)) || [];
	} else if (providerFactory !== undefined) {
		throw new Error(`The boot module exports 'prerenderProviders', but it is ${describe(providerFactory)} rather than a function (data, params) => Provider[].`);
	}

	const html = await resolved.exports.renderApplication(resolved.fn, { document, url: params.url, platformProviders });
	return { html };
}

function withTimeout(promise, timeoutMilliseconds, moduleName) {
	if (!(timeoutMilliseconds > 0)) {
		return promise;
	}

	let timer;
	const timeout = new Promise((_, reject) => {
		timer = setTimeout(() => reject(new Error(
			`Prerendering timed out after ${timeoutMilliseconds}ms because the boot module '${moduleName}' returned ` +
			'a promise that did not resolve or reject.')), timeoutMilliseconds);
	});
	return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

// Invoked by .NET (Prerenderer.cs) through the NodeServices entry point.
async function renderToString(callback, applicationBasePath, bootModule, absoluteRequestUrl, requestPathAndQuery, customData, overrideTimeoutMilliseconds, requestPathBase) {
	let resolved;
	try {
		const fullPath = path.resolve(applicationBasePath, bootModule.moduleName);
		resolved = resolveRenderer(await importModule(fullPath), bootModule.moduleName, bootModule.exportName);
	} catch (ex) {
		callback(formatError(ex), null);
		return;
	}

	if (resolved.kind === 'serverRenderer') {
		// The renderer owns the callback, the timeout and the params, exactly as before.
		resolved.fn.apply(null, arguments);
		return;
	}

	try {
		const params = createParams(absoluteRequestUrl, requestPathAndQuery, customData, requestPathBase);
		const rendering = resolved.kind === 'angular'
			? renderAngular(resolved, params)
			: Promise.resolve(resolved.fn(params)).then(result => {
				if (result === null || typeof result !== 'object') {
					throw new Error(`The boot module '${bootModule.moduleName}' must resolve to an object { html }, but resolved to ${describe(result)}.`);
				}
				return result;
			});
		const result = await withTimeout(rendering, overrideTimeoutMilliseconds || defaultTimeoutMilliseconds, bootModule.moduleName);
		callback(null, result);
	} catch (ex) {
		callback(formatError(ex), null);
	}
}

function formatError(ex) {
	// A string, not an Error: the entry point reports it verbatim as the exception message in .NET.
	const detail = ex && ex.stack ? ex.stack : String(ex);
	return 'Prerendering failed because of error: ' + detail + '\nCurrent directory is: ' + process.cwd();
}

module.exports = { renderToString, resolveRenderer, createParams };
