/******/ (() => { // webpackBootstrap
/******/ 	var __webpack_modules__ = ({

/***/ 307:
/***/ ((__unused_webpack_module, exports) => {

"use strict";

Object.defineProperty(exports, "__esModule", ({ value: true }));
exports.parseArgs = void 0;
function parseArgs(args) {
    // Very simplistic parsing which is sufficient for the cases needed. We don't want to bring in any external
    // dependencies (such as an args-parsing library) to this file.
    const result = {};
    let currentKey = null;
    args.forEach(arg => {
        if (arg.indexOf('--') === 0) {
            const argName = arg.substring(2);
            result[argName] = undefined;
            currentKey = argName;
        }
        else if (currentKey) {
            result[currentKey] = arg;
            currentKey = null;
        }
    });
    return result;
}
exports.parseArgs = parseArgs;


/***/ }),

/***/ 855:
/***/ ((__unused_webpack_module, exports) => {

"use strict";

/*
In general, we want the Node child processes to be terminated as soon as the parent .NET processes exit,
because we have no further use for them. If the .NET process shuts down gracefully, it will run its
finalizers, one of which (in OutOfProcessNodeInstance.cs) will kill its associated Node process immediately.

But if the .NET process is terminated forcefully (e.g., on Linux/OSX with 'kill -9'), then it won't have
any opportunity to shut down its child processes, and by default they will keep running. In this case, it's
up to the child process to detect this has happened and terminate itself.

There are many possible approaches to detecting when a parent process has exited, most of which behave
differently between Windows and Linux/OS X:

 - On Windows, the parent process can mark its child as being a 'job' that should auto-terminate when
   the parent does (http://stackoverflow.com/a/4657392). Not cross-platform.
 - The child Node process can get a callback when the parent disconnects (process.on('disconnect', ...)).
   But despite http://stackoverflow.com/a/16487966, no callback fires in any case I've tested (Windows / OS X).
 - The child Node process can get a callback when its stdin/stdout are disconnected, as described at
   http://stackoverflow.com/a/15693934. This works well on OS X, but calling stdout.resume() on Windows
   causes the process to terminate prematurely.
 - I don't know why, but on Windows, it's enough to invoke process.stdin.resume(). For some reason this causes
   the child Node process to exit as soon as the parent one does, but I don't see this documented anywhere.
 - You can poll to see if the parent process, or your stdin/stdout connection to it, is gone
   - You can directly pass a parent process PID to the child, and then have the child poll to see if it's
     still running (e.g., using process.kill(pid, 0), which doesn't kill it but just tests whether it exists,
     as per https://nodejs.org/api/process.html#process_process_kill_pid_signal)
   - Or, on each poll, you can try writing to process.stdout. If the parent has died, then this will throw.
     However I don't see this documented anywhere. It would be nice if you could just poll for whether or not
     process.stdout is still connected (without actually writing to it) but I haven't found any property whose
     value changes until you actually try to write to it.

Of these, the only cross-platform approach that is actually documented as a valid strategy is simply polling
to check whether the parent PID is still running. So that's what we do here.
*/
Object.defineProperty(exports, "__esModule", ({ value: true }));
exports.exitWhenParentExits = void 0;
const pollIntervalMs = 1000;
function exitWhenParentExits(parentPid, ignoreSigint) {
    setInterval(() => {
        if (!processExists(parentPid)) {
            // Can't log anything at this point, because out stdout was connected to the parent,
            // but the parent is gone.
            process.exit();
        }
    }, pollIntervalMs);
    if (ignoreSigint) {
        // Pressing ctrl+c in the terminal sends a SIGINT to all processes in the foreground process tree.
        // By default, the Node process would then exit before the .NET process, because ASP.NET implements
        // a delayed shutdown to allow ongoing requests to complete.
        //
        // This is problematic, because if Node exits first, the CopyToAsync code in ConditionalProxyMiddleware
        // will experience a read fault, and logs a huge load of errors. Fortunately, since the Node process is
        // already set up to shut itself down if it detects the .NET process is terminated, all we have to do is
        // ignore the SIGINT. The Node process will then terminate automatically after the .NET process does.
        //
        // A better solution would be to have WebpackDevMiddleware listen for SIGINT and gracefully close any
        // ongoing EventSource connections before letting the Node process exit, independently of the .NET
        // process exiting. However, doing this well in general is very nontrivial (see all the discussion at
        // https://github.com/nodejs/node/issues/2642).
        process.on('SIGINT', () => {
            console.log('Received SIGINT. Waiting for .NET process to exit...');
        });
    }
}
exports.exitWhenParentExits = exitWhenParentExits;
function processExists(pid) {
    try {
        // Sending signal 0 - on all platforms - tests whether the process exists. As long as it doesn't
        // throw, that means it does exist.
        process.kill(pid, 0);
        return true;
    }
    catch (ex) {
        // If the reason for the error is that we don't have permission to ask about this process,
        // report that as a separate problem.
        if (ex.code === 'EPERM') {
            throw new Error(`Attempted to check whether process ${pid} was running, but got a permissions error.`);
        }
        return false;
    }
}


/***/ }),

/***/ 952:
/***/ (() => {

// When Node writes to stdout/strerr, we capture that and convert the lines into calls on the
// active .NET ILogger. But by default, stdout/stderr don't have any way of distinguishing
// linebreaks inside log messages from the linebreaks that delimit separate log messages,
// so multiline strings will end up being written to the ILogger as multiple independent
// log messages. This makes them very hard to make sense of, especially when they represent
// something like stack traces.
//
// To fix this, we intercept stdout/stderr writes, and replace internal linebreaks with a
// marker token. When .NET receives the lines, it converts the marker tokens back to regular
// linebreaks within the logged messages.
//
// Note that it's better to do the interception at the stdout/stderr level, rather than at
// the console.log/console.error (etc.) level, because this takes place after any native
// message formatting has taken place (e.g., inserting values for % placeholders).
const findInternalNewlinesRegex = /\n(?!$)/g;
const encodedNewline = '__ns_newline__';
encodeNewlinesWrittenToStream(process.stdout);
encodeNewlinesWrittenToStream(process.stderr);
function encodeNewlinesWrittenToStream(outputStream) {
    const origWriteFunction = outputStream.write;
    outputStream.write = function (value) {
        // Only interfere with the write if it's definitely a string
        if (typeof value === 'string') {
            const argsClone = Array.prototype.slice.call(arguments, 0);
            argsClone[0] = encodeNewlinesInString(value);
            origWriteFunction.apply(this, argsClone);
        }
        else {
            origWriteFunction.apply(this, arguments);
        }
    };
}
function encodeNewlinesInString(str) {
    return str.replace(findInternalNewlinesRegex, encodedNewline);
}


/***/ }),

/***/ 370:
/***/ ((__unused_webpack_module, exports, __webpack_require__) => {

"use strict";

Object.defineProperty(exports, "__esModule", ({ value: true }));
const path = __webpack_require__(17);
const startsWith = (str, prefix) => str.substring(0, prefix.length) === prefix;
const appRootDir = process.cwd();
function patchedLStat(pathToStatLong, fsReqWrap) {
    try {
        // If the lstat completes without errors, we don't modify its behavior at all
        return origLStat.apply(this, arguments);
    }
    catch (ex) {
        const shouldOverrideError = startsWith(ex.message, 'EPERM') // It's a permissions error
            && typeof appRootDirLong === 'string'
            && startsWith(appRootDirLong, pathToStatLong) // ... for an ancestor directory
            && ex.stack.indexOf('Object.realpathSync ') >= 0; // ... during symlink resolution
        if (shouldOverrideError) {
            // Fake the result to give the same result as an 'lstat' on the app root dir.
            // This stops Node failing to load modules just because it doesn't know whether
            // ancestor directories are symlinks or not. If there's a genuine file
            // permissions issue, it will still surface later when Node actually
            // tries to read the file.
            return origLStat.call(this, appRootDir, fsReqWrap);
        }
        else {
            // In any other case, preserve the original error
            throw ex;
        }
    }
}
;
// It's only necessary to apply this workaround on Windows
let appRootDirLong = null;
let origLStat = null;
if (/^win/.test(process.platform)) {
    try {
        // Get the app's root dir in Node's internal "long" format (e.g., \\?\C:\dir\subdir)
        appRootDirLong = path._makeLong(appRootDir);
        // Actually apply the patch, being as defensive as possible
        const bindingFs = process.binding('fs');
        origLStat = bindingFs.lstat;
        if (typeof origLStat === 'function') {
            bindingFs.lstat = patchedLStat;
        }
    }
    catch (ex) {
        // If some future version of Node throws (e.g., to prevent use of process.binding()),
        // don't apply the patch, but still let the application run.
    }
}


/***/ }),

/***/ 685:
/***/ ((module) => {

"use strict";
module.exports = require("http");

/***/ }),

/***/ 17:
/***/ ((module) => {

"use strict";
module.exports = require("path");

/***/ }),

/***/ 310:
/***/ ((module) => {

"use strict";
module.exports = require("url");

/***/ })

/******/ 	});
/************************************************************************/
/******/ 	// The module cache
/******/ 	var __webpack_module_cache__ = {};
/******/ 	
/******/ 	// The require function
/******/ 	function __webpack_require__(moduleId) {
/******/ 		// Check if module is in cache
/******/ 		var cachedModule = __webpack_module_cache__[moduleId];
/******/ 		if (cachedModule !== undefined) {
/******/ 			return cachedModule.exports;
/******/ 		}
/******/ 		// Create a new module (and put it into the cache)
/******/ 		var module = __webpack_module_cache__[moduleId] = {
/******/ 			// no module.id needed
/******/ 			// no module.loaded needed
/******/ 			exports: {}
/******/ 		};
/******/ 	
/******/ 		// Execute the module function
/******/ 		__webpack_modules__[moduleId](module, module.exports, __webpack_require__);
/******/ 	
/******/ 		// Return the exports of the module
/******/ 		return module.exports;
/******/ 	}
/******/ 	
/************************************************************************/
var __webpack_exports__ = {};
// This entry need to be wrapped in an IIFE because it need to be in strict mode.
(() => {
"use strict";
var exports = __webpack_exports__;

Object.defineProperty(exports, "__esModule", ({ value: true }));
// Limit dependencies to core Node modules. This means the code in this file has to be very low-level and unattractive,
// but simplifies things for the consumer of this module.
__webpack_require__(370);
__webpack_require__(952);
const http = __webpack_require__(685);
const path = __webpack_require__(17);
const url = __webpack_require__(310);
const ArgsUtil_1 = __webpack_require__(307);
const ExitWhenParentExits_1 = __webpack_require__(855);
// Use dynamic import() which supports both CommonJS and ESM modules
// We use Function constructor to prevent webpack from transforming the import() call
// This is similar to how eval('require') was used before for CommonJS
const dynamicImport = new Function('modulePath', 'return import(modulePath)');
async function importModule(modulePath) {
    // Convert to file:// URL for ESM compatibility on Windows
    const moduleUrl = url.pathToFileURL(modulePath).href;
    return dynamicImport(moduleUrl);
}
const server = http.createServer((req, res) => {
    readRequestBodyAsJson(req, async (bodyJson) => {
        let hasSentResult = false;
        const callback = (errorValue, successValue) => {
            if (!hasSentResult) {
                hasSentResult = true;
                if (errorValue) {
                    respondWithError(res, errorValue);
                }
                else if (typeof successValue !== 'string') {
                    // Arbitrary object/number/etc - JSON-serialize it
                    let successValueJson;
                    try {
                        successValueJson = JSON.stringify(successValue);
                    }
                    catch (ex) {
                        // JSON serialization error - pass it back to .NET
                        respondWithError(res, ex);
                        return;
                    }
                    res.setHeader('Content-Type', 'application/json');
                    res.end(successValueJson);
                }
                else {
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
            }
            else if (typeof moduleExports === 'function') {
                // The module itself is a function (module.exports = function)
                func = moduleExports;
            }
            else if (typeof moduleExports.default === 'function') {
                // TypeScript/ESM style default export
                func = moduleExports.default;
            }
            else if (typeof invokedModule.default === 'function') {
                // Direct ESM default export
                func = invokedModule.default;
            }
            else {
                func = moduleExports;
            }
            if (!func || typeof func !== 'function') {
                throw new Error('The module "' + resolvedPath + '" has no export named "' + (bodyJson.exportedFunctionName || 'default') + '"');
            }
            func.apply(null, [callback].concat(bodyJson.args));
        }
        catch (err) {
            callback(err, null);
        }
    });
});
const parsedArgs = ArgsUtil_1.parseArgs(process.argv);
const requestedPortOrZero = parsedArgs.port || 0; // 0 means 'let the OS decide'
server.listen(requestedPortOrZero, 'localhost', function () {
    const addressInfo = server.address();
    // Signal to HttpNodeHost which loopback IP address (IPv4 or IPv6) and port it should make its HTTP connections on
    console.log('[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {' + addressInfo.address + '} port ' + addressInfo.port + '\]');
    // Signal to the NodeServices base class that we're ready to accept invocations
    console.log('[MintPlayer.AspNetCore.NodeServices:Listening]');
});
ExitWhenParentExits_1.exitWhenParentExits(parseInt(parsedArgs.parentPid), /* ignoreSigint */ true);
function readRequestBodyAsJson(request, callback) {
    let requestBodyAsString = '';
    request.on('data', chunk => { requestBodyAsString += chunk; });
    request.on('end', () => { callback(JSON.parse(requestBodyAsString)); });
}
function respondWithError(res, errorValue) {
    res.statusCode = 500;
    res.end(JSON.stringify({
        errorMessage: errorValue.message || errorValue,
        errorDetails: errorValue.stack || null
    }));
}

})();

var __webpack_export_target__ = exports;
for(var i in __webpack_exports__) __webpack_export_target__[i] = __webpack_exports__[i];
if(__webpack_exports__.__esModule) Object.defineProperty(__webpack_export_target__, "__esModule", { value: true });
/******/ })()
;