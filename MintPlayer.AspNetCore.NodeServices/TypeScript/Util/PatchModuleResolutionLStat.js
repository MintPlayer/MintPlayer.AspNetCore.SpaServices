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
const path = __importStar(require("path"));
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
