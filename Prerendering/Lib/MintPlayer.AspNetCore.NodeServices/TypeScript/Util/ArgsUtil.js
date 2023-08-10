"use strict";
exports.__esModule = true;
exports.parseArgs = void 0;
function parseArgs(args) {
    // Very simplistic parsing which is sufficient for the cases needed. We don't want to bring in any external
    // dependencies (such as an args-parsing library) to this file.
    var result = {};
    var currentKey = null;
    args.forEach(function (arg) {
        if (arg.indexOf('--') === 0) {
            var argName = arg.substring(2);
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
