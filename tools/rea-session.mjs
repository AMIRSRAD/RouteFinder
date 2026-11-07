#!/usr/bin/env node
// A local MCP client. Uses the public npx launcher and public MCP client exports.
// Does not register servers, change Codex settings, or execute the inspected binary.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const analysis = path.join(workspace, 'ezconnect-research', 'analysis');
const argv = process.argv.slice(2);
const option = flag => { const index = argv.indexOf(flag); return index < 0 ? undefined : argv[index + 1]; };
if (argv.includes('--help')) {
  console.log('Usage: node tools/rea-session.mjs [--calls calls.json | --json JSON_ARRAY | JSON_ARRAY] [--schemas] [--prefix name]');
  console.log('Without a batch argument, reads one call object per stdin line and retains one native session. EOF/$exit closes the native target.');
  console.log('Call shape: {"name":"open_binary","arguments":{"path":"...","provider_id":"ghidra"},"output":"optional-name","timeoutMs":900000}');
  console.log('Environment: GHIDRA_INSTALL_DIR/JAVA_HOME inherited; REA_NPM_NODE_MODULES optionally points to the existing npm-cache node_modules directory.');
  process.exit(0);
}
await fs.mkdir(analysis, { recursive: true });
const prefix = option('--prefix') ?? `rea-${new Date().toISOString().replace(/[:.]/g, '-')}-${process.pid}`;
if (!/^[a-zA-Z0-9_-]+$/.test(prefix)) throw new Error('Prefix must contain only letters, numbers, underscore, or hyphen.');

async function findDependencies() {
  if (process.env.REA_NPM_NODE_MODULES) return path.resolve(process.env.REA_NPM_NODE_MODULES);
  const base = path.join(process.env.LOCALAPPDATA ?? '', 'npm-cache', '_npx');
  for (const entry of await fs.readdir(base, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const modules = path.join(base, entry.name, 'node_modules');
    try {
      const pkg = JSON.parse(await fs.readFile(path.join(modules, 'rea-agents', 'package.json'), 'utf8'));
      if (pkg.version === '6.4.0') return modules;
    } catch { }
  }
  throw new Error('REA 6.4.0 npm cache not found. Run npx -y rea-agents@6.4.0 --version first.');
}
const modules = await findDependencies();
const requireFromCache = createRequire(path.join(modules, 'rea-agents', 'package.json'));
// REA 6.4.0 carries SDK v2 under @modelcontextprotocol/client, rather than the older /sdk name.
const { Client } = requireFromCache('@modelcontextprotocol/client');
const { StdioClientTransport } = requireFromCache('@modelcontextprotocol/client/stdio');
const npmCli = path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npx-cli.js');
await fs.access(npmCli);
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [npmCli, '-y', 'rea-agents@6.4.0', 'mcp'],
  env: Object.fromEntries(Object.entries(process.env).filter(([, v]) => v !== undefined)),
  cwd: workspace,
  stderr: 'pipe',
  maxBufferSize: 64 * 1024 * 1024
});
const stderrPath = path.join(analysis, `${prefix}-server.log`);
transport.stderr?.on('data', bytes => { fs.appendFile(stderrPath, bytes).catch(() => {}); });
const client = new Client({ name: 'routefinder-local-rea', version: '1.0.0' }, { capabilities: {} });
let opened = false, sequence = 0, closed = false;
const validators = new Map();
let toolMap;
let ajv;
try {
  const AjvModule = requireFromCache('ajv/dist/2020');
  const Ajv = AjvModule.default ?? AjvModule;
  ajv = new Ajv({ strict: false, allErrors: true, validateFormats: false });
} catch { /* Server validation remains authoritative when Ajv is unavailable. */ }

function jsonFromResult(result) {
  if (result.structuredContent !== undefined) return result.structuredContent;
  for (const block of result.content ?? []) {
    if (block.type !== 'text') continue;
    try { return JSON.parse(block.text); } catch { }
  }
  return result;
}
function brief(value) {
  const evidence = value?.evidence ?? value;
  return { evidenceId: evidence?.evidence_id ?? evidence?.id ?? value?.evidence_id, keys: value && typeof value === 'object' ? Object.keys(value).slice(0, 12) : [], status: value?.status ?? value?.result?.status, error: value?.error?.code ?? value?.error?.message };
}
async function call(item, cleanup = false) {
  if (!item || typeof item !== 'object' || Array.isArray(item)) throw new Error('Each call must be an object.');
  const name = item.name;
  if (name === '$exit') return false;
  const schema = toolMap.get(name);
  if (!schema) throw new Error(`Unknown advertised tool: ${name}`);
  const args = item.arguments ?? item.args ?? {};
  if (ajv) {
    let validate = validators.get(name);
    if (!validate) { validate = ajv.compile(schema.inputSchema); validators.set(name, validate); }
    if (!validate(args)) throw new Error(`Arguments do not match ${name}: ${ajv.errorsText(validate.errors)}`);
  }
  const number = String(++sequence).padStart(4, '0');
  const label = item.output ?? `${prefix}-${number}-${name}`;
  if (!/^[a-zA-Z0-9_-]+$/.test(label)) throw new Error('Output basename must contain only letters, numbers, underscore, or hyphen.');
  const file = path.join(analysis, label + '.json');
  const rawFile = path.join(analysis, label + '.mcp.json');
  const requestFile = path.join(analysis, label + '.request.json');
  await fs.writeFile(requestFile, JSON.stringify({ name, arguments: args, cleanup }, null, 2), { flag: 'wx' });
  const start = Date.now();
  const result = await client.callTool({ name, arguments: args }, { timeout: item.timeoutMs ?? 900000, resetTimeoutOnProgress: true, maxTotalTimeout: item.timeoutMs ?? 900000 });
  const value = jsonFromResult(result);
  await fs.writeFile(rawFile, JSON.stringify(result, null, 2), { flag: 'wx' });
  await fs.writeFile(file, JSON.stringify(value, null, 2), { flag: 'wx' });
  if (name === 'open_binary' && !result.isError) opened = true;
  if (name === 'close_binary' && !result.isError) opened = false;
  console.log(JSON.stringify({ name, ok: !result.isError, elapsedMs: Date.now() - start, file, ...brief(value) }));
  if (result.isError && !cleanup) throw new Error(`${name} returned a tool error; inspect ${file}`);
  return true;
}

async function finish() {
  if (closed) return;
  closed = true;
  try { if (opened) await call({ name: 'close_binary', arguments: {}, timeoutMs: 120000 }, true); }
  finally { await client.close(); }
}
process.once('SIGINT', () => { finish().finally(() => process.exit(130)); });
process.once('SIGTERM', () => { finish().finally(() => process.exit(143)); });

try {
  await client.connect(transport);
  const listed = await client.listTools();
  toolMap = new Map(listed.tools.map(tool => [tool.name, tool]));
  const schemaPath = path.join(analysis, `${prefix}-tools.json`);
  await fs.writeFile(schemaPath, JSON.stringify(listed, null, 2), { flag: 'wx' });
  const wanted = ['open_binary', 'binary_session', 'search_strings', 'search_procedures', 'analyze_function', 'get_xrefs', 'xrefs', 'close_binary'];
  const selected = listed.tools.filter(tool => wanted.includes(tool.name));
  const focusedPath = path.join(analysis, `${prefix}-schemas.json`);
  await fs.writeFile(focusedPath, JSON.stringify(selected, null, 2), { flag: 'wx' });
  console.log(JSON.stringify({ ready: true, tools: listed.tools.length, schemas: focusedPath, allSchemas: schemaPath, selected: selected.map(tool => tool.name) }));
  if (!argv.includes('--schemas')) {
    let batch;
    if (option('--calls')) batch = JSON.parse(await fs.readFile(path.resolve(option('--calls')), 'utf8'));
    else if (option('--json')) batch = JSON.parse(option('--json'));
    else if (argv[0]?.trim().startsWith('[')) batch = JSON.parse(argv[0]);
    if (batch !== undefined) {
      if (!Array.isArray(batch)) throw new Error('Batch must be a JSON array of calls.');
      for (const item of batch) if (!(await call(item))) break;
    } else {
      const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
      for await (const line of input) {
        if (!line.trim()) continue;
        try { if (!(await call(JSON.parse(line)))) break; }
        catch (error) { console.log(JSON.stringify({ ok: false, error: error.message })); }
      }
    }
  }
} catch (error) {
  console.error(JSON.stringify({ ok: false, error: error.message }));
  process.exitCode = 1;
} finally { await finish(); }
