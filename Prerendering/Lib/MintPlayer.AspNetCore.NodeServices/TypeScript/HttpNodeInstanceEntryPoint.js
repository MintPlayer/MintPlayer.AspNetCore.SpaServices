"use strict";
exports.__esModule = true;
// Limit dependencies to core Node modules. This means the code in this file has to be very low-level and unattractive,
// but simplifies things for the consumer of this module.
require("./Util/PatchModuleResolutionLStat");
require("./Util/OverrideStdOutputs");
var http = require("http");
var path = require("path");
var ArgsUtil_1 = require("./Util/ArgsUtil");
var ExitWhenParentExits_1 = require("./Util/ExitWhenParentExits");
// Webpack doesn't support dynamic requires for files not present at compile time, so grab a direct
// reference to Node's runtime 'require' function.
var dynamicRequire = eval('require');
var server = http.createServer(function (req, res) {
    readRequestBodyAsJson(req, function (bodyJson) {
        var hasSentResult = false;
        var callback = function (errorValue, successValue) {
            if (!hasSentResult) {
                hasSentResult = true;
                if (errorValue) {
                    respondWithError(res, errorValue);
                }
                else if (typeof successValue !== 'string') {
                    // Arbitrary object/number/etc - JSON-serialize it
                    var successValueJson = void 0;
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
            var resolvedPath = path.resolve(process.cwd(), bodyJson.moduleName);
            var invokedModule = dynamicRequire(resolvedPath);
            var func = bodyJson.exportedFunctionName ? invokedModule[bodyJson.exportedFunctionName] : invokedModule;
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
var parsedArgs = ArgsUtil_1.parseArgs(process.argv);
var requestedPortOrZero = parsedArgs.port || 0; // 0 means 'let the OS decide'
server.listen(requestedPortOrZero, 'localhost', function () {
    var addressInfo = server.address();
    // Signal to HttpNodeHost which loopback IP address (IPv4 or IPv6) and port it should make its HTTP connections on
    console.log('[Microsoft.AspNetCore.NodeServices.HttpNodeHost:Listening on {' + addressInfo.address + '} port ' + addressInfo.port + '\]');
    // Signal to the NodeServices base class that we're ready to accept invocations
    console.log('[Microsoft.AspNetCore.NodeServices:Listening]');
});
ExitWhenParentExits_1.exitWhenParentExits(parseInt(parsedArgs.parentPid), /* ignoreSigint */ true);
function readRequestBodyAsJson(request, callback) {
    var requestBodyAsString = '';
    request.on('data', function (chunk) { requestBodyAsString += chunk; });
    request.on('end', function () { callback(JSON.parse(requestBodyAsString)); });
}
function respondWithError(res, errorValue) {
    res.statusCode = 500;
    res.end(JSON.stringify({
        errorMessage: errorValue.message || errorValue,
        errorDetails: errorValue.stack || null
    }));
}
