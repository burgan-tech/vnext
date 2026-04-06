#!/usr/bin/env node

/**
 * VNext Package API Server
 * 
 * This server provides an API endpoint to download npm packages and publish them to vnext API.
 * It replicates the functionality of init-vnext-core.sh but exposes it as a REST API.
 * 
 * Version Format: MAJOR.MINOR.PATCH-pkg.PKG_VERSION+PKG_NAME
 * - MAJOR.MINOR.PATCH → Artifact version (from component's version field)
 * - pkg.PKG_VERSION → Package version (from vnext.config.json)
 * - PKG_NAME → Domain/Package name (from vnext.config.json)
 */

const http = require('http');
const https = require('https');
const crypto = require('crypto');
const fs = require('fs').promises;
const path = require('path');
const { execSync, spawn } = require('child_process');
const { promisify } = require('util');
const exec = promisify(require('child_process').exec);

const PORT = process.env.PACKAGE_API_PORT || 3000;
const VNEXT_APP_URL = process.env.VNEXT_APP_URL || 'http://host.docker.internal:4201';
const API_ENDPOINT = `${VNEXT_APP_URL}/api/v1/definitions/publish`;
const DEFAULT_REGISTRY = process.env.NPM_REGISTRY || 'https://registry.npmjs.org/';

/**
 * Server timeout configuration (in milliseconds)
 * These can be overridden via environment variables for long-running pipelines
 * Default: 10 minutes (600000ms)
 */
const SERVER_TIMEOUT_MS = parseInt(process.env.SERVER_TIMEOUT_MS, 10) || 600000;
const SERVER_KEEP_ALIVE_TIMEOUT_MS = parseInt(process.env.SERVER_KEEP_ALIVE_TIMEOUT_MS, 10) || 600000;
const SERVER_HEADERS_TIMEOUT_MS = parseInt(process.env.SERVER_HEADERS_TIMEOUT_MS, 10) || (SERVER_KEEP_ALIVE_TIMEOUT_MS + 10000);

/** 
 * The special core runtime package that requires domain replacement for SYS files
 */
const VNEXT_CORE_RUNTIME_PACKAGE = '@burgan-tech/vnext-core-runtime';

const JOB_TTL_MS = 30 * 60 * 1000; // 30 minutes
const jobs = new Map();

/**
 * @typedef {'accepted'|'processing'|'completed'|'failed'} JobStatus
 * @typedef {{current: number, total: number, currentFile: string, phase: string}} JobProgress
 * @typedef {{id: string, packageName: string, status: JobStatus, progress: JobProgress|null, results: Object|null, error: string|null, createdAt: string, updatedAt: string}} Job
 */

/**
 * Create a new job entry and store it.
 * @param {string} packageName
 * @returns {Job}
 */
function createJob(packageName) {
    cleanupExpiredJobs();
    const id = crypto.randomUUID();
    const job = {
        id,
        packageName,
        status: 'accepted',
        progress: null,
        results: null,
        error: null,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
    };
    jobs.set(id, job);
    return job;
}

/**
 * Update mutable fields on an existing job.
 * @param {Job} job
 * @param {Partial<Job>} fields
 */
function updateJob(job, fields) {
    Object.assign(job, fields, { updatedAt: new Date().toISOString() });
}

/**
 * Remove jobs older than JOB_TTL_MS that are in a terminal state.
 */
function cleanupExpiredJobs() {
    const now = Date.now();
    for (const [id, job] of jobs) {
        if ((job.status === 'completed' || job.status === 'failed') &&
            (now - new Date(job.updatedAt).getTime()) > JOB_TTL_MS) {
            jobs.delete(id);
        }
    }
}

/**
 * ANSI color codes for console output
 */
const colors = {
    reset: '\x1b[0m',
    red: '\x1b[31m',
    green: '\x1b[32m',
    yellow: '\x1b[33m',
    blue: '\x1b[34m',
    magenta: '\x1b[35m',
    cyan: '\x1b[36m',
    white: '\x1b[37m',
    bold: '\x1b[1m',
    dim: '\x1b[2m'
};

/**
 * Returns current ISO 8601 timestamp string
 */
const getTimestamp = () => new Date().toISOString();

/**
 * Logging utilities with colors and timestamps
 */
const log = {
    info: (msg) => console.log(`${colors.dim}${getTimestamp()}${colors.reset} ${colors.cyan}[package-api]${colors.reset} ${msg}`),
    success: (msg) => console.log(`${colors.dim}${getTimestamp()}${colors.reset} ${colors.green}[package-api] ✓${colors.reset} ${msg}`),
    warn: (msg) => console.log(`${colors.dim}${getTimestamp()}${colors.reset} ${colors.yellow}[package-api] ⚠${colors.reset} ${msg}`),
    error: (msg) => console.error(`${colors.dim}${getTimestamp()}${colors.reset} ${colors.red}[package-api] ✗ ${msg}${colors.reset}`),
    section: (msg) => console.log(`\n${colors.bold}${colors.blue}${'═'.repeat(60)}${colors.reset}\n${colors.dim}${getTimestamp()}${colors.reset} ${colors.bold}${colors.blue}  ${msg}${colors.reset}\n${colors.bold}${colors.blue}${'═'.repeat(60)}${colors.reset}`),
    subsection: (msg) => console.log(`\n${colors.cyan}${'─'.repeat(50)}${colors.reset}\n${colors.dim}${getTimestamp()}${colors.reset} ${colors.cyan}  ${msg}${colors.reset}\n${colors.cyan}${'─'.repeat(50)}${colors.reset}`),
    component: (type, name) => console.log(`${colors.dim}${getTimestamp()}${colors.reset} ${colors.magenta}[package-api]${colors.reset} ${colors.bold}[${type.toUpperCase()}]${colors.reset} ${name}`),
    detail: (msg) => console.log(`${colors.dim}${getTimestamp()} [package-api]   ${msg}${colors.reset}`)
};

/**
 * Default component paths if not specified in vnext.config.json
 */
const DEFAULT_COMPONENT_PATHS = {
    componentsRoot: 'core',
    tasks: 'Tasks',
    views: 'Views',
    functions: 'Functions',
    extensions: 'Extensions',
    workflows: 'Workflows',
    schemas: 'Schemas'
};

/**
 * Recursively find all JSON files in a directory
 * 
 * Excludes:
 * - .meta directories
 * - Files ending with -diagram.json
 * 
 * @param {string} dirPath - Directory path to search
 * @returns {Promise<string[]>} Array of absolute paths to JSON files
 */
async function findJsonFilesRecursively(dirPath) {
    const jsonFiles = [];
    
    try {
        const entries = await fs.readdir(dirPath, { withFileTypes: true });
        
        for (const entry of entries) {
            const fullPath = path.join(dirPath, entry.name);
            
            if (entry.isDirectory()) {
                // Skip .meta directories
                if (entry.name === '.meta') {
                    continue;
                }
                // Recursively search subdirectories
                const subFiles = await findJsonFilesRecursively(fullPath);
                jsonFiles.push(...subFiles);
            } else if (entry.isFile() && entry.name.endsWith('.json')) {
                // Skip diagram files
                if (entry.name.endsWith('.diagram.json')) {
                    continue;
                }
                jsonFiles.push(fullPath);
            }
        }
    } catch (e) {
        // Directory doesn't exist or can't be read - this is often expected
    }
    
    return jsonFiles;
}

/**
 * Get all component directories from vnext.config.json paths
 * 
 * @param {string} packagePath - Path to the package root
 * @param {Object} vnextConfig - Parsed vnext.config.json
 * @returns {Object[]} Array of { type, path } objects for each component directory
 */
function getComponentDirectories(packagePath, vnextConfig) {
    const paths = vnextConfig.paths || DEFAULT_COMPONENT_PATHS;
    const componentsRoot = paths.componentsRoot || DEFAULT_COMPONENT_PATHS.componentsRoot;
    
    const directories = [];
    
    // Get all path entries except componentsRoot
    const componentTypes = ['tasks', 'views', 'functions', 'extensions', 'workflows', 'schemas'];
    
    for (const type of componentTypes) {
        const relativePath = paths[type] || DEFAULT_COMPONENT_PATHS[type];
        if (relativePath) {
            const fullPath = path.join(packagePath, componentsRoot, relativePath);
            directories.push({
                type: type,
                path: fullPath,
                relativePath: path.join(componentsRoot, relativePath)
            });
        }
    }
    
    return directories;
}

/**
 * Read vnext.config.json from package directory
 * @param {string} packagePath - Path to the package
 * @returns {Object} vnext config object with version and domain
 */
async function readVNextConfig(packagePath) {
    const configPath = path.join(packagePath, 'vnext.config.json');
    try {
        const configContent = await fs.readFile(configPath, 'utf8');
        const config = JSON.parse(configContent);
        log.info(`Read vnext.config.json - version: ${config.version}, domain: ${config.domain}`);
        return {
            version: config.version || '1.0.0',
            domain: config.domain || 'core',
            paths: config.paths || {}
        };
    } catch (e) {
        log.warn(`vnext.config.json not found or invalid at ${configPath}, using defaults`);
        return {
            version: '1.0.0',
            domain: 'core',
            paths: {}
        };
    }
}

/**
 * Generate version string in the format: MAJOR.MINOR.PATCH-pkg.PKG_VERSION+PKG_NAME
 * 
 * @param {string} artifactVersion - Component version (e.g., "1.0.0")
 * @param {string} packageVersion - Package version from vnext.config.json (e.g., "1.17.0")
 * @param {string} domain - Domain/Package name from vnext.config.json (e.g., "account")
 * @returns {string} Generated version (e.g., "1.0.0-pkg.1.17.0+account")
 */
function generateVersion(artifactVersion, packageVersion, domain) {
    if (!artifactVersion) {
        artifactVersion = '1.0.0';
    }
    
    return `${artifactVersion}-pkg.${packageVersion}+${domain}`;
}

/**
 * Update version fields in a component object
 * Updates version at root level only
 * 
 * NOTE: data[] items are processed separately by processSysFileData,
 * so we don't process them here to avoid double-processing.
 * 
 * @param {Object} component - Component object to update
 * @param {string} packageVersion - Package version from vnext.config.json
 * @param {string} domain - Domain from vnext.config.json
 * @returns {Object} Updated component object
 */
function updateComponentVersions(component, packageVersion, domain) {
    if (typeof component !== 'object' || component === null) {
        return component;
    }
    
    const updated = { ...component };
    
    // Update root level version (component's artifact version)
    if (updated.version && typeof updated.version === 'string') {
        const newVersion = generateVersion(updated.version, packageVersion, domain);
        updated.version = newVersion;
    }
    
    // NOTE: data[] items are NOT processed here - they are processed separately
    // by processSysFileData which calls processComponentFile for each item
    
    return updated;
}

/**
 * Process component file: update versions and optionally replace domain
 * 
 * @param {Object} jsonData - Parsed JSON data from component file
 * @param {Object} vnextConfig - Config from vnext.config.json
 * @param {boolean} shouldReplaceDomain - Whether to replace domain (only for core runtime SYS files)
 * @param {string} targetDomain - Target domain for replacement (only used if shouldReplaceDomain is true)
 * @returns {Object} Processed JSON data
 */
function processComponentFile(jsonData, vnextConfig, shouldReplaceDomain, targetDomain) {
    let processed = { ...jsonData };
    
    // Step 1: Update versions in the component
    processed = updateComponentVersions(processed, vnextConfig.version, vnextConfig.domain);
    
    // Step 2: Replace domain at ALL levels if requested
    if (shouldReplaceDomain && targetDomain) {
        log.detail(`Replacing domain → ${targetDomain}`);
        processed = replaceDomainInJson(processed, targetDomain);
    }
    
    return processed;
}

/**
 * Process sys file data: update versions and domains for root object and data array items
 * 
 * Supports two structures:
 * 1. Object with data array: { key, version, domain, ..., data: [{ key, version, ... }, ...] }
 * 2. Plain array of components: [{ key, version, ... }, ...]
 * 
 * IMPORTANT: Both root object AND data[] items are processed for version/domain replacement
 * 
 * @param {Object|Array} sysFileData - Parsed sys-*.json file (object with data array OR plain array)
 * @param {Object} vnextConfig - Config from vnext.config.json
 * @param {boolean} shouldReplaceDomain - Whether to replace domain
 * @param {string} targetDomain - Target domain for replacement
 * @returns {Object|Array} Processed sys file data
 */
function processSysFileData(sysFileData, vnextConfig, shouldReplaceDomain, targetDomain) {
    // Case 1: Plain array of components (no wrapper object)
    if (Array.isArray(sysFileData)) {
        log.detail(`Processing ${sysFileData.length} components (array format)`);
        return sysFileData.map((component, index) => {
            return processComponentFile(component, vnextConfig, shouldReplaceDomain, targetDomain);
        });
    }
    
    // Case 2: Object (with or without data array)
    // ALWAYS process the root object first for version/domain
    let processed = { ...sysFileData };
    
    // Check if root object has version and key - if so, process it
    if (processed.version && processed.key) {
        log.detail(`Processing root component: ${processed.key}`);
        processed = processComponentFile(processed, vnextConfig, shouldReplaceDomain, targetDomain);
    }
    
    // Also process data[] array items if present
    if (Array.isArray(processed.data) && processed.data.length > 0) {
        const dataLength = processed.data.length;
        log.detail(`Processing ${dataLength} seed data items`);
        processed.data = processed.data.map((component, index) => {
            return processComponentFile(component, vnextConfig, shouldReplaceDomain, targetDomain);
        });
    }
    
    return processed;
}

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
 * 
 * Supports two authentication strategies:
 * 1. Token-based authentication (_authToken) - for npmjs.org and compatible registries
 * 2. Username/Password authentication (_password + username + email) - for Azure DevOps / TFS Artifacts
 * 
 * Auth Strategy Decision:
 * - If npmToken exists → use _authToken
 * - If npmUsername + npmPassword exists → use _password (Base64 encoded)
 * - If neither exists → only set registry=
 * 
 * @param {string} registry - NPM registry URL
 * @param {Object} authOptions - Authentication options
 * @param {string} [authOptions.token] - NPM token for _authToken based auth
 * @param {string} [authOptions.username] - Username for TFS Artifacts auth
 * @param {string} [authOptions.password] - Password for TFS Artifacts auth (will be Base64 encoded)
 * @param {string} [authOptions.email] - Email for TFS Artifacts auth (optional, defaults to 'unused@dev.azure.com')
 */
async function setupNpmRegistry(registry, authOptions = {}) {
    const npmrcPath = path.join(process.env.HOME || '/app', '.npmrc');
    const registryHost = registry.replace(/^https?:\/\//, '').replace(/\/$/, '');
    let npmrcContent = '';
    
    const { token, username, password, email } = authOptions;
    
    if (token) {
        // Strategy 1: Token-based authentication (_authToken)
        // Compatible with npmjs.org and registries that support tokens
        log.info('Using token-based authentication (_authToken)');
        npmrcContent = [
            `registry=${registry}`,
            `//${registryHost}/:_authToken=${token}`,
            ''
        ].join('\n');
    } else if (username && password) {
        // Strategy 2: Username/Password authentication for Azure DevOps / TFS Artifacts
        // Uses _password (Base64 encoded) + username + email
        log.info('Using TFS Artifacts authentication (_password + username)');
        const base64Password = Buffer.from(password).toString('base64');
        const authEmail = email || 'unused@dev.azure.com';
        
        npmrcContent = [
            `registry=${registry}`,
            `//${registryHost}/:username=${username}`,
            `//${registryHost}/:_password=${base64Password}`,
            `//${registryHost}/:email=${authEmail}`,
            'always-auth=true',
            ''
        ].join('\n');
    } else {
        // No authentication - public registry
        log.info('No authentication configured - using public registry');
        npmrcContent = `registry=${registry}\n`;
    }
    
    await fs.writeFile(npmrcPath, npmrcContent, 'utf8');
    log.info(`Configured npm registry: ${registry}`);
}

/**
 * Download and install npm package
 * ONLY called from API request handler - never automatically
 * 
 * @param {string} packageName - Name of the npm package to download
 * @param {string} version - Version of the package (e.g., 'latest', '1.0.0')
 * @param {string} registry - NPM registry URL
 * @param {Object} authOptions - Authentication options
 * @param {string} [authOptions.token] - NPM token for _authToken based auth
 * @param {string} [authOptions.username] - Username for TFS Artifacts auth
 * @param {string} [authOptions.password] - Password for TFS Artifacts auth
 * @param {string} [authOptions.email] - Email for TFS Artifacts auth
 */
async function downloadPackage(packageName, version, registry, authOptions = {}) {
    log.section(`Downloading Package: ${packageName}@${version}`);
    log.info('This download was triggered by an API call');
    
    await setupNpmRegistry(registry, authOptions);
    
    // Ensure package.json exists
    const packageJsonPath = '/app/package.json';
    try {
        await fs.access(packageJsonPath);
    } catch {
        log.info('Creating package.json...');
        execSync('npm init -y', { cwd: '/app', stdio: 'ignore' });
    }
    
    // Install package
    const installCmd = `npm install "${packageName}@${version}" --no-audit --no-fund --loglevel=warn --save-exact --no-save`;
    execSync(installCmd, { cwd: '/app', stdio: 'inherit' });
    
    // Get installed version
    try {
        const packageJson = JSON.parse(await fs.readFile(`/app/node_modules/${packageName}/package.json`, 'utf8'));
        log.success(`Installed version: ${packageJson.version}`);
    } catch (e) {
        log.warn('Could not read package version');
    }
    
    return `/app/node_modules/${packageName}`;
}

/**
 * Replace ALL domain fields in JSON object at ALL levels
 * 
 * Replaces domain in:
 * - Root level domain (if object has key, flow, version, domain)
 * - attributes.domain
 * - data[].domain
 * - data[].attributes.domain
 * - Any nested object's domain field
 * 
 * @param {Object} obj - Object to process
 * @param {string} targetDomain - Target domain to replace with
 */
function replaceDomainInJson(obj, targetDomain) {
    if (typeof obj !== 'object' || obj === null) {
        return obj;
    }
    
    if (Array.isArray(obj)) {
        return obj.map(item => replaceDomainInJson(item, targetDomain));
    }
    
    // Create a copy of the object
    const result = { ...obj };
    
    // Replace domain field if it exists and is a string
    if (typeof result.domain === 'string') {
        result.domain = targetDomain;
    }
    
    // Recursively process all properties to find nested domain fields
    // BUT skip "config" and "process" keys - it should always remain unchanged
    for (const [key, value] of Object.entries(result)) {
        if (key !== 'domain' && key !== 'config' && key !== "process") { // Skip domain (already processed) and attributes (always preserved)
            result[key] = replaceDomainInJson(value, targetDomain);
        }
    }
    
    return result;
} 


/**
 * Upload JSON to vnext API
 */
async function uploadToVNextApi(jsonData, description) {
    log.detail(`Uploading ${description}...`);
    
    const response = await httpRequest(API_ENDPOINT, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(jsonData)
    });
    
    if (response.statusCode === 200 || response.statusCode === 201) {
        return true;
    } else {
        log.error(`Failed to upload ${description} — HTTP ${response.statusCode}`);
        logErrorResponse(response.body, description);
        log.detail(`Raw response body: ${response.body}`);
        return false;
    }
}

/**
 * Parse and display error response in a readable format
 * 
 * @param {string} responseBody - Raw response body from API
 * @param {string} context - Context description (file path, component name, etc.)
 */
function logErrorResponse(responseBody, context) {
    try {
        const errorData = JSON.parse(responseBody);
        
        // Display main error information
        if (errorData.code) {
            log.error(`  Error Code: ${errorData.code}`);
        }
        if (errorData.message) {
            log.error(`  Message: ${errorData.message}`);
        }
        if (errorData.prefix) {
            log.error(`  Type: ${errorData.prefix}`);
        }
        if (errorData.target) {
            log.error(`  Target: ${errorData.target}`);
        }
        if (errorData.detail) {
            log.error(`  Detail: ${errorData.detail}`);
        }
        
        // Display validation errors if present
        if (errorData.validationErrors && Array.isArray(errorData.validationErrors)) {
            log.error(`  Validation Errors (${errorData.validationErrors.length}):`);
            errorData.validationErrors.forEach((validationError, index) => {
                const errorMessage = validationError.errorMessage || validationError.ErrorMessage || 'Unknown error';
                const memberNames = validationError.memberNames || validationError.MemberNames || [];
                const members = Array.isArray(memberNames) ? memberNames.join(', ') : memberNames;
                
                log.error(`    ${index + 1}. ${errorMessage}`);
                if (members) {
                    log.error(`       Field(s): ${members}`);
                }
            });
        }
        
        // Handle nested error structure (some APIs wrap errors)
        if (errorData.error) {
            if (typeof errorData.error === 'string') {
                log.error(`  Error: ${errorData.error}`);
            } else if (typeof errorData.error === 'object') {
                logErrorResponse(JSON.stringify(errorData.error), context);
            }
        }
        
        // Handle errors array
        if (errorData.errors && Array.isArray(errorData.errors)) {
            log.error(`  Errors (${errorData.errors.length}):`);
            errorData.errors.forEach((err, index) => {
                if (typeof err === 'string') {
                    log.error(`    ${index + 1}. ${err}`);
                } else if (err.message) {
                    log.error(`    ${index + 1}. ${err.message}`);
                    if (err.code) {
                        log.error(`       Code: ${err.code}`);
                    }
                }
            });
        }
        
        // Handle Problem Details format (RFC 7807)
        if (errorData.title) {
            log.error(`  Title: ${errorData.title}`);
        }
        if (errorData.status) {
            log.error(`  Status: ${errorData.status}`);
        }
        if (errorData.traceId) {
            log.error(`  Trace ID: ${errorData.traceId}`);
        }
        
    } catch (e) {
        // If response is not valid JSON, display as plain text
        if (responseBody && responseBody.trim()) {
            log.error(`  Raw Response: ${responseBody.substring(0, 2000)}${responseBody.length > 2000 ? '...' : ''}`);
        }
    }
}

/**
 * Process a single JSON file: read, process versions/domain, upload
 * 
 * @param {string} jsonFilePath - Full path to the JSON file
 * @param {string} packagePath - Path to the package root
 * @param {string} componentType - Type of component (workflows, tasks, etc.)
 * @param {Object} vnextConfig - Package config
 * @param {boolean} shouldReplaceDomain - Whether to replace domain fields
 * @param {string} appDomain - Target app domain
 * @param {Object} results - Results object to update
 * @param {Job} [job] - Optional job for progress tracking
 */
async function processJsonFile(jsonFilePath, packagePath, componentType, vnextConfig, shouldReplaceDomain, appDomain, results, job) {
            const relativePath = path.relative(packagePath, jsonFilePath);
            const fileName = path.basename(jsonFilePath);
            
    log.component(componentType, fileName);
            
            try {
                // Read the JSON file
                const fileContent = await fs.readFile(jsonFilePath, 'utf8');
                let jsonData = JSON.parse(fileContent);
                
                // Process: update versions and optionally replace domain
        jsonData = processSysFileData(jsonData, vnextConfig, shouldReplaceDomain, appDomain);
                
                // Upload to API
                if (await uploadToVNextApi(jsonData, relativePath)) {
                    results.success.push(relativePath);
            log.success(`Uploaded: ${relativePath}`);
                } else {
                    results.failed.push(relativePath);
            log.error(`Failed to upload: ${relativePath}`);
                }
                
                // Rate limiting
                await new Promise(resolve => setTimeout(resolve, 500));
                
            } catch (e) {
        log.error(`Failed to process ${relativePath}: ${e.message}`);
                results.failed.push(relativePath);
            }

    if (job) {
        const current = results.success.length + results.failed.length;
        updateJob(job, {
            progress: {
                ...job.progress,
                current,
                currentFile: relativePath,
            },
        });
    }
        }

/**
 * Process workflows directory with special ordering for runtime package
 * Order: sys-flows.json first, then other workflow files
 * 
 * @param {Object} workflowDir - Workflow directory info
 * @param {string} packagePath - Path to the package root
 * @param {Object} vnextConfig - Package config
 * @param {boolean} shouldReplaceDomain - Whether to replace domain fields
 * @param {string} appDomain - Target app domain
 * @param {Object} results - Results object to update
 * @param {Job} [job] - Optional job for progress tracking
 */
async function processWorkflowsWithOrdering(workflowDir, packagePath, vnextConfig, shouldReplaceDomain, appDomain, results, job) {
    log.subsection(`Processing WORKFLOWS (Priority Order)`);
    
    try {
        await fs.access(workflowDir.path);
    } catch {
        log.warn(`Directory not found: ${workflowDir.relativePath}, skipping...`);
        results.skipped.push(workflowDir.type);
        return;
    }
    
    // Find all JSON files recursively in workflows directory
    const jsonFiles = await findJsonFilesRecursively(workflowDir.path);
    log.info(`Found ${jsonFiles.length} JSON files in workflows`);
    
    // Separate sys-flows from other files
    const sysFlowsFile = jsonFiles.find(f => path.basename(f).toLowerCase() === 'sys-flows.json');
    const otherFiles = jsonFiles.filter(f => path.basename(f).toLowerCase() !== 'sys-flows.json');
    
    // Step 1: Process sys-flows.json first (if exists)
    if (sysFlowsFile) {
        log.info(`${colors.bold}Step 1: Loading sys-flows (Flow Definitions)${colors.reset}`);
        await processJsonFile(sysFlowsFile, packagePath, 'workflows', vnextConfig, shouldReplaceDomain, appDomain, results, job);
    } else {
        log.warn('sys-flows.json not found in Workflows directory');
    }
    
    // Step 2: Process other workflow files
    if (otherFiles.length > 0) {
        log.info(`${colors.bold}Step 2: Loading other workflow files (${otherFiles.length} files)${colors.reset}`);
        for (const jsonFilePath of otherFiles) {
            await processJsonFile(jsonFilePath, packagePath, 'workflows', vnextConfig, shouldReplaceDomain, appDomain, results, job);
        }
    }
}

/**
 * Process a component directory (non-workflow)
 * 
 * @param {Object} componentDir - Component directory info
 * @param {string} packagePath - Path to the package root
 * @param {Object} vnextConfig - Package config
 * @param {boolean} shouldReplaceDomain - Whether to replace domain fields
 * @param {string} appDomain - Target app domain
 * @param {Object} results - Results object to update
 * @param {Job} [job] - Optional job for progress tracking
 */
async function processComponentDirectory(componentDir, packagePath, vnextConfig, shouldReplaceDomain, appDomain, results, job) {
    log.subsection(`Processing ${componentDir.type.toUpperCase()}`);
    
    try {
        await fs.access(componentDir.path);
    } catch {
        log.warn(`Directory not found: ${componentDir.relativePath}, skipping...`);
        results.skipped.push(componentDir.type);
        return;
    }
    
    // Find all JSON files recursively in this directory
    const jsonFiles = await findJsonFilesRecursively(componentDir.path);
    log.info(`Found ${jsonFiles.length} JSON files in ${componentDir.type}`);
    
    // Process each JSON file
    for (const jsonFilePath of jsonFiles) {
        await processJsonFile(jsonFilePath, packagePath, componentDir.type, vnextConfig, shouldReplaceDomain, appDomain, results, job);
    }
}

/**
 * Process and publish package
 * 
 * Processes all JSON files in component directories defined in vnext.config.json paths.
 * - Version replacement is applied to ALL files
 * - Domain replacement is applied if shouldReplaceDomain is true and appDomain is provided
 * 
 * For isRuntimePackage=true:
 *   1. Workflows are processed FIRST
 *   2. Within Workflows, sys-flows.json is loaded FIRST
 *   3. Then other workflow files
 *   4. Then all other component types
 * 
 * For isRuntimePackage=false: Order doesn't matter
 * 
 * @param {string} packagePath - Path to the downloaded package
 * @param {string} packageName - Name of the npm package (e.g., "@burgan-tech/vnext-core-runtime")
 * @param {string|null} appDomain - Target app domain for domain replacement (null = no replacement)
 * @param {boolean} isRuntimePackage - Whether this is the runtime package (special ordering)
 * @param {Job} [job] - Optional job for progress tracking
 */
async function processPackage(packagePath, packageName, appDomain, isRuntimePackage = false, job) {
    // Read vnext.config.json from package
    const vnextConfig = await readVNextConfig(packagePath);
    
    // Determine if domain replacement should be applied
    const shouldReplaceDomain = appDomain && appDomain.trim() !== '';
    
    log.section(`Processing Package: ${packageName}`);
    log.info(`Package Type: ${isRuntimePackage ? 'Runtime Package (Special Ordering)' : 'Standard Package'}`);
    log.info(`Domain Replacement: ${shouldReplaceDomain ? `ENABLED → ${appDomain}` : 'DISABLED'}`);
    log.info(`Config Version: ${vnextConfig.version}`);
    log.info(`Config Domain: ${vnextConfig.domain}`);
    
    // Get all component directories from vnext.config.json paths
    const componentDirs = getComponentDirectories(packagePath, vnextConfig);
    
    log.subsection('Component Directories');
    componentDirs.forEach(dir => log.detail(`${dir.type}: ${dir.relativePath}`));
    
    // Pre-count total files for progress tracking
    let totalFiles = 0;
    for (const dir of componentDirs) {
        try {
            const files = await findJsonFilesRecursively(dir.path);
            totalFiles += files.length;
        } catch { /* directory may not exist */ }
    }

    if (job) {
        updateJob(job, {
            progress: { current: 0, total: totalFiles, currentFile: '', phase: 'uploading' },
        });
    }

    const results = {
        success: [],
        failed: [],
        skipped: []
    };
    
    if (isRuntimePackage) {
        // Special ordering for runtime package
        log.section('Runtime Package - Priority Loading Order');
        log.info('Order: 1) Workflows (sys-flows first) → 2) Other Components');
        
        // Step 1: Process Workflows FIRST (with sys-flows priority)
        const workflowDir = componentDirs.find(d => d.type === 'workflows');
        if (workflowDir) {
            await processWorkflowsWithOrdering(workflowDir, packagePath, vnextConfig, shouldReplaceDomain, appDomain, results, job);
        } else {
            log.warn('Workflows directory not configured, skipping...');
        }
        
        // Step 2: Process other component directories (excluding workflows)
        const otherDirs = componentDirs.filter(d => d.type !== 'workflows');
        log.section('Loading Other Components');
        
        for (const componentDir of otherDirs) {
            await processComponentDirectory(componentDir, packagePath, vnextConfig, shouldReplaceDomain, appDomain, results, job);
        }
    } else {
        // Standard package - no special ordering
        log.section('Standard Package - Loading Components');
        
        for (const componentDir of componentDirs) {
            await processComponentDirectory(componentDir, packagePath, vnextConfig, shouldReplaceDomain, appDomain, results, job);
        }
    }
    
    // Summary
    log.section('Processing Complete');
    log.success(`Successfully processed: ${results.success.length} files`);
    if (results.failed.length > 0) {
        log.error(`Failed: ${results.failed.length} files`);
        results.failed.forEach(f => log.error(`  - ${f}`));
    }
    if (results.skipped.length > 0) {
        log.warn(`Skipped directories: ${results.skipped.length}`);
        results.skipped.forEach(d => log.warn(`  - ${d}`));
    }
    
    return results;
}

/**
 * Wait for vnext app to be healthy
 */
async function waitForVNextApp(maxRetries = 24) {
    const healthUrl = `${VNEXT_APP_URL}/health`;
    log.info(`Waiting for vnext-app to be healthy at ${healthUrl}...`);
    
    for (let i = 0; i < maxRetries; i++) {
        try {
            const response = await httpRequest(healthUrl, { method: 'GET' });
            if (response.statusCode === 200) {
                log.success('VNext App is healthy!');
                return true;
            }
        } catch (e) {
            // Continue retrying
        }
        
        if (i < maxRetries - 1) {
            log.warn(`VNext App not ready yet, waiting... (${i + 1}/${maxRetries})`);
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
 * Handle Runtime Package Publish (async job pattern)
 * Endpoint: POST /api/package/runtime/publish
 * 
 * Returns 202 Accepted immediately with a jobId.
 * Processing runs in the background; poll GET /api/package/publish/status/:jobId for progress.
 */
async function handleRuntimePublish(req, res) {
    try {
        const body = await parseJSON(req);
        
        const {
            version = 'latest',
            npmRegistry = DEFAULT_REGISTRY,
            npmToken,
            npmUsername,
            npmPassword,
            npmEmail,
            appDomain
        } = body;
        
        if (!appDomain || appDomain.trim() === '') {
            res.writeHead(400, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ 
                error: 'appDomain is required for runtime package publish',
                message: 'Please provide appDomain parameter (e.g., "core", "my-domain")'
            }));
            return;
        }

        const job = createJob(VNEXT_CORE_RUNTIME_PACKAGE);

        log.section(`API Request: Runtime Package Publish [job=${job.id}]`);
        log.info(`Package: ${VNEXT_CORE_RUNTIME_PACKAGE}@${version}`);
        log.info(`App Domain: ${appDomain} (REQUIRED)`);
        log.info(`Domain Replacement: ALL domains will be replaced`);

        res.writeHead(202, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            jobId: job.id,
            statusUrl: `/api/package/publish/status/${job.id}`,
            message: 'Job accepted. Poll statusUrl for progress.',
        }));

        runPackagePublishJob(job, {
            packageName: VNEXT_CORE_RUNTIME_PACKAGE,
            version,
            npmRegistry,
            authOptions: { token: npmToken, username: npmUsername, password: npmPassword, email: npmEmail },
            appDomain,
            isRuntimePackage: true,
        });
        
    } catch (error) {
        log.error(`Error: ${error.message}`);
        if (error.stack) {
            log.error(error.stack);
        }
        res.writeHead(500, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            success: false,
            error: error.message
        }));
    }
}

/**
 * Handle Standard Package Publish (async job pattern)
 * Endpoint: POST /api/package/publish
 * 
 * Returns 202 Accepted immediately with a jobId.
 * Processing runs in the background; poll GET /api/package/publish/status/:jobId for progress.
 */
async function handlePackagePublish(req, res) {
    try {
        const body = await parseJSON(req);
        
        const {
            packageName,
            version = 'latest',
            npmRegistry = DEFAULT_REGISTRY,
            npmToken,
            npmUsername,
            npmPassword,
            npmEmail
        } = body;

        if (!packageName) {
            res.writeHead(400, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: 'packageName is required' }));
            return;
        }

        const job = createJob(packageName);

        log.section(`API Request: Package Publish [job=${job.id}]`);
        log.info(`Package: ${packageName}@${version}`);
        log.info(`Domain Replacement: DISABLED`);

        res.writeHead(202, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            jobId: job.id,
            statusUrl: `/api/package/publish/status/${job.id}`,
            message: 'Job accepted. Poll statusUrl for progress.',
        }));

        runPackagePublishJob(job, {
            packageName,
            version,
            npmRegistry,
            authOptions: { token: npmToken, username: npmUsername, password: npmPassword, email: npmEmail },
            appDomain: null,
            isRuntimePackage: false,
        });
        
    } catch (error) {
        log.error(`Error: ${error.message}`);
        if (error.stack) {
            log.error(error.stack);
        }
        res.writeHead(500, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            success: false,
            error: error.message
        }));
    }
}

/**
 * Background worker for package publish jobs.
 * Shared by both standard and runtime publish handlers.
 *
 * @param {Job} job
 * @param {Object} opts
 */
async function runPackagePublishJob(job, opts) {
    const { packageName, version, npmRegistry, authOptions, appDomain, isRuntimePackage } = opts;
    try {
        updateJob(job, {
            status: 'processing',
            progress: { current: 0, total: 0, currentFile: '', phase: 'waiting-for-vnext' },
        });

        await waitForVNextApp();

        updateJob(job, {
            progress: { ...job.progress, phase: 'downloading' },
        });

        const packagePath = await downloadPackage(packageName, version, npmRegistry, authOptions);
        await verifyPackageStructure(packagePath);

        updateJob(job, {
            progress: { ...job.progress, phase: 'uploading' },
        });

        const results = await processPackage(packagePath, packageName, appDomain, isRuntimePackage, job);

        updateJob(job, {
            progress: { ...job.progress, phase: 're-initializing' },
        });

        await reInitializeDefinitions();

        let success = true;
        let message = 'Package processed and published successfully';

        if (results.success.length === 0 && results.failed.length > 0) {
            success = false;
            message = 'Package processing failed. No packages were loaded.';
        } else if (results.success.length > 0 && results.failed.length > 0) {
            success = false;
            message = `Package partially processed. ${results.success.length} loaded, ${results.failed.length} failed.`;
        }

        updateJob(job, {
            status: 'completed',
            results: {
                success,
                message,
                packageName,
                appDomain,
                successful: results.success,
                failed: results.failed,
                skipped: results.skipped,
            },
            progress: { ...job.progress, phase: 'done' },
        });

        log.success(`Job ${job.id} completed — ${message}`);
    } catch (error) {
        log.error(`Job ${job.id} failed: ${error.message}`);
        if (error.stack) log.error(error.stack);

        updateJob(job, {
            status: 'failed',
            error: error.message,
            progress: job.progress ? { ...job.progress, phase: 'failed' } : null,
        });
    }
}

/**
 * Verify package has valid structure
 */
async function verifyPackageStructure(packagePath) {
        const vnextConfigPath = path.join(packagePath, 'vnext.config.json');
        const corePath = path.join(packagePath, 'core');
        
        let hasValidStructure = false;
        try {
            await fs.access(vnextConfigPath);
            hasValidStructure = true;
        } catch {
            try {
                await fs.access(corePath);
                hasValidStructure = true;
            } catch {
                // Neither found
            }
        }
        
        if (!hasValidStructure) {
            throw new Error(`Invalid package structure: neither vnext.config.json nor core directory found at ${packagePath}`);
    }
}

/**
 * Trigger re-initialization of definitions to clear cache
 */
async function reInitializeDefinitions() {
    const url = `${VNEXT_APP_URL}/api/v1/definitions/re-initialize`;
    log.detail(`Triggering re-initialization at ${url}...`);
    
    try {
        const response = await httpRequest(url, { method: 'GET' });
        
        if (response.statusCode === 200 || response.statusCode === 204) {
            log.success('Cache re-initialization triggered successfully');
            return true;
        } else {
            log.warn(`Failed to trigger re-initialization (HTTP ${response.statusCode})`);
            return false;
        }
    } catch (e) {
        log.warn(`Error triggering re-initialization: ${e.message}`);
        return false;
    }
}

/**
 * Handle job status polling
 * Endpoint: GET /api/package/publish/status/:jobId
 */
async function handleJobStatus(req, res) {
    const jobId = req.url.replace('/api/package/publish/status/', '');
    const job = jobs.get(jobId);

    if (!job) {
        res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: 'Job not found', jobId }));
        return;
    }

    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({
        jobId: job.id,
        packageName: job.packageName,
        status: job.status,
        progress: job.progress,
        results: job.results,
        error: job.error,
        createdAt: job.createdAt,
        updatedAt: job.updatedAt,
    }));
}

/**
 * Handle API request - Route to appropriate handler
 */
async function handleRequest(req, res) {
    // CORS headers
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'POST, GET, OPTIONS');
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
    
    // Job status polling endpoint
    if (req.method === 'GET' && req.url.startsWith('/api/package/publish/status/')) {
        await handleJobStatus(req, res);
        return;
    }
    
    // Route POST requests
    if (req.method === 'POST') {
        // Runtime package publish endpoint
        if (req.url === '/api/package/runtime/publish') {
            await handleRuntimePublish(req, res);
            return;
        }
        
        // Standard package publish endpoint
        if (req.url === '/api/package/publish') {
            await handlePackagePublish(req, res);
            return;
        }
    }
    
    // Not found
    res.writeHead(404, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
        error: 'Not Found',
        availableEndpoints: [
            'GET /health',
            'POST /api/package/publish',
            'POST /api/package/runtime/publish',
            'GET /api/package/publish/status/:jobId'
        ]
    }));
}

/**
 * Run automatic initialization (like init-vnext-core.sh)
 * 
 * NOTE: This function is NOT called automatically anymore.
 * It's kept for reference but disabled. Packages must be downloaded via API requests.
 * 
 * @deprecated - Use API endpoints instead
 */
async function runAutomaticInit() {
    // This function should NEVER be called automatically
    log.error('runAutomaticInit() should not be called!');
    log.error('Automatic initialization is disabled. Use API endpoint instead.');
    throw new Error('Automatic initialization is disabled. Use the API endpoints instead.');
}

/**
 * Start HTTP server
 */
function startServer() {
    const server = http.createServer(handleRequest);
    
    // Increase timeouts for long-running package publish operations
    // Configurable via env vars: SERVER_TIMEOUT_MS, SERVER_KEEP_ALIVE_TIMEOUT_MS, SERVER_HEADERS_TIMEOUT_MS
    server.timeout = SERVER_TIMEOUT_MS;
    server.keepAliveTimeout = SERVER_KEEP_ALIVE_TIMEOUT_MS;
    server.headersTimeout = SERVER_HEADERS_TIMEOUT_MS;
    
    server.listen(PORT, () => {
        log.section('Package API Server Started');
        log.success(`Server running on port ${PORT}`);
        log.subsection('Available Endpoints');
        log.info(`Health Check: GET http://localhost:${PORT}/health`);
        log.info(`Runtime Publish: POST http://localhost:${PORT}/api/package/runtime/publish`);
        log.detail(`  - For ${VNEXT_CORE_RUNTIME_PACKAGE}`);
        log.detail(`  - appDomain is REQUIRED`);
        log.detail(`  - Special ordering (Workflows first, sys-flows first)`);
        log.info(`Package Publish: POST http://localhost:${PORT}/api/package/publish`);
        log.detail(`  - For any npm package`);
        log.detail(`  - appDomain is OPTIONAL`);
        log.detail(`  - If appDomain provided, replaces all domains`);
        log.info(`Job Status: GET http://localhost:${PORT}/api/package/publish/status/:jobId`);
        log.detail(`  - Poll for job progress after publish`);
        log.info(`VNext App URL: ${VNEXT_APP_URL}`);
        log.subsection('Timeout Configuration');
        log.info(`Server Timeout: ${SERVER_TIMEOUT_MS}ms (${SERVER_TIMEOUT_MS / 60000} min)`);
        log.info(`Keep-Alive Timeout: ${SERVER_KEEP_ALIVE_TIMEOUT_MS}ms (${SERVER_KEEP_ALIVE_TIMEOUT_MS / 60000} min)`);
        log.info(`Headers Timeout: ${SERVER_HEADERS_TIMEOUT_MS}ms`);
        log.detail(`  - Override via: SERVER_TIMEOUT_MS, SERVER_KEEP_ALIVE_TIMEOUT_MS, SERVER_HEADERS_TIMEOUT_MS`);
    });
    
    server.on('error', (err) => {
        log.error(`Server error: ${err.message}`);
        process.exit(1);
    });

    setInterval(cleanupExpiredJobs, 5 * 60 * 1000);
}

// Main execution logic
if (require.main === module) {
    // Always start API server - NO automatic initialization, NO automatic downloads
    log.section('VNext Package API Server');
    log.info('Automatic initialization: DISABLED');
    log.info('Automatic package downloads: DISABLED');
    log.info('Packages will ONLY be downloaded via API requests');
    startServer();
}

module.exports = { startServer, processPackage, downloadPackage, runAutomaticInit };