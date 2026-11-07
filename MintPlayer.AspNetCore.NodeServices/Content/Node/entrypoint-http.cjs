// Entry point of the Node process that MintPlayer.AspNetCore.NodeServices starts (HttpNodeInstance.cs).
// Hand-written CommonJS with no dependencies outside Node's core modules: it is embedded in the assembly as-is.
// There is no build step - edit this file directly.
//
// Protocol:
//   - Listens on localhost (the port from --port, or one the OS picks) and prints
//     "[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {address} port N]", then
//     "[MintPlayer.AspNetCore.NodeServices:Listening]".
//   - Each POST body is { moduleName, exportedFunctionName, args }. The module is loaded with import(), so it may be
//     CommonJS or an ECMAScript module, and the export is called as fn(callback, ...args).
//   - The callback's result goes back as text/plain (a string), application/json (anything else) or
//     application/octet-stream (callback.stream). Errors go back as HTTP 500 { errorMessage, errorDetails }.
//   - Exits when the parent .NET process (--parentPid) is gone.
'use strict';

const http = require('node:http');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

// ---------------------------------------------------------------------------------------------------------------
// stdout/stderr: .NET logs each line as one message, so internal line breaks are encoded and decoded on the .NET
// side (OutOfProcessNodeInstance.cs). Done at the stream level, after console formatting has taken place.
const findInternalNewlinesRegex = /\n(?!$)/g;
const encodedNewline = '__ns_newline__';

function encodeNewlinesWrittenToStream(outputStream) {
	const origWrite = outputStream.write;
	outputStream.write = function (value, ...rest) {
		if (typeof value === 'string') {
			value = value.replace(findInternalNewlinesRegex, encodedNewline);
		}
		return origWrite.call(this, value, ...rest);
	};
}

encodeNewlinesWrittenToStream(process.stdout);
encodeNewlinesWrittenToStream(process.stderr);

// ---------------------------------------------------------------------------------------------------------------
// Arguments: --key value pairs.
function parseArgs(args) {
	const result = {};
	let currentKey = null;
	for (const arg of args) {
		if (arg.startsWith('--')) {
			currentKey = arg.substring(2);
			result[currentKey] = undefined;
		} else if (currentKey) {
			result[currentKey] = arg;
			currentKey = null;
		}
	}
	return result;
}

// ---------------------------------------------------------------------------------------------------------------
// Module loading.
function importModule(modulePath) {
	// A file URL, not a path: import() rejects Windows drive-letter paths.
	return import(pathToFileURL(modulePath).href);
}

function findExport(invokedModule, exportedFunctionName) {
	// CommonJS loaded through import() exposes module.exports as `default`; transpiled modules mark it __esModule.
	const def = invokedModule.default;
	const moduleExports = def && typeof def === 'object' && def.__esModule ? def : invokedModule;

	if (exportedFunctionName) {
		for (const source of [moduleExports, invokedModule, def]) {
			if (source && typeof source[exportedFunctionName] === 'function') {
				return source[exportedFunctionName];
			}
		}
		return undefined;
	}

	if (typeof moduleExports === 'function') return moduleExports;
	if (typeof moduleExports.default === 'function') return moduleExports.default;
	return undefined;
}

// ---------------------------------------------------------------------------------------------------------------
// HTTP server.
function respondWithError(res, errorValue) {
	res.statusCode = 500;
	res.setHeader('Content-Type', 'application/json');
	res.end(JSON.stringify({
		errorMessage: (errorValue && errorValue.message) || String(errorValue),
		errorDetails: (errorValue && errorValue.stack) || null,
	}));
}

function readRequestBodyAsJson(request) {
	return new Promise((resolve, reject) => {
		let body = '';
		request.setEncoding('utf8');
		request.on('data', chunk => { body += chunk; });
		request.on('end', () => {
			try {
				resolve(JSON.parse(body));
			} catch (ex) {
				reject(ex);
			}
		});
		request.on('error', reject);
	});
}

async function handleRequest(req, res) {
	let hasSentResult = false;
	const callback = (errorValue, successValue) => {
		if (hasSentResult) {
			return;
		}
		hasSentResult = true;

		if (errorValue) {
			respondWithError(res, errorValue);
		} else if (typeof successValue === 'string') {
			// Strings bypass JSON serialization altogether.
			res.setHeader('Content-Type', 'text/plain');
			res.end(successValue);
		} else {
			let json;
			try {
				json = JSON.stringify(successValue);
			} catch (ex) {
				respondWithError(res, ex);
				return;
			}
			res.setHeader('Content-Type', 'application/json');
			res.end(json);
		}
	};

	// Streamed responses.
	Object.defineProperty(callback, 'stream', {
		enumerable: true,
		get() {
			if (!hasSentResult) {
				hasSentResult = true;
				res.setHeader('Content-Type', 'application/octet-stream');
			}
			return res;
		},
	});

	try {
		const bodyJson = await readRequestBodyAsJson(req);
		const resolvedPath = path.resolve(process.cwd(), bodyJson.moduleName);
		const func = findExport(await importModule(resolvedPath), bodyJson.exportedFunctionName);
		if (typeof func !== 'function') {
			throw new Error('The module "' + resolvedPath + '" has no export named "' + (bodyJson.exportedFunctionName || 'default') + '"');
		}

		await func.apply(null, [callback].concat(bodyJson.args || []));
	} catch (err) {
		callback(err, null);
	}
}

const parsedArgs = parseArgs(process.argv);
const server = http.createServer((req, res) => { handleRequest(req, res); });
server.listen(parsedArgs.port || 0, 'localhost', () => {
	const addressInfo = server.address();

	// Tells HttpNodeInstance which loopback address (IPv4 or IPv6) and port to connect to.
	console.log('[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {' + addressInfo.address + '} port ' + addressInfo.port + ']');

	// Tells OutOfProcessNodeInstance that invocations can start.
	console.log('[MintPlayer.AspNetCore.NodeServices:Listening]');
});

// ---------------------------------------------------------------------------------------------------------------
// Exit when the parent exits. Polling the parent PID is the only cross-platform approach that is documented to
// work, and a forcefully killed .NET process gets no chance to stop its children itself.
const parentPid = parseInt(parsedArgs.parentPid, 10);

function processExists(pid) {
	try {
		// Signal 0 tests for existence without killing.
		process.kill(pid, 0);
		return true;
	} catch (ex) {
		if (ex.code === 'EPERM') {
			throw new Error(`Attempted to check whether process ${pid} was running, but got a permissions error.`);
		}
		return false;
	}
}

if (parentPid > 0) {
	setInterval(() => {
		if (!processExists(parentPid)) {
			// Nothing can be logged here: stdout was connected to the parent, which is gone.
			process.exit();
		}
	}, 1000);
}

// Ctrl+C sends SIGINT to the whole foreground process tree. ASP.NET Core shuts down gracefully and needs Node
// until then, so ignore it: the parent-PID poll above ends this process once .NET has exited.
process.on('SIGINT', () => {
	console.log('Received SIGINT. Waiting for .NET process to exit...');
});
