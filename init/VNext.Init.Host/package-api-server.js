#!/usr/bin/env node

/**
 * VNext Package API Server
 * 
 * This server provides an API endpoint to download npm packages and publish them to vnext API.
 * It replicates the functionality of init-vnext-core.sh but exposes it as a REST API.
 */

const http = require('http');
const https = require('https');
const fs = require('fs').promises;
const path = require('path');
const { execSync, spawn } = require('child_process');
const { promisify } = require('util');
const exec = promisify(require('child_process').exec);

const PORT = process.env.PACKAGE_API_PORT || 3000;
const VNEXT_APP_URL = process.env.VNEXT_APP_URL || 'http://host.docker.internal:4201';
const API_ENDPOINT = `${VNEXT_APP_URL}/api/v1/admin/publish`;
const DEFAULT_REGISTRY = process.env.NPM_REGISTRY || 'https://registry.npmjs.org/';
const CUSTOM_PATH = process.env.CUSTOM_COMPONENTS_PATH || '/app/custom-components';

/**
 * Get app domain from environment variable or default to 'core'
 */
function getDefaultAppDomain() {
    return process.env.APP_DOMAIN || 'core';
}

// SYS file mappings
const SYS_FILE_MAPPINGS = {
    'sys-flows.json': 'Workflows',
    'sys-tasks.json': 'Tasks',
    'sys-extensions.json': 'Extensions',
    'sys-functions.json': 'Functions',
    'sys-views.json': 'Views',
    'sys-schemas.json': 'Schemas'
};

const SYS_FILES_ORDER = [
    'sys-tasks.json',
    'sys-extensions.json',
    'sys-functions.json',
    'sys-views.json',
    'sys-schemas.json'
];

/**
 * Parse JSON request body
 */
function parseJSON(req) {
    return new Promise((resolve, reject) => {
        let body = '';
        req.on('data', chunk => {
            body += chunk.toString();
        });
        req.on('end', () => {
            try {
                resolve(JSON.parse(body));
            } catch (e) {
                reject(new Error('Invalid JSON'));
            }
        });
        req.on('error', reject);
    });
}

/**
 * Make HTTP request
 */
function httpRequest(url, options = {}) {
    return new Promise((resolve, reject) => {
        const lib = url.startsWith('https') ? https : http;
        const req = lib.request(url, options, (res) => {
            let data = '';
            res.on('data', chunk => { data += chunk; });
            res.on('end', () => {
                resolve({ statusCode: res.statusCode, headers: res.headers, body: data });
            });
        });
        req.on('error', reject);
        if (options.body) {
            req.write(options.body);
        }
        req.end();
    });
}

/**
 * Setup npm registry configuration
 */
async function setupNpmRegistry(registry, token) {
    const npmrcPath = path.join(process.env.HOME || '/app', '.npmrc');
    let npmrcContent = '';
    
    if (token) {
        const registryHost = registry.replace(/^https?:\/\//, '').replace(/\/$/, '');
        npmrcContent = `//${registryHost}:_authToken=${token}\nregistry=${registry}\n`;
    } else {
        npmrcContent = `registry=${registry}\n`;
    }
    
    await fs.writeFile(npmrcPath, npmrcContent, 'utf8');
    console.log(`[package-api] Configured npm registry: ${registry}`);
}

/**
 * Download and install npm package
 * ONLY called from API request handler - never automatically
 */
async function downloadPackage(packageName, version, registry, token) {
    console.log(`[package-api] [API REQUEST] Downloading ${packageName}@${version}...`);
    console.log(`[package-api] [API REQUEST] This download was triggered by an API call, not automatic initialization`);
    
    await setupNpmRegistry(registry, token);
    
    // Ensure package.json exists
    const packageJsonPath = '/app/package.json';
    try {
        await fs.access(packageJsonPath);
    } catch {
        console.log('[package-api] Creating package.json...');
        execSync('npm init -y', { cwd: '/app', stdio: 'ignore' });
    }
    
    // Install package
    const installCmd = `npm install "${packageName}@${version}" --no-audit --no-fund --loglevel=warn --save-exact --no-save`;
    execSync(installCmd, { cwd: '/app', stdio: 'inherit' });
    
    // Get installed version
    try {
        const packageJson = JSON.parse(await fs.readFile(`/app/node_modules/${packageName}/package.json`, 'utf8'));
        console.log(`[package-api] Installed version: ${packageJson.version}`);
    } catch (e) {
        console.warn('[package-api] Could not read package version');
    }
    
    return `/app/node_modules/${packageName}`;
}

/**
 * Replace domain in JSON object with specific rules
 * Only replaces domain if object has key, flow, version, and domain fields
 */
function replaceDomainInJson(obj, targetDomain) {
    if (typeof obj !== 'object' || obj === null) {
        return obj;
    }
    
    if (Array.isArray(obj)) {
        return obj.map(item => replaceDomainInJson(item, targetDomain));
    }
    
    // Check if object should have domain replaced
    // Must have: domain (string), key, flow, and version
    const shouldReplace = typeof obj.domain === 'string' &&
                         obj.key !== undefined &&
                         obj.flow !== undefined &&
                         obj.version !== undefined;
    
    const result = shouldReplace 
        ? { ...obj, domain: targetDomain }
        : { ...obj };
    
    // Recursively process all properties
    for (const [key, value] of Object.entries(result)) {
        result[key] = replaceDomainInJson(value, targetDomain);
    }
    
    return result;
}

/**
 * Merge custom components with core sys file
 */
async function mergeCustomComponents(sysFilePath, customFolderPath, appDomain) {
    console.log(`[package-api] Merging components for ${path.basename(sysFilePath)}...`);
    
    // Read core file
    let mergedData = JSON.parse(await fs.readFile(sysFilePath, 'utf8'));
    
    // Ensure data array exists
    if (!mergedData.data) {
        mergedData.data = [];
    }
    
    const initialCount = mergedData.data.length;
    
    // Merge custom components if folder exists
    try {
        const customFolder = await fs.stat(customFolderPath);
        if (customFolder.isDirectory()) {
            const files = await fs.readdir(customFolderPath);
            const jsonFiles = files.filter(f => f.endsWith('.json'));
            
            for (const jsonFile of jsonFiles) {
                const customFilePath = path.join(customFolderPath, jsonFile);
                try {
                    const customData = JSON.parse(await fs.readFile(customFilePath, 'utf8'));
                    if (customData.data && Array.isArray(customData.data) && customData.data.length > 0) {
                        mergedData.data = mergedData.data.concat(customData.data);
                        console.log(`[package-api] Merged ${customData.data.length} items from ${jsonFile}`);
                    }
                } catch (e) {
                    console.warn(`[package-api] Failed to merge ${jsonFile}: ${e.message}`);
                }
            }
        }
    } catch (e) {
        console.log(`[package-api] No custom components folder found: ${customFolderPath}`);
    }
    
    console.log(`[package-api] Merged ${mergedData.data.length - initialCount} custom items`);
    
    // Apply domain replacement if needed
    if (appDomain && appDomain !== 'core') {
        console.log(`[package-api] Applying domain replacement: core -> ${appDomain}`);
        mergedData = replaceDomainInJson(mergedData, appDomain);
    }
    
    return mergedData;
}

/**
 * Upload JSON to vnext API
 */
async function uploadToVNextApi(jsonData, description) {
    console.log(`[package-api] Uploading ${description}...`);
    
    const response = await httpRequest(API_ENDPOINT, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(jsonData)
    });
    
    if (response.statusCode === 200 || response.statusCode === 201) {
        console.log(`[package-api] ✓ Successfully uploaded ${description} (HTTP ${response.statusCode})`);
        return true;
    } else {
        console.error(`[package-api] ✗ Failed to upload ${description} (HTTP ${response.statusCode})`);
        console.error(`[package-api] Response: ${response.body}`);
        return false;
    }
}

/**
 * Process and publish package
 */
async function processPackage(packagePath, appDomain, customComponentsPath) {
    const workflowsPath = path.join(packagePath, 'core', 'Workflows');
    
    try {
        await fs.access(workflowsPath);
    } catch {
        throw new Error(`Workflows directory not found at ${workflowsPath}`);
    }
    
    const results = {
        success: [],
        failed: []
    };
    
    // Step 1: Process sys-flows.json first (CRITICAL)
    const sysFlowsPath = path.join(workflowsPath, 'sys-flows.json');
    try {
        await fs.access(sysFlowsPath);
        console.log('[package-api] Processing CRITICAL sys-flows.json...');
        
        const customWorkflowsPath = path.join(customComponentsPath, 'Workflows');
        const mergedData = await mergeCustomComponents(sysFlowsPath, customWorkflowsPath, appDomain);
        
        if (await uploadToVNextApi(mergedData, 'sys-flows.json')) {
            results.success.push('sys-flows.json');
        } else {
            results.failed.push('sys-flows.json');
            throw new Error('Failed to upload sys-flows.json');
        }
        
        await new Promise(resolve => setTimeout(resolve, 2000)); // Rate limiting
    } catch (e) {
        console.error(`[package-api] ✗ CRITICAL ERROR: ${e.message}`);
        throw e;
    }
    
    // Step 2: Process other sys-* files
    for (const sysFileName of SYS_FILES_ORDER) {
        const sysFilePath = path.join(workflowsPath, sysFileName);
        try {
            await fs.access(sysFilePath);
            console.log(`[package-api] Processing ${sysFileName}...`);
            
            const customFolderName = SYS_FILE_MAPPINGS[sysFileName];
            const customFolderPath = path.join(customComponentsPath, customFolderName);
            const mergedData = await mergeCustomComponents(sysFilePath, customFolderPath, appDomain);
            
            if (await uploadToVNextApi(mergedData, sysFileName)) {
                results.success.push(sysFileName);
            } else {
                results.failed.push(sysFileName);
            }
            
            await new Promise(resolve => setTimeout(resolve, 1000)); // Rate limiting
        } catch (e) {
            console.warn(`[package-api] ⚠ ${sysFileName} not found or failed: ${e.message}`);
        }
    }
    
    // Step 3: Process additional workflow files
    try {
        const files = await fs.readdir(workflowsPath);
        const additionalFiles = files.filter(f => 
            f.endsWith('.json') && !f.startsWith('sys-')
        );
        
        for (const fileName of additionalFiles) {
            const filePath = path.join(workflowsPath, fileName);
            console.log(`[package-api] Processing additional file: ${fileName}...`);
            
            const customWorkflowsPath = path.join(customComponentsPath, 'Workflows');
            const mergedData = await mergeCustomComponents(filePath, customWorkflowsPath, appDomain);
            
            if (await uploadToVNextApi(mergedData, fileName)) {
                results.success.push(fileName);
            } else {
                results.failed.push(fileName);
            }
            
            await new Promise(resolve => setTimeout(resolve, 1000)); // Rate limiting
        }
    } catch (e) {
        console.warn(`[package-api] Could not process additional files: ${e.message}`);
    }
    
    return results;
}

/**
 * Wait for vnext app to be healthy
 */
async function waitForVNextApp(maxRetries = 24) {
    const healthUrl = `${VNEXT_APP_URL}/health`;
    console.log(`[package-api] Waiting for vnext-app to be healthy at ${healthUrl}...`);
    
    for (let i = 0; i < maxRetries; i++) {
        try {
            const response = await httpRequest(healthUrl, { method: 'GET' });
            if (response.statusCode === 200) {
                console.log('[package-api] VNext App is healthy!');
                return true;
            }
        } catch (e) {
            // Continue retrying
        }
        
        if (i < maxRetries - 1) {
            console.log(`[package-api] VNext App not ready yet, waiting... (${i + 1}/${maxRetries})`);
            await new Promise(resolve => setTimeout(resolve, 5000));
        }
    }
    
    throw new Error(`VNext App did not become healthy after ${maxRetries * 5} seconds`);
}

/**
 * Health check endpoint
 */
async function handleHealthCheck(req, res) {
    try {
        // Check if vnext app is healthy
        const healthUrl = `${VNEXT_APP_URL}/health`;
        const response = await httpRequest(healthUrl, { method: 'GET' });
        
        if (response.statusCode === 200) {
            res.writeHead(200, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({
                status: 'healthy',
                vnextApp: 'healthy',
                timestamp: new Date().toISOString()
            }));
        } else {
            res.writeHead(503, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({
                status: 'unhealthy',
                vnextApp: 'unhealthy',
                timestamp: new Date().toISOString()
            }));
        }
    } catch (error) {
        res.writeHead(503, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            status: 'unhealthy',
            vnextApp: 'unreachable',
            error: error.message,
            timestamp: new Date().toISOString()
        }));
    }
}

/**
 * Handle API request
 */
async function handleRequest(req, res) {
    // CORS headers
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'POST, OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
    
    if (req.method === 'OPTIONS') {
        res.writeHead(200);
        res.end();
        return;
    }
    
    // Health check endpoint
    if (req.method === 'GET' && req.url === '/health') {
        await handleHealthCheck(req, res);
        return;
    }
    
    if (req.method !== 'POST' || req.url !== '/api/package/download') {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Not Found' }));
        return;
    }
    
    try {
        const body = await parseJSON(req);
        
        const {
            packageName,
            version = 'latest',
            npmRegistry = DEFAULT_REGISTRY,
            npmToken,
            appDomain,
            customComponentsPath = CUSTOM_PATH
        } = body;
        
        // Get appDomain from request body, fallback to environment variable, then default to 'core'
        const finalAppDomain = appDomain || getDefaultAppDomain();
        
        if (!packageName) {
            res.writeHead(400, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: 'packageName is required' }));
            return;
        }
        
        console.log(`[package-api] [API REQUEST] Received package download request: ${packageName}@${version}`);
        console.log(`[package-api] [API REQUEST] Starting package download process...`);
        
        // Wait for vnext app to be ready
        await waitForVNextApp();
        
        // Download package - ONLY happens when API request is made
        const packagePath = await downloadPackage(packageName, version, npmRegistry, npmToken);
        
        // Find core directory
        const corePath = path.join(packagePath, 'core');
        try {
            await fs.access(corePath);
        } catch {
            throw new Error(`Core directory not found at ${corePath}`);
        }
        
        // Process and publish
        const results = await processPackage(packagePath, finalAppDomain, customComponentsPath);
        
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            success: true,
            message: 'Package processed and published successfully',
            results: {
                successful: results.success,
                failed: results.failed
            }
        }));
        
    } catch (error) {
        console.error('[package-api] Error:', error);
        res.writeHead(500, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            success: false,
            error: error.message
        }));
    }
}

/**
 * Run automatic initialization (like init-vnext-core.sh)
 * 
 * NOTE: This function is NOT called automatically anymore.
 * It's kept for reference but disabled. Packages must be downloaded via API requests.
 * 
 * @deprecated - Use API endpoint /api/package/download instead
 */
async function runAutomaticInit() {
    // This function should NEVER be called automatically
    console.error('[package-api] ERROR: runAutomaticInit() should not be called!');
    console.error('[package-api] Automatic initialization is disabled. Use API endpoint instead.');
    throw new Error('Automatic initialization is disabled. Use POST /api/package/download endpoint instead.');
    const packageName = process.env.VNEXT_CORE_RUNTIME_VERSION 
        ? `@burgan-tech/vnext-core-runtime@${process.env.VNEXT_CORE_RUNTIME_VERSION}`
        : '@burgan-tech/vnext-core-runtime@latest';
    
    const version = process.env.VNEXT_CORE_RUNTIME_VERSION || 'latest';
    const registry = process.env.NPM_REGISTRY || DEFAULT_REGISTRY;
    const token = process.env.NPM_TOKEN;
    const appDomain = getDefaultAppDomain();
    const customComponentsPath = process.env.CUSTOM_COMPONENTS_PATH || CUSTOM_PATH;
    
    console.log('[package-api] Running automatic initialization...');
    console.log(`[package-api] Package: ${packageName}`);
    console.log(`[package-api] Registry: ${registry}`);
    console.log(`[package-api] App Domain: ${appDomain}`);
    
    try {
        // Wait for vnext app
        await waitForVNextApp();
        
        // Download package
        const packagePath = await downloadPackage('@burgan-tech/vnext-core-runtime', version, registry, token);
        
        // Find core directory
        const corePath = path.join(packagePath, 'core');
        try {
            await fs.access(corePath);
        } catch {
            throw new Error(`Core directory not found at ${corePath}`);
        }
        
        // Process and publish
        const results = await processPackage(packagePath, appDomain, customComponentsPath);
        
        console.log('');
        console.log('==========================================');
        console.log('✓ VNext Core Runtime Initialization COMPLETED!');
        console.log('==========================================');
        console.log(`Successfully processed: ${results.success.length} files`);
        if (results.failed.length > 0) {
            console.log(`Failed: ${results.failed.length} files`);
        }
        
        return results;
    } catch (error) {
        console.error('[package-api] Initialization failed:', error);
        process.exit(1);
    }
}

/**
 * Start HTTP server
 */
function startServer() {
    const server = http.createServer(handleRequest);
    
    server.listen(PORT, () => {
        console.log(`[package-api] Package API Server started on port ${PORT}`);
        console.log(`[package-api] Endpoint: POST http://localhost:${PORT}/api/package/download`);
        console.log(`[package-api] VNext App URL: ${VNEXT_APP_URL}`);
    });
    
    server.on('error', (err) => {
        console.error('[package-api] Server error:', err);
        process.exit(1);
    });
}

// Main execution logic
if (require.main === module) {
    // Always start API server - NO automatic initialization, NO automatic downloads
    console.log('[package-api] ==========================================');
    console.log('[package-api] Starting Package API Server...');
    console.log('[package-api] Automatic initialization: DISABLED');
    console.log('[package-api] Automatic package downloads: DISABLED');
    console.log('[package-api] Packages will ONLY be downloaded via API requests');
    console.log('[package-api] ==========================================');
    startServer();
}

module.exports = { startServer, processPackage, downloadPackage, runAutomaticInit };

