// Limit dependencies to core Node modules. This means the code in this file has to be very low-level and unattractive,
// but simplifies things for the consumer of this module.
import './Util/PatchModuleResolutionLStat';
import './Util/OverrideStdOutputs';
import * as http from 'http';
import * as path from 'path';
import * as url from 'url';
import { parseArgs } from './Util/ArgsUtil';
import { exitWhenParentExits } from './Util/ExitWhenParentExits';
import { AddressInfo } from 'net';

// Use dynamic import() which supports both CommonJS and ESM modules
// We use Function constructor to prevent webpack from transforming the import() call
// This is similar to how eval('require') was used before for CommonJS
const dynamicImport: (modulePath: string) => Promise<any> = new Function(
	'modulePath',
	'return import(modulePath)'
) as any;

async function importModule(modulePath: string): Promise<any> {
	// Convert to file:// URL for ESM compatibility on Windows
	const moduleUrl = url.pathToFileURL(modulePath).href;
	return dynamicImport(moduleUrl);
}

const server = http.createServer((req, res) => {
	readRequestBodyAsJson(req, async bodyJson => {
		let hasSentResult = false;
		const callback = (errorValue, successValue) => {
			if (!hasSentResult) {
				hasSentResult = true;
				if (errorValue) {
					respondWithError(res, errorValue);
				} else if (typeof successValue !== 'string') {
					// Arbitrary object/number/etc - JSON-serialize it
					let successValueJson: string;
					try {
						successValueJson = JSON.stringify(successValue);
					} catch (ex) {
						// JSON serialization error - pass it back to .NET
						respondWithError(res, ex);
						return;
					}
					res.setHeader('Content-Type', 'application/json');
					res.end(successValueJson);
				} else {
					// String - can bypass JSON-serialization altogether
					res.setHeader('Content-Type', 'text/plain');
					res.end(successValue);
				}
			}
		};

		// Support streamed responses
		Object.defineProperty(callback, 'stream', {
			enumerable: true,
			get: function () {
				if (!hasSentResult) {
					hasSentResult = true;
					res.setHeader('Content-Type', 'application/octet-stream');
				}

				return res;
			}
		});

		try {
			const resolvedPath = path.resolve(process.cwd(), bodyJson.moduleName);
			const invokedModule = await importModule(resolvedPath);

			// Handle both CommonJS and ESM exports
			// When loading CommonJS modules via import(), the module.exports becomes invokedModule.default
			// So we need to unwrap it first if it's a CommonJS module loaded via ESM import
			let moduleExports = invokedModule;
			if (invokedModule.default && typeof invokedModule.default === 'object' && invokedModule.default.__esModule) {
				// This is a CommonJS module with __esModule flag, loaded via import()
				// The actual exports are in invokedModule.default
				moduleExports = invokedModule.default;
			}

			let func;
			if (bodyJson.exportedFunctionName) {
				// Named export requested
				func = moduleExports[bodyJson.exportedFunctionName];
				// Also check the original module in case it's a true ESM named export
				if (func === undefined) {
					func = invokedModule[bodyJson.exportedFunctionName];
				}
			} else if (typeof moduleExports === 'function') {
				// The module itself is a function (module.exports = function)
				func = moduleExports;
			} else if (typeof moduleExports.default === 'function') {
				// TypeScript/ESM style default export
				func = moduleExports.default;
			} else if (typeof invokedModule.default === 'function') {
				// Direct ESM default export
				func = invokedModule.default;
			} else {
				func = moduleExports;
			}

			if (!func || typeof func !== 'function') {
				throw new Error('The module "' + resolvedPath + '" has no export named "' + (bodyJson.exportedFunctionName || 'default') + '"');
			}

			func.apply(null, [callback].concat(bodyJson.args));
		} catch (err) {
			callback(err, null);
		}
	});
});

const parsedArgs = parseArgs(process.argv);
const requestedPortOrZero = parsedArgs.port || 0; // 0 means 'let the OS decide'
server.listen(requestedPortOrZero, 'localhost', function () {
	const addressInfo = server.address() as AddressInfo;

	// Signal to HttpNodeHost which loopback IP address (IPv4 or IPv6) and port it should make its HTTP connections on
	console.log('[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {' + addressInfo.address + '} port ' + addressInfo.port + '\]');

	// Signal to the NodeServices base class that we're ready to accept invocations
	console.log('[MintPlayer.AspNetCore.NodeServices:Listening]');
});

exitWhenParentExits(parseInt(parsedArgs.parentPid), /* ignoreSigint */ true);

function readRequestBodyAsJson(request, callback) {
	let requestBodyAsString = '';
	request.on('data', chunk => { requestBodyAsString += chunk; });
	request.on('end', () => { callback(JSON.parse(requestBodyAsString)); });
}

function respondWithError(res: http.ServerResponse, errorValue: any) {
	res.statusCode = 500;
	res.end(JSON.stringify({
		errorMessage: errorValue.message || errorValue,
		errorDetails: errorValue.stack || null
	}));
}
