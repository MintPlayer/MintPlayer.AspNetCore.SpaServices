"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
// Limit dependencies to core Node modules. This means the code in this file has to be very low-level and unattractive,
// but simplifies things for the consumer of this module.
require("./Util/PatchModuleResolutionLStat");
require("./Util/OverrideStdOutputs");
const http = __importStar(require("http"));
const path = __importStar(require("path"));
const ArgsUtil_1 = require("./Util/ArgsUtil");
const ExitWhenParentExits_1 = require("./Util/ExitWhenParentExits");
// Webpack doesn't support dynamic requires for files not present at compile time, so grab a direct
// reference to Node's runtime 'require' function.
const dynamicRequire = eval('require');
const server = http.createServer((req, res) => {
    readRequestBodyAsJson(req, bodyJson => {
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
            const x = import(resolvedPath);
            const invokedModule = import(resolvedPath);
            const func = bodyJson.exportedFunctionName ? invokedModule[bodyJson.exportedFunctionName] : invokedModule;
            if (!func) {
                throw new Error('The module "' + resolvedPath + '" has no export named "' + bodyJson.exportedFunctionName + '"');
            }
            func.apply(null, [callback].concat(bodyJson.args));
        }
        catch (synchronousException) {
            callback(synchronousException, null);
        }
    });
});
const parsedArgs = (0, ArgsUtil_1.parseArgs)(process.argv);
const requestedPortOrZero = parsedArgs.port || 0; // 0 means 'let the OS decide'
server.listen(requestedPortOrZero, 'localhost', function () {
    const addressInfo = server.address();
    // Signal to HttpNodeHost which loopback IP address (IPv4 or IPv6) and port it should make its HTTP connections on
    console.log('[MintPlayer.AspNetCore.NodeServices.HttpNodeHost:Listening on {' + addressInfo.address + '} port ' + addressInfo.port + '\]');
    // Signal to the NodeServices base class that we're ready to accept invocations
    console.log('[MintPlayer.AspNetCore.NodeServices:Listening]');
});
(0, ExitWhenParentExits_1.exitWhenParentExits)(parseInt(parsedArgs.parentPid), /* ignoreSigint */ true);
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
