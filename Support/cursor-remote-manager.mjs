#!/usr/bin/env node

import { spawn } from "node:child_process";
import { createHash, randomUUID, timingSafeEqual } from "node:crypto";
import { constants as fsConstants } from "node:fs";
import {
  access,
  chmod,
  lstat,
  mkdir,
  open,
  readFile,
  realpath,
  rename,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import http from "node:http";
import https from "node:https";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const RUNTIME_SCHEMA_VERSION = 2;
export const PROVISION_SCHEMA_VERSION = 2;
export const MAX_CATALOG_BYTES = 2 * 1024 * 1024;
export const MAX_PROVISION_BYTES = 4 * 1024 * 1024;
export const PROVIDER_ID = "syncbar_cursor_bridge";
export const MARKER_BEGIN = "# BEGIN CODEX SYNCBAR CURSOR REMOTE v1";
export const MARKER_END = "# END CODEX SYNCBAR CURSOR REMOTE v1";

const MODEL_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$/;
const MODEL_PARAMETER_EFFORTS = new Set([
  "none", "minimal", "low", "medium", "high", "xhigh", "max",
]);
const MODEL_ROUTE_EFFORTS = new Set([
  "default", ...MODEL_PARAMETER_EFFORTS,
]);
const BRIDGE_TOKEN_PATTERN = /^[a-f0-9]{64}$/;
const MAX_RUNTIME_BYTES = MAX_PROVISION_BYTES;
const MAX_JOURNAL_BYTES = 16 * 1024 * 1024;
const MAX_INSTALLER_BYTES = 2 * 1024 * 1024;
const MAX_CHILD_OUTPUT_BYTES = 512 * 1024;
const DEFAULT_INSTALL_URL = "https://cursor.com/install";
const DEFAULT_HEALTH_TIMEOUT_MS = 750;
const DEFAULT_START_TIMEOUT_MS = 8_000;
const DEFAULT_LOCK_TIMEOUT_SECONDS = 5;
// This is an implementation switch in the current Cursor Agent CLI, not a
// documented compatibility guarantee. Keep status/model validation fail-closed
// so a future CLI change cannot silently bypass the dedicated file store.
const CURSOR_FILE_CREDENTIAL_STORE = "file";
const thisFile = fileURLToPath(import.meta.url);

export class RemoteManagerError extends Error {
  constructor(message, code = "remote_manager_error") {
    super(message);
    this.name = "RemoteManagerError";
    this.code = code;
  }
}

function fail(message, code) {
  throw new RemoteManagerError(message, code);
}

function sha256(data) {
  return createHash("sha256").update(data).digest("hex");
}

function sleep(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function validatedHome(value) {
  if (typeof value !== "string" || !path.isAbsolute(value) || value.includes("\0")) {
    fail("HOME must be an absolute path", "invalid_home");
  }
  return path.resolve(value);
}

function validatedAbsolutePath(value, field) {
  if (typeof value !== "string" || !path.isAbsolute(value) || /[\0\r\n]/.test(value)) {
    fail(`${field} must be an absolute path`, "invalid_path");
  }
  return path.resolve(value);
}

function validatedAPIKey(value) {
  const encoded = typeof value === "string" ? Buffer.from(value, "utf8") : Buffer.alloc(0);
  const bytes = encoded.length;
  if (
    typeof value !== "string" ||
    bytes < 16 ||
    bytes > 1024 ||
    encoded.toString("utf8") !== value ||
    /[\p{White_Space}\p{Cc}\p{Cf}]/u.test(value)
  ) {
    fail("apiKey is invalid", "invalid_secret");
  }
  return value;
}

function validatedBridgeToken(value) {
  if (typeof value !== "string" || !BRIDGE_TOKEN_PATTERN.test(value)) {
    fail("bridgeToken is invalid", "invalid_secret");
  }
  return value;
}

function validatedModel(value) {
  if (typeof value !== "string" || !MODEL_PATTERN.test(value)) {
    fail("model is invalid", "invalid_model");
  }
  return value;
}

function validatedModels(value, model) {
  if (!Array.isArray(value) || value.length === 0 || value.length > 512) {
    fail("models must be a non-empty bounded array", "invalid_models");
  }
  const result = [];
  const seen = new Set();
  for (const candidate of value) {
    const slug = validatedModel(candidate);
    if (seen.has(slug)) fail("models contains a duplicate slug", "invalid_models");
    seen.add(slug);
    result.push(slug);
  }
  if (!seen.has(model)) fail("models does not contain model", "invalid_models");
  return result;
}

function validatedModelParameters(value, models) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    fail("modelParameters must be a JSON object", "invalid_model_parameters");
  }
  const expectedSlugs = [...models].sort();
  const actualSlugs = Object.keys(value).sort();
  if (actualSlugs.length !== expectedSlugs.length ||
      actualSlugs.some((slug, index) => slug !== expectedSlugs[index])) {
    fail("modelParameters keys must exactly match models", "invalid_model_parameters");
  }

  const result = {};
  for (const slug of models) {
    const parameters = value[slug];
    if (!parameters || typeof parameters !== "object" || Array.isArray(parameters)) {
      fail("modelParameters entry must be a JSON object", "invalid_model_parameters");
    }
    const keys = Object.keys(parameters).sort();
    const allowedKeys = new Set(["context", "effort", "fast", "model", "thinking"]);
    if (!Object.hasOwn(parameters, "model") || !Object.hasOwn(parameters, "fast") ||
        !Object.hasOwn(parameters, "thinking") || keys.some((key) => !allowedKeys.has(key))) {
      fail("modelParameters entry contains missing or unknown fields", "invalid_model_parameters");
    }
    if (typeof parameters.fast !== "boolean" || typeof parameters.thinking !== "boolean") {
      fail("modelParameters flags must be booleans", "invalid_model_parameters");
    }
    if (Object.hasOwn(parameters, "context") && parameters.context !== "1m") {
      fail("modelParameters context is invalid", "invalid_model_parameters");
    }
    if (Object.hasOwn(parameters, "effort") &&
        (typeof parameters.effort !== "string" ||
          !MODEL_PARAMETER_EFFORTS.has(parameters.effort))) {
      fail("modelParameters effort is invalid", "invalid_model_parameters");
    }
    const normalized = {
      model: validatedModel(parameters.model),
      fast: parameters.fast,
      thinking: parameters.thinking,
    };
    if (Object.hasOwn(parameters, "context")) normalized.context = parameters.context;
    if (Object.hasOwn(parameters, "effort")) normalized.effort = parameters.effort;
    result[slug] = normalized;
  }
  return result;
}

function validatedModelRoutesJSON(value, models, codexModel) {
  if (typeof value !== "string" || Buffer.byteLength(value, "utf8") > 512 * 1024) {
    fail("modelRoutesJSON is invalid", "invalid_model_routes");
  }
  let routes;
  try { routes = JSON.parse(value); } catch {
    fail("modelRoutesJSON is invalid", "invalid_model_routes");
  }
  if (!routes || typeof routes !== "object" || Array.isArray(routes) ||
      Object.keys(routes).length === 0 || Object.keys(routes).length > 512 ||
      !Object.hasOwn(routes, codexModel)) {
    fail("modelRoutesJSON is invalid", "invalid_model_routes");
  }
  const allowed = new Set(models);
  const normalized = {};
  for (const [pickerModel, route] of Object.entries(routes)) {
    validatedModel(pickerModel);
    if (allowed.has(pickerModel) || !route || typeof route !== "object" || Array.isArray(route) ||
        !exactObjectKeys(route, ["default_effort", "variants"]) ||
        typeof route.default_effort !== "string" ||
        !MODEL_ROUTE_EFFORTS.has(route.default_effort) ||
        !route.variants || typeof route.variants !== "object" || Array.isArray(route.variants)) {
      fail("modelRoutesJSON is invalid", "invalid_model_routes");
    }
    if (!Object.hasOwn(route.variants, route.default_effort)) {
      fail("modelRoutesJSON is missing its default variant", "invalid_model_routes");
    }
    const variants = {};
    for (const [effort, tiers] of Object.entries(route.variants)) {
      if (!MODEL_ROUTE_EFFORTS.has(effort) || !tiers || typeof tiers !== "object" ||
          Array.isArray(tiers) || Object.keys(tiers).length === 0 ||
          typeof tiers.standard !== "string" ||
          Object.keys(tiers).some((tier) => !["standard", "fast"].includes(tier))) {
        fail("modelRoutesJSON is invalid", "invalid_model_routes");
      }
      variants[effort] = {};
      for (const [tier, slug] of Object.entries(tiers)) {
        if (typeof slug !== "string" || !allowed.has(slug)) {
          fail("modelRoutesJSON references an unavailable model", "invalid_model_routes");
        }
        variants[effort][tier] = slug;
      }
    }
    normalized[pickerModel] = { default_effort: route.default_effort, variants };
  }
  return normalized;
}

function validatedNativeModels(value, models, modelRoutes) {
  if (!Array.isArray(value) || value.length > 512) {
    fail("nativeModels is invalid", "invalid_native_models");
  }
  // Native Codex ids may also be exact Cursor CLI slugs. The bridge checks
  // native models first and keeps the Cursor choice under syncbar-cursor/*.
  const reserved = new Set(Object.keys(modelRoutes));
  const seen = new Set();
  return value.map((candidate) => {
    const slug = validatedModel(candidate);
    if (reserved.has(slug) || !seen.add(slug)) {
      fail("nativeModels is invalid", "invalid_native_models");
    }
    return slug;
  });
}

function decodedCatalog(value, codexModel, modelRoutes, nativeModels) {
  if (typeof value !== "string" || value.length === 0) {
    fail("catalogData is invalid", "invalid_catalog");
  }
  const data = Buffer.from(value, "base64");
  if (data.length === 0 || data.length > MAX_CATALOG_BYTES || data.toString("base64") !== value) {
    fail("catalogData is invalid", "invalid_catalog");
  }
  let catalog;
  try { catalog = JSON.parse(data.toString("utf8")); } catch {
    fail("catalogData is invalid", "invalid_catalog");
  }
  if (!catalog || typeof catalog !== "object" || Array.isArray(catalog) || !Array.isArray(catalog.models)) {
    fail("catalogData is invalid", "invalid_catalog");
  }
  const slugs = new Set();
  for (const entry of catalog.models) {
    if (!entry || typeof entry !== "object" || Array.isArray(entry) ||
        typeof entry.slug !== "string" || !MODEL_PATTERN.test(entry.slug) || slugs.has(entry.slug)) {
      fail("catalogData model ids are invalid", "invalid_catalog");
    }
    slugs.add(entry.slug);
  }
  for (const slug of [codexModel, ...Object.keys(modelRoutes), ...nativeModels]) {
    if (!slugs.has(slug)) fail("catalogData does not match model routing", "invalid_catalog");
  }
  return data;
}

function migratedModelParameters(models) {
  const aliases = new Map([
    ["auto", "default"],
    ["cursor-grok-4.6", "grok-4.6"],
    ["cursor-grok-4.5", "grok-4.5"],
    ["claude-4.6-sonnet", "claude-sonnet-4-6"],
    ["claude-4.6-opus", "claude-opus-4-6"],
    ["claude-4.5-opus", "claude-opus-4-5"],
    ["claude-4.5-sonnet", "claude-sonnet-4-5"],
    ["claude-4-sonnet", "claude-sonnet-4"],
  ]);
  const effortSuffixes = [
    ["-extra-high", "xhigh"],
    ["-minimal", "minimal"],
    ["-default", null],
    ["-medium", "medium"],
    ["-xhigh", "xhigh"],
    ["-none", "none"],
    ["-high", "high"],
    ["-low", "low"],
    ["-max", "max"],
  ];
  const result = {};
  for (const slug of models) {
    if (slug === "auto") {
      result[slug] = { model: "default", fast: false, thinking: false };
      continue;
    }
    let model = slug;
    let fast = false;
    let thinking = false;
    let effort;
    let changed = true;
    while (changed) {
      changed = false;
      if (!fast && model.endsWith("-fast")) {
        model = model.slice(0, -5);
        fast = true;
        changed = true;
      }
      if (!thinking && model.endsWith("-thinking")) {
        model = model.slice(0, -9);
        thinking = true;
        changed = true;
      }
      if (effort === undefined) {
        for (const [suffix, candidate] of effortSuffixes) {
          if (model.endsWith(suffix)) {
            model = model.slice(0, -suffix.length);
            effort = candidate;
            changed = true;
            break;
          }
        }
      }
    }
    const parameters = { model: aliases.get(model) ?? model, fast, thinking };
    if (effort) parameters.effort = effort;
    // Old runtimes did not retain the catalog's explicit context token. Do
    // not infer a 1m setting while migrating them in memory.
    result[slug] = parameters;
  }
  return result;
}

export function validateProvisionInput(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    fail("provision input must be a JSON object", "invalid_input");
  }
  if (value.schemaVersion !== PROVISION_SCHEMA_VERSION) {
    fail("unsupported provision schemaVersion", "invalid_schema");
  }
  const expectedKeys = [
    "apiKey", "bridgeToken", "catalogData", "codexModel", "model", "modelParameters",
    "modelRoutesJSON", "models", "nativeModels", "port", "schemaVersion",
  ];
  const actualKeys = Object.keys(value).sort();
  if (actualKeys.length !== expectedKeys.length ||
      actualKeys.some((key, index) => key !== expectedKeys[index])) {
    fail("provision input contains missing or unknown fields", "invalid_input");
  }
  const model = validatedModel(value.model);
  const port = Number(value.port);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    fail("port must be between 1024 and 65535", "invalid_port");
  }
  const models = validatedModels(value.models, model);
  const codexModel = validatedModel(value.codexModel);
  const modelRoutes = validatedModelRoutesJSON(value.modelRoutesJSON, models, codexModel);
  const nativeModels = validatedNativeModels(value.nativeModels, models, modelRoutes);
  return {
    schemaVersion: RUNTIME_SCHEMA_VERSION,
    apiKey: validatedAPIKey(value.apiKey),
    model,
    port,
    bridgeToken: validatedBridgeToken(value.bridgeToken),
    models,
    modelParameters: validatedModelParameters(value.modelParameters, models),
    codexModel,
    modelRoutes,
    nativeModels,
    catalogData: decodedCatalog(value.catalogData, codexModel, modelRoutes, nativeModels),
  };
}

export function managerPaths(options = {}) {
  const environment = options.env ?? process.env;
  const home = validatedHome(options.home ?? environment.HOME ?? os.homedir());
  const stateRoot = path.join(home, ".local", "share", "gpt-switch");
  const xdgRoot = path.join(stateRoot, "cursor-remote-xdg");
  return {
    home,
    stateRoot,
    runtime: path.join(stateRoot, "cursor-remote-runtime.json"),
    backup: path.join(stateRoot, "cursor-remote-config-backup.json"),
    catalog: path.join(stateRoot, "cursor-codex-model-catalog.json"),
    journal: path.join(stateRoot, "cursor-remote-transaction.json"),
    lock: path.join(stateRoot, ".cursor-remote-manager.lock"),
    configDirectory: path.join(home, ".codex"),
    config: path.join(home, ".codex", "config.toml"),
    workspace: path.join(stateRoot, "cursor-remote-workspace"),
    xdgRoot,
    cursorHome: path.join(xdgRoot, "home"),
    xdgConfig: path.join(xdgRoot, "config"),
    xdgData: path.join(xdgRoot, "data"),
    xdgCache: path.join(xdgRoot, "cache"),
    xdgState: path.join(xdgRoot, "state"),
    defaultAgent: path.join(home, ".local", "bin", "agent"),
    defaultBridge: path.join(home, ".local", "lib", "gpt-switch", "cursor-codex-bridge.mjs"),
  };
}

function ownedByCurrentUser(info) {
  return typeof process.getuid !== "function" || info.uid === process.getuid();
}

async function requireSafeDirectory(directory) {
  const info = await lstat(directory);
  if (!info.isDirectory() || info.isSymbolicLink() || !ownedByCurrentUser(info) || (info.mode & 0o022) !== 0) {
    fail("A manager directory is unsafe", "unsafe_path");
  }
}

async function ensureDirectory(directory, parent = null) {
  if (parent && path.dirname(directory) !== parent) fail("Invalid directory layout", "unsafe_path");
  try {
    await mkdir(directory, { mode: 0o700 });
  } catch (error) {
    if (error?.code !== "EEXIST") throw error;
  }
  await requireSafeDirectory(directory);
}

async function ensureManagerDirectories(paths) {
  await requireSafeDirectory(paths.home);
  const local = path.join(paths.home, ".local");
  const share = path.join(local, "share");
  await ensureDirectory(local, paths.home);
  await ensureDirectory(share, local);
  await ensureDirectory(paths.stateRoot, share);
  await ensureDirectory(paths.configDirectory, paths.home);
}

async function ensureCursorXDGDirectories(paths) {
  await ensureDirectory(paths.xdgRoot, paths.stateRoot);
  for (const directory of [
    paths.cursorHome,
    paths.xdgConfig,
    paths.xdgData,
    paths.xdgCache,
    paths.xdgState,
  ]) {
    await ensureDirectory(directory, paths.xdgRoot);
  }
}

async function safeSnapshot(file, options = {}) {
  let info;
  try {
    info = await lstat(file);
  } catch (error) {
    if (error?.code === "ENOENT") return { exists: false, data: Buffer.alloc(0), hash: sha256(Buffer.alloc(0)), mode: 0o600 };
    throw error;
  }
  if (!info.isFile() || info.isSymbolicLink() || !ownedByCurrentUser(info)) {
    fail("A manager file is unsafe", "unsafe_path");
  }
  const mode = info.mode & 0o777;
  if ((mode & 0o022) !== 0 || (options.privateFile && (mode & 0o077) !== 0)) {
    fail("A manager file has unsafe permissions", "unsafe_permissions");
  }
  const data = await readFile(file);
  if (options.maxBytes && data.length > options.maxBytes) fail("A manager file is too large", "file_too_large");
  return { exists: true, data, hash: sha256(data), mode };
}

function sameSnapshot(first, second) {
  if (first.exists !== second.exists) return false;
  if (!first.exists) return true;
  if (first.mode !== second.mode) return false;
  const a = Buffer.from(first.hash, "hex");
  const b = Buffer.from(second.hash, "hex");
  return a.length === b.length && timingSafeEqual(a, b);
}

function bridgeProcessIdentity(runtime) {
  return JSON.stringify({
    apiKey: runtime.apiKey,
    bridgeToken: runtime.bridgeToken,
    port: runtime.port,
    model: runtime.model,
    models: runtime.models,
    modelParameters: runtime.modelParameters,
    modelRoutes: runtime.modelRoutes ?? {},
    nativeModels: runtime.nativeModels ?? [],
    home: runtime.home,
    cursorHome: runtime.cursorHome,
    agentPath: runtime.agentPath,
    bridgePath: runtime.bridgePath,
    nodePath: runtime.nodePath,
    workspace: runtime.workspace,
    xdgConfig: runtime.xdgConfig,
    xdgData: runtime.xdgData,
    xdgCache: runtime.xdgCache,
    xdgState: runtime.xdgState,
  });
}

export function sameBridgeProcessIdentity(left, right) {
  if (!left || !right) return false;
  const a = Buffer.from(sha256(Buffer.from(bridgeProcessIdentity(left))), "hex");
  const b = Buffer.from(sha256(Buffer.from(bridgeProcessIdentity(right))), "hex");
  return a.length === b.length && timingSafeEqual(a, b);
}

async function syncDirectory(directory) {
  const handle = await open(directory, "r");
  try {
    await handle.sync();
  } finally {
    await handle.close();
  }
}

async function atomicCompareAndSwap(file, expected, candidate) {
  const parent = path.dirname(file);
  await requireSafeDirectory(parent);
  const live = await safeSnapshot(file, { privateFile: false });
  if (!sameSnapshot(live, expected)) fail("A managed file changed concurrently", "cas_mismatch");

  if (!candidate.exists) {
    if (!expected.exists) return;
    const tombstone = path.join(parent, `.${path.basename(file)}.${process.pid}.${randomUUID()}.remove`);
    await rename(file, tombstone);
    await syncDirectory(parent);
    await rm(tombstone, { force: true });
    await syncDirectory(parent);
    return;
  }

  const temporary = path.join(parent, `.${path.basename(file)}.${process.pid}.${randomUUID()}.tmp`);
  let handle;
  try {
    handle = await open(temporary, "wx", candidate.mode ?? 0o600);
    await handle.writeFile(candidate.data);
    await chmod(temporary, candidate.mode ?? 0o600);
    await handle.sync();
    await handle.close();
    handle = null;
    const beforeRename = await safeSnapshot(file, { privateFile: false });
    if (!sameSnapshot(beforeRename, expected)) fail("A managed file changed concurrently", "cas_mismatch");
    await rename(temporary, file);
    await syncDirectory(parent);
  } finally {
    await handle?.close().catch(() => {});
    await rm(temporary, { force: true });
  }
  const installed = await safeSnapshot(file, {
    privateFile: ((candidate.mode ?? 0o600) & 0o077) === 0,
  });
  if (installed.hash !== sha256(candidate.data) || installed.mode !== (candidate.mode ?? 0o600)) {
    fail("A managed file failed post-write verification", "write_verification_failed");
  }
}

async function advisoryLockUtility() {
  const candidates = process.platform === "darwin"
    ? [{ path: "/usr/bin/lockf", kind: "lockf" }]
    : [
        { path: "/usr/bin/flock", kind: "flock" },
        { path: "/bin/flock", kind: "flock" },
      ];
  for (const candidate of candidates) {
    try {
      await access(candidate.path, fsConstants.X_OK);
      const info = await stat(candidate.path);
      if (info.isFile() && (info.mode & 0o022) === 0) return candidate;
    } catch {}
  }
  fail("lockf or flock is required for remote manager locking", "missing_lock_utility");
}

async function ensureAdvisoryLockFile(lockPath) {
  try {
    await writeFile(lockPath, "", { flag: "wx", mode: 0o600 });
  } catch (error) {
    if (error?.code !== "EEXIST") throw error;
  }
  const info = await lstat(lockPath);
  if (!info.isFile() || info.isSymbolicLink() || !ownedByCurrentUser(info) ||
      (info.mode & 0o777) !== 0o600) {
    fail("The manager lock is unsafe", "unsafe_lock");
  }
}

async function acquireAdvisoryLock(paths) {
  await ensureAdvisoryLockFile(paths.lock);
  const utility = await advisoryLockUtility();
  const holderScript = [
    'process.stdout.write("locked\\n")',
    "process.stdin.resume()",
    "process.stdin.on('end', () => process.exit(0))",
  ].join(";");
  const timeout = String(DEFAULT_LOCK_TIMEOUT_SECONDS);
  const args = utility.kind === "lockf"
    ? ["-s", "-t", timeout, paths.lock, process.execPath, "-e", holderScript]
    : ["-x", "-w", timeout, paths.lock, process.execPath, "-e", holderScript];
  const child = spawn(utility.path, args, { stdio: ["pipe", "pipe", "pipe"] });
  child.stdin.on("error", () => {});

  return new Promise((resolve, reject) => {
    let output = "";
    let ready = false;
    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new RemoteManagerError("Another remote manager operation is running", "lock_timeout"));
    }, (DEFAULT_LOCK_TIMEOUT_SECONDS + 2) * 1_000);
    const rejectBeforeReady = () => {
      if (ready) return;
      clearTimeout(timer);
      reject(new RemoteManagerError("Another remote manager operation is running", "lock_timeout"));
    };
    child.once("error", rejectBeforeReady);
    child.once("close", rejectBeforeReady);
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      if (ready) return;
      output += chunk;
      if (output.length > 64 || !"locked\n".startsWith(output)) {
        clearTimeout(timer);
        child.kill("SIGKILL");
        reject(new RemoteManagerError("The manager lock helper failed", "unsafe_lock"));
        return;
      }
      if (output === "locked\n") {
        ready = true;
        clearTimeout(timer);
        resolve(child);
      }
    });
  });
}

async function releaseAdvisoryLock(child) {
  if (child.exitCode !== null || child.signalCode !== null) return;
  const closed = new Promise((resolve) => child.once("close", resolve));
  child.stdin.end();
  const timeout = new Promise((resolve) => setTimeout(resolve, 2_000, "timeout"));
  if (await Promise.race([closed, timeout]) === "timeout") child.kill("SIGKILL");
}

async function withManagerLock(paths, operation, options = {}) {
  await ensureManagerDirectories(paths);
  const holder = await acquireAdvisoryLock(paths);
  try {
    await recoverPendingTransaction(paths, options);
    return await operation();
  } finally {
    await releaseAdvisoryLock(holder);
  }
}

function splitLines(text) {
  if (text.length === 0) return [];
  const lines = [];
  let offset = 0;
  while (offset < text.length) {
    const newline = text.indexOf("\n", offset);
    if (newline < 0) {
      lines.push({ content: text.slice(offset), ending: "" });
      break;
    }
    const hasCR = newline > offset && text[newline - 1] === "\r";
    lines.push({
      content: text.slice(offset, hasCR ? newline - 1 : newline),
      ending: hasCR ? "\r\n" : "\n",
    });
    offset = newline + 1;
  }
  return lines;
}

function preferredNewline(text) {
  return text.includes("\r\n") ? "\r\n" : "\n";
}

function tomlString(value) {
  if (typeof value !== "string" || /[\0\r\n]/.test(value)) fail("Unsafe TOML value", "invalid_toml_value");
  return `"${value.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`;
}

function rejectProviderCollision(text) {
  if (text.includes(MARKER_BEGIN) || text.includes(MARKER_END)) {
    fail("The managed Cursor provider marker already exists", "managed_marker_exists");
  }
  for (const { content } of splitLines(text)) {
    const trimmed = content.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;
    const syntax = trimmed.split("#", 1)[0].replace(/[\s"']/g, "");
    if (
      syntax === `[model_providers.${PROVIDER_ID}]` ||
      syntax === `[[model_providers.${PROVIDER_ID}]]` ||
      syntax.startsWith(`model_providers.${PROVIDER_ID}.`) ||
      syntax.startsWith(`model_providers.${PROVIDER_ID}=`)
    ) {
      fail("The Cursor provider id is already configured", "provider_collision");
    }
  }
}

export function patchCodexConfig(originalText, runtime, options = {}) {
  if (typeof originalText !== "string") fail("config.toml is not UTF-8 text", "invalid_config");
  rejectProviderCollision(originalText);
  const newline = preferredNewline(originalText);
  const lines = splitLines(originalText);
  let firstTable = lines.length;
  const found = new Map();
  for (let index = 0; index < lines.length; index += 1) {
    const trimmed = lines[index].content.trim();
    if (trimmed.startsWith("[")) {
      firstTable = index;
      break;
    }
    if (!trimmed || trimmed.startsWith("#")) continue;
    if (trimmed.includes('"""') || trimmed.includes("'''")) {
      fail("Top-level multiline TOML cannot be patched safely", "unsupported_config");
    }
    if (/^["'](?:model|model_provider|model_catalog_json)["']\s*=/.test(trimmed)) {
      fail("Quoted top-level model keys cannot be patched safely", "unsupported_config");
    }
    const match = /^(model|model_provider|model_catalog_json)\s*=/.exec(trimmed);
    if (!match) continue;
    if (found.has(match[1])) fail("Duplicate top-level model configuration", "unsupported_config");
    found.set(match[1], index);
  }

  const assignments = {
    model: `model = ${tomlString(runtime.codexModel ?? runtime.model)}`,
    model_provider: `model_provider = ${tomlString(PROVIDER_ID)}`,
    model_catalog_json: runtime.catalogPath
      ? `model_catalog_json = ${tomlString(runtime.catalogPath)}`
      : null,
  };
  const missing = [];
  const managedKeys = runtime.catalogPath
    ? ["model", "model_provider", "model_catalog_json"]
    : ["model", "model_provider"];
  for (const key of managedKeys) {
    const index = found.get(key);
    if (index === undefined) missing.push({ content: assignments[key], ending: newline });
    else lines[index].content = assignments[key];
  }
  if (missing.length > 0) {
    if (firstTable === lines.length && lines.length > 0 && lines.at(-1).ending === "") {
      lines.at(-1).ending = newline;
    }
    lines.splice(firstTable, 0, ...missing);
  }

  let patched = lines.map((line) => line.content + line.ending).join("");
  const separator = patched.length === 0 ? "" : patched.endsWith(newline + newline)
    ? ""
    : patched.endsWith(newline) ? newline : newline + newline;
  const managerPath = validatedAbsolutePath(options.managerPath ?? runtime.managerPath ?? thisFile, "manager path");
  const nodePath = validatedAbsolutePath(runtime.nodePath, "node path");
  const block = [
    MARKER_BEGIN,
    `[model_providers.${PROVIDER_ID}]`,
    'name = "Cursor Subscription (remote SyncBar bridge)"',
    `base_url = "http://127.0.0.1:${runtime.port}/v1"`,
    'wire_api = "responses"',
    `auth = { command = ${tomlString(nodePath)}, args = [${tomlString(managerPath)}, "auth"], timeout_ms = 15000, refresh_interval_ms = 0 }`,
    "request_max_retries = 0",
    "stream_max_retries = 0",
    "stream_idle_timeout_ms = 900000",
    MARKER_END,
  ].join(newline);
  patched += separator + block + newline;
  return patched;
}

function privateJSON(value) {
  return Buffer.from(`${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function decodeJSON(data, description) {
  try {
    return JSON.parse(data.toString("utf8"));
  } catch {
    fail(`${description} is not valid JSON`, "invalid_json");
  }
}

function runtimeFromDisk(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    fail("Cursor remote runtime is invalid", "invalid_runtime");
  }
  const legacyKeys = [
    "agentPath", "apiKey", "bridgePath", "bridgeToken", "cursorHome", "home", "managerPath",
    "model", "models", "nodePath", "port", "schemaVersion", "workspace",
    "xdgCache", "xdgConfig", "xdgData", "xdgState",
  ];
  const currentKeys = [
    ...legacyKeys, "catalogPath", "codexModel", "modelParameters", "modelRoutes", "nativeModels",
  ];
  const actualKeys = Object.keys(value).sort();
  const isCurrent = value.schemaVersion === RUNTIME_SCHEMA_VERSION;
  const expectedKeys = isCurrent
    ? currentKeys.sort()
    : (Object.hasOwn(value, "modelParameters") ? [...legacyKeys, "modelParameters"].sort() : legacyKeys);
  if (actualKeys.length !== expectedKeys.length ||
      actualKeys.some((key, index) => key !== expectedKeys[index])) {
    fail("Cursor remote runtime has missing or unknown fields", "invalid_runtime");
  }
  if (!isCurrent && value.schemaVersion !== 1) fail("Cursor remote runtime schema is invalid", "invalid_runtime");
  const modelParameters = Object.hasOwn(value, "modelParameters")
    ? value.modelParameters
    : (Array.isArray(value.models) && value.models.every((model) => typeof model === "string")
        ? migratedModelParameters(value.models)
        : {});
  const model = validatedModel(value.model);
  const port = Number(value.port);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    fail("Cursor remote runtime port is invalid", "invalid_runtime");
  }
  const models = validatedModels(value.models, model);
  const modelParametersValue = validatedModelParameters(modelParameters, models);
  const codexModel = isCurrent ? validatedModel(value.codexModel) : model;
  const modelRoutes = isCurrent
    ? validatedModelRoutesJSON(JSON.stringify(value.modelRoutes), models, codexModel)
    : {};
  const nativeModels = isCurrent
    ? validatedNativeModels(value.nativeModels, models, modelRoutes)
    : [];
  return {
    schemaVersion: value.schemaVersion,
    apiKey: validatedAPIKey(value.apiKey),
    model,
    port,
    bridgeToken: validatedBridgeToken(value.bridgeToken),
    models,
    modelParameters: modelParametersValue,
    codexModel,
    modelRoutes,
    nativeModels,
    ...(isCurrent ? { catalogPath: validatedAbsolutePath(value.catalogPath, "catalog path") } : {}),
    home: validatedAbsolutePath(value.home, "runtime HOME"),
    cursorHome: validatedAbsolutePath(value.cursorHome, "Cursor isolated HOME"),
    agentPath: validatedAbsolutePath(value.agentPath, "Cursor agent path"),
    bridgePath: validatedAbsolutePath(value.bridgePath, "bridge path"),
    nodePath: validatedAbsolutePath(value.nodePath, "Node path"),
    managerPath: validatedAbsolutePath(value.managerPath, "manager path"),
    workspace: validatedAbsolutePath(value.workspace, "workspace path"),
    xdgConfig: validatedAbsolutePath(value.xdgConfig, "XDG config path"),
    xdgData: validatedAbsolutePath(value.xdgData, "XDG data path"),
    xdgCache: validatedAbsolutePath(value.xdgCache, "XDG cache path"),
    xdgState: validatedAbsolutePath(value.xdgState, "XDG state path"),
  };
}

export async function readRuntime(options = {}) {
  const paths = options.paths ?? managerPaths(options);
  const snapshot = await safeSnapshot(paths.runtime, { privateFile: true, maxBytes: MAX_RUNTIME_BYTES });
  if (!snapshot.exists) fail("Cursor remote runtime is not provisioned", "not_provisioned");
  if (snapshot.mode !== 0o600) fail("Cursor remote runtime must have mode 0600", "unsafe_permissions");
  return runtimeFromDisk(decodeJSON(snapshot.data, "Cursor remote runtime"));
}

async function executablePath(candidate, description) {
  const absolute = validatedAbsolutePath(candidate, description);
  let resolved;
  try {
    resolved = await realpath(absolute);
    await access(resolved, fsConstants.X_OK);
    const info = await stat(resolved);
    if (!info.isFile() || (info.mode & 0o022) !== 0) {
      fail(`${description} is unsafe`, "unsafe_executable");
    }
  } catch (error) {
    if (error instanceof RemoteManagerError) throw error;
    fail(`${description} is unavailable`, "missing_executable");
  }
  return resolved;
}

async function regularFilePath(candidate, description) {
  const absolute = validatedAbsolutePath(candidate, description);
  let resolved;
  try {
    resolved = await realpath(absolute);
    const info = await stat(resolved);
    if (!info.isFile() || !ownedByCurrentUser(info) || (info.mode & 0o022) !== 0) {
      fail(`${description} is unsafe`, "unsafe_path");
    }
  } catch (error) {
    if (error instanceof RemoteManagerError) throw error;
    fail(`${description} is unavailable`, "missing_file");
  }
  return resolved;
}

function boundedChild(executable, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, {
      cwd: options.cwd,
      env: options.env,
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stdout = Buffer.alloc(0);
    let stderr = Buffer.alloc(0);
    let overflow = false;
    const append = (current, chunk) => {
      if (current.length + chunk.length > (options.maxOutputBytes ?? MAX_CHILD_OUTPUT_BYTES)) {
        overflow = true;
        child.kill("SIGKILL");
        return current;
      }
      return Buffer.concat([current, chunk]);
    };
    child.stdout.on("data", (chunk) => { stdout = append(stdout, chunk); });
    child.stderr.on("data", (chunk) => { stderr = append(stderr, chunk); });
    child.on("error", () => reject(new RemoteManagerError("A required child process failed to start", "child_start_failed")));
    const timer = setTimeout(() => child.kill("SIGKILL"), options.timeoutMs ?? 20_000);
    child.on("close", (code, signal) => {
      clearTimeout(timer);
      if (overflow) return reject(new RemoteManagerError("A child process produced too much output", "child_output_too_large"));
      if (signal) return reject(new RemoteManagerError("A child process timed out or was terminated", "child_terminated"));
      resolve({ code, stdout, stderr });
    });
    if (options.input) child.stdin.end(options.input);
    else child.stdin.end();
  });
}

function baseChildEnvironment(base = process.env) {
  const result = {};
  for (const key of ["PATH", "LANG", "LC_ALL", "LC_CTYPE", "SHELL", "TERM", "TMPDIR", "USER", "LOGNAME"]) {
    if (typeof base[key] === "string") result[key] = base[key];
  }
  return result;
}

function cursorRuntimeEnvironment(runtime, base = process.env) {
  return {
    ...baseChildEnvironment(base),
    HOME: runtime.cursorHome,
    XDG_CONFIG_HOME: runtime.xdgConfig,
    XDG_DATA_HOME: runtime.xdgData,
    XDG_CACHE_HOME: runtime.xdgCache,
    XDG_STATE_HOME: runtime.xdgState,
    CURSOR_API_KEY: runtime.apiKey,
    AGENT_CLI_CREDENTIAL_STORE: CURSOR_FILE_CREDENTIAL_STORE,
    NO_COLOR: "1",
  };
}

function discoveredModelSlugs(output) {
  const result = new Set();
  const plain = output.replace(/\u001b\[[0-9;]*m/g, "");
  for (const line of plain.split(/\r?\n/)) {
    const separator = line.indexOf(" - ");
    if (separator < 0) continue;
    const slug = line.slice(0, separator).trim();
    if (MODEL_PATTERN.test(slug)) result.add(slug);
  }
  return result;
}

async function validateCursorSDK(runtime, environment) {
  const childEnvironment = cursorRuntimeEnvironment(runtime, environment);
  const statusResult = await boundedChild(runtime.nodePath, [
    runtime.bridgePath,
    "--sdk-status",
  ], {
    env: childEnvironment,
    cwd: runtime.home,
    timeoutMs: 15_000,
  });
  if (statusResult.code !== 0) {
    fail("Cursor SDK credential validation failed", "cursor_unauthenticated");
  }
  let account;
  try { account = JSON.parse(statusResult.stdout.toString("utf8")); }
  catch { fail("Cursor SDK account response is invalid", "cursor_unauthenticated"); }
  if (account?.schema_version !== 1 ||
      !(account.email === null || account.email === undefined || typeof account.email === "string")) {
    fail("Cursor SDK account response is invalid", "cursor_unauthenticated");
  }
  const modelsResult = await boundedChild(runtime.nodePath, [
    runtime.bridgePath,
    "--sdk-list-models",
  ], {
    env: childEnvironment,
    cwd: runtime.home,
    timeoutMs: 20_000,
  });
  if (modelsResult.code !== 0) {
    fail("Cursor SDK model validation failed", "cursor_models_failed");
  }
  const available = discoveredModelSlugs(modelsResult.stdout.toString("utf8"));
  if (runtime.models.some((model) => !available.has(model))) {
    fail("Cursor SDK did not report every configured model", "cursor_model_mismatch");
  }
}

function fetchHTTPS(url, redirects = 0) {
  return new Promise((resolve, reject) => {
    if (redirects > 5) return reject(new RemoteManagerError("Cursor installer redirected too many times", "installer_download_failed"));
    const request = https.get(url, { headers: { "User-Agent": "CodexSyncBar-CursorRemoteManager/1" } }, (response) => {
      const statusCode = response.statusCode ?? 0;
      if ([301, 302, 303, 307, 308].includes(statusCode) && response.headers.location) {
        response.resume();
        const redirected = new URL(response.headers.location, url);
        if (redirected.protocol !== "https:") return reject(new RemoteManagerError("Cursor installer redirected to an unsafe URL", "installer_download_failed"));
        fetchHTTPS(redirected, redirects + 1).then(resolve, reject);
        return;
      }
      if (statusCode !== 200) {
        response.resume();
        reject(new RemoteManagerError("Cursor installer download failed", "installer_download_failed"));
        return;
      }
      const chunks = [];
      let bytes = 0;
      response.on("data", (chunk) => {
        bytes += chunk.length;
        if (bytes > MAX_INSTALLER_BYTES) {
          request.destroy(new RemoteManagerError("Cursor installer is too large", "installer_download_failed"));
          return;
        }
        chunks.push(chunk);
      });
      response.on("end", () => resolve(Buffer.concat(chunks)));
    });
    request.setTimeout(20_000, () => request.destroy(new RemoteManagerError("Cursor installer download timed out", "installer_download_failed")));
    request.on("error", (error) => reject(error instanceof RemoteManagerError
      ? error
      : new RemoteManagerError("Cursor installer download failed", "installer_download_failed")));
  });
}

async function installerSource(urlString) {
  let url;
  try {
    url = new URL(urlString);
  } catch {
    fail("Cursor installer URL is invalid", "invalid_installer_url");
  }
  if (url.protocol === "file:") {
    const data = await readFile(fileURLToPath(url));
    if (data.length > MAX_INSTALLER_BYTES) fail("Cursor installer is too large", "installer_download_failed");
    return data;
  }
  if (url.protocol !== "https:") fail("Cursor installer URL must use HTTPS", "invalid_installer_url");
  return fetchHTTPS(url);
}

async function installCursorAgent(paths, environment) {
  const source = await installerSource(environment.CURSOR_REMOTE_INSTALL_URL ?? DEFAULT_INSTALL_URL);
  // Cursor's official installer uses Bash conditionals (`[[ ... ]]`). Running
  // it through a generic /bin/sh succeeds far enough to print "installed" on
  // Debian/Ubuntu, but can skip or fail the final symlink setup under dash.
  const shell = environment.CURSOR_REMOTE_SHELL_PATH ?? "/bin/bash";
  // The installer must target the real remote HOME. Do not pass the
  // undocumented credential-store override (or the Cursor API key) here.
  const childEnvironment = {
    ...baseChildEnvironment(environment),
    HOME: paths.home,
    NO_COLOR: "1",
  };
  const result = await boundedChild(shell, [], {
    env: childEnvironment,
    cwd: paths.home,
    input: source,
    timeoutMs: 60_000,
  });
  if (result.code !== 0) fail("The official Cursor installer failed", "installer_failed");
}

async function resolvedCursorSDKNode(agentPath, paths, environment) {
  const explicit = environment.CURSOR_REMOTE_NODE_PATH;
  const candidates = explicit
    ? [explicit]
    : [path.join(path.dirname(agentPath), "node"), process.execPath];
  for (const candidate of candidates) {
    let executable;
    try { executable = await executablePath(candidate, "Node"); }
    catch (error) {
      if (explicit || !(error instanceof RemoteManagerError) || error.code !== "missing_executable") {
        throw error;
      }
      continue;
    }
    const result = await boundedChild(executable, [
      "-e",
      "const [a,b]=process.versions.node.split('.').map(Number);process.exit(a>22||(a===22&&b>=13)?0:1)",
    ], {
      env: baseChildEnvironment(environment),
      cwd: paths.home,
      timeoutMs: 5_000,
    });
    if (result.code === 0) return executable;
    if (explicit) fail("Cursor SDK requires Node.js 22.13 or newer", "node_too_old");
  }
  fail("Cursor SDK requires Node.js 22.13 or newer", "node_too_old");
}

async function resolvedRuntime(input, paths, environment, options = {}) {
  const requestedAgent = environment.CURSOR_REMOTE_AGENT_PATH ?? paths.defaultAgent;
  let agentPath;
  try {
    agentPath = await executablePath(requestedAgent, "Cursor agent");
  } catch (error) {
    if (!(error instanceof RemoteManagerError) || error.code !== "missing_executable") throw error;
    await installCursorAgent(paths, environment);
    agentPath = await executablePath(requestedAgent, "Cursor agent");
  }
  const bridgePath = await regularFilePath(
    environment.CURSOR_REMOTE_BRIDGE_PATH ?? paths.defaultBridge,
    "Cursor bridge",
  );
  const nodePath = await resolvedCursorSDKNode(agentPath, paths, environment);
  const managerPath = await regularFilePath(options.managerPath ?? thisFile, "Cursor remote manager");
  const { catalogData: _catalogData, ...runtimeInput } = input;
  return {
    ...runtimeInput,
    home: paths.home,
    cursorHome: paths.cursorHome,
    agentPath,
    bridgePath,
    nodePath,
    managerPath,
    catalogPath: paths.catalog,
    workspace: paths.workspace,
    xdgConfig: paths.xdgConfig,
    xdgData: paths.xdgData,
    xdgCache: paths.xdgCache,
    xdgState: paths.xdgState,
  };
}

function decodeBackup(snapshot) {
  if (!snapshot.exists) return null;
  if (snapshot.mode !== 0o600) fail("Cursor config backup must have mode 0600", "unsafe_permissions");
  const value = decodeJSON(snapshot.data, "Cursor config backup");
  const isLegacy = value?.schemaVersion === 1;
  const hasInstalledModel = value?.schemaVersion === 2 || value?.schemaVersion === 3;
  const isCurrent = value?.schemaVersion === 3;
  if (
    (!isLegacy && !hasInstalledModel) ||
    typeof value.originalExisted !== "boolean" ||
    typeof value.originalDataBase64 !== "string" ||
    typeof value.originalSHA256 !== "string" ||
    typeof value.installedSHA256 !== "string" ||
    typeof value.runtimeSHA256 !== "string" ||
    !Number.isInteger(value.originalMode) ||
    (hasInstalledModel && typeof value.installedModel !== "string") ||
    (isLegacy && Object.hasOwn(value, "installedModel"))
  ) {
    fail("Cursor config backup is invalid", "invalid_backup");
  }
  const originalData = Buffer.from(value.originalDataBase64, "base64");
  const expectedHash = value.originalExisted ? sha256(originalData) : sha256(Buffer.alloc(0));
  if (expectedHash !== value.originalSHA256) fail("Cursor config backup checksum is invalid", "invalid_backup");
  let originalCatalogData = Buffer.alloc(0);
  if (isCurrent) {
    if (typeof value.originalCatalogExisted !== "boolean" ||
        typeof value.originalCatalogDataBase64 !== "string" ||
        typeof value.originalCatalogSHA256 !== "string" ||
        typeof value.installedCatalogSHA256 !== "string" ||
        !Number.isInteger(value.originalCatalogMode)) {
      fail("Cursor catalog backup is invalid", "invalid_backup");
    }
    originalCatalogData = Buffer.from(value.originalCatalogDataBase64, "base64");
    const catalogHash = value.originalCatalogExisted
      ? sha256(originalCatalogData)
      : sha256(Buffer.alloc(0));
    if (catalogHash !== value.originalCatalogSHA256 ||
        (value.originalCatalogExisted && originalCatalogData.toString("base64") !== value.originalCatalogDataBase64)) {
      fail("Cursor catalog backup checksum is invalid", "invalid_backup");
    }
  }
  return {
    ...value,
    installedModel: hasInstalledModel ? validatedModel(value.installedModel) : null,
    originalData,
    originalCatalogExisted: isCurrent ? value.originalCatalogExisted : false,
    originalCatalogMode: isCurrent ? value.originalCatalogMode : 0o600,
    originalCatalogData,
  };
}

function topLevelModelProviderLines(text) {
  const lines = splitLines(text);
  const found = new Map();
  for (let index = 0; index < lines.length; index += 1) {
    const trimmed = lines[index].content.trim();
    if (trimmed.startsWith("[")) break;
    if (!trimmed || trimmed.startsWith("#")) continue;
    if (trimmed.includes('\"\"\"') || trimmed.includes("'''")) {
      fail("Top-level multiline TOML cannot be validated safely", "unsupported_config");
    }
    if (/^["'](?:model|model_provider|model_catalog_json)["']\s*=/.test(trimmed)) {
      fail("Quoted top-level model keys cannot be validated safely", "unsupported_config");
    }
    const match = /^(model|model_provider|model_catalog_json)\s*=/.exec(trimmed);
    if (!match) continue;
    if (found.has(match[1])) fail("Duplicate top-level model configuration", "unsupported_config");
    found.set(match[1], { index, content: lines[index].content });
  }
  return { lines, found };
}

function strictManagedTopLevel(text) {
  const parsed = topLevelModelProviderLines(text);
  const modelLine = parsed.found.get("model");
  const providerLine = parsed.found.get("model_provider");
  const catalogLine = parsed.found.get("model_catalog_json");
  if (!modelLine || !providerLine) {
    fail("Managed Codex config has missing model settings", "unsupported_config");
  }
  const modelMatch = /^model\s*=\s*"([A-Za-z0-9][A-Za-z0-9._:/-]{0,127})"\s*$/.exec(
    modelLine.content.trim(),
  );
  const providerMatch = /^model_provider\s*=\s*"([A-Za-z0-9][A-Za-z0-9._:/-]{0,127})"\s*$/.exec(
    providerLine.content.trim(),
  );
  if (!modelMatch || !providerMatch || providerMatch[1] !== PROVIDER_ID) {
    fail("Managed Codex model settings are invalid", "unsupported_config");
  }
  const catalogMatch = catalogLine
    ? /^model_catalog_json\s*=\s*"([^"\r\n]+)"\s*$/.exec(catalogLine.content.trim())
    : null;
  if (catalogLine && !catalogMatch) {
    fail("Managed Codex catalog setting is invalid", "unsupported_config");
  }
  return { ...parsed, model: validatedModel(modelMatch[1]), catalogPath: catalogMatch?.[1] ?? null };
}

function restoreOriginalTopLevel(managedText, originalText, includeCatalog = false) {
  const managed = topLevelModelProviderLines(managedText);
  const original = topLevelModelProviderLines(originalText);
  const removals = [];
  const keys = includeCatalog
    ? ["model", "model_provider", "model_catalog_json"]
    : ["model", "model_provider"];
  for (const key of keys) {
    const managedLine = managed.found.get(key);
    if (!managedLine) fail("Managed Codex config has missing model settings", "unsupported_config");
    const originalLine = original.found.get(key);
    if (originalLine) managed.lines[managedLine.index].content = originalLine.content;
    else removals.push(managedLine.index);
  }
  for (const index of removals.sort((a, b) => b - a)) managed.lines.splice(index, 1);
  return managed.lines.map((line) => line.content + line.ending).join("");
}

function prefixCandidatesBeforeMarker(currentText) {
  const first = currentText.indexOf(MARKER_BEGIN);
  if (first < 0 || first !== currentText.lastIndexOf(MARKER_BEGIN) ||
      currentText.indexOf(MARKER_END) < first ||
      currentText.indexOf(MARKER_END) !== currentText.lastIndexOf(MARKER_END)) {
    fail("Managed Cursor provider marker is missing or duplicated", "cas_mismatch");
  }
  const result = [currentText.slice(0, first)];
  for (let attempt = 0; attempt < 2; attempt += 1) {
    const previous = result.at(-1);
    if (previous.endsWith("\r\n")) result.push(previous.slice(0, -2));
    else if (previous.endsWith("\n")) result.push(previous.slice(0, -1));
    else break;
  }
  return result;
}

function managedConfigState(configSnapshot, backup, runtime, options = {}) {
  if (!configSnapshot.exists || configSnapshot.mode !== 0o600) {
    fail("Codex config changed after Cursor provisioning", "cas_mismatch");
  }
  const installedModel = backup.installedModel ?? runtime.codexModel ?? runtime.model;
  if (configSnapshot.hash === backup.installedSHA256) {
    return { selectedModel: installedModel, baseData: backup.originalData };
  }
  let originalText;
  let currentText;
  try {
    originalText = new TextDecoder("utf-8", { fatal: true }).decode(backup.originalData);
    currentText = new TextDecoder("utf-8", { fatal: true }).decode(configSnapshot.data);
  } catch {
    fail("config.toml is not UTF-8", "invalid_config");
  }
  const expectedText = patchCodexConfig(
    originalText,
    { ...runtime, codexModel: installedModel },
    options,
  );
  if (sha256(Buffer.from(expectedText, "utf8")) !== backup.installedSHA256) {
    fail("Cursor config backup does not match its managed runtime", "invalid_backup");
  }

  const current = strictManagedTopLevel(currentText);
  if (runtime.catalogPath && current.catalogPath !== runtime.catalogPath) {
    fail("Codex model catalog changed after Cursor provisioning", "cas_mismatch");
  }
  for (const prefix of prefixCandidatesBeforeMarker(currentText)) {
    const restored = restoreOriginalTopLevel(prefix, originalText, Boolean(runtime.catalogPath));
    const roundTrip = patchCodexConfig(
      restored,
      { ...runtime, codexModel: current.model },
      options,
    );
    if (roundTrip === currentText) {
      return { selectedModel: current.model, baseData: Buffer.from(restored, "utf8") };
    }
  }
  fail("Codex config changed after Cursor provisioning", "cas_mismatch");
}

function candidate(data, mode = 0o600) {
  return { exists: true, data: Buffer.from(data), hash: sha256(data), mode };
}

function absentCandidate() {
  return { exists: false, data: Buffer.alloc(0), hash: sha256(Buffer.alloc(0)), mode: 0o600 };
}

const TRANSACTION_FILE_KEYS = ["runtime", "catalog", "config", "backup"];
const LEGACY_TRANSACTION_FILE_KEYS = ["runtime", "config", "backup"];

function exactObjectKeys(value, keys) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  return actual.length === expected.length &&
    actual.every((key, index) => key === expected[index]);
}

function encodeSnapshot(snapshot) {
  return {
    exists: snapshot.exists,
    dataBase64: snapshot.exists ? snapshot.data.toString("base64") : "",
    sha256: snapshot.hash,
    mode: snapshot.mode,
  };
}

function decodeJournalSnapshot(value, privateFile) {
  if (!exactObjectKeys(value, ["dataBase64", "exists", "mode", "sha256"]) ||
      typeof value.exists !== "boolean" || typeof value.dataBase64 !== "string" ||
      !/^[a-f0-9]{64}$/.test(value.sha256) || !Number.isInteger(value.mode) ||
      value.mode < 0 || value.mode > 0o777 || (value.mode & 0o022) !== 0 ||
      (privateFile && value.exists && (value.mode & 0o077) !== 0) ||
      (!value.exists && (value.dataBase64 !== "" || value.mode !== 0o600))) {
    fail("Cursor transaction journal is invalid", "invalid_journal");
  }
  const data = Buffer.from(value.dataBase64, "base64");
  if ((value.exists && data.toString("base64") !== value.dataBase64) ||
      sha256(data) !== value.sha256) {
    fail("Cursor transaction journal checksum is invalid", "invalid_journal");
  }
  return { exists: value.exists, data, hash: value.sha256, mode: value.mode };
}

function transactionFileSpecs(paths) {
  return {
    runtime: { path: paths.runtime, privateFile: true, maxBytes: MAX_RUNTIME_BYTES },
    catalog: { path: paths.catalog, privateFile: true, maxBytes: MAX_CATALOG_BYTES },
    config: { path: paths.config, privateFile: false },
    backup: { path: paths.backup, privateFile: true, maxBytes: MAX_PROVISION_BYTES },
  };
}

function encodeTransactionJournal(operation, files, bridge) {
  const encodedFiles = {};
  for (const key of TRANSACTION_FILE_KEYS) {
    encodedFiles[key] = {
      old: encodeSnapshot(files[key].old),
      new: encodeSnapshot(files[key].new),
    };
  }
  return privateJSON({
    schemaVersion: 1,
    operation,
    files: encodedFiles,
    oldBridgeExpected: bridge.oldBridgeExpected,
    newBridgeExpected: bridge.newBridgeExpected,
  });
}

function decodeTransactionJournal(snapshot) {
  if (!snapshot.exists) return null;
  if (snapshot.mode !== 0o600) fail("Cursor transaction journal must have mode 0600", "unsafe_permissions");
  const value = decodeJSON(snapshot.data, "Cursor transaction journal");
  if (!exactObjectKeys(value, [
    "files", "newBridgeExpected", "oldBridgeExpected", "operation", "schemaVersion",
  ]) || value.schemaVersion !== 1 || !["provision", "deprovision"].includes(value.operation) ||
      typeof value.oldBridgeExpected !== "boolean" ||
      typeof value.newBridgeExpected !== "boolean" ||
      (!exactObjectKeys(value.files, TRANSACTION_FILE_KEYS) &&
       !exactObjectKeys(value.files, LEGACY_TRANSACTION_FILE_KEYS))) {
    fail("Cursor transaction journal is invalid", "invalid_journal");
  }
  const fileKeys = Object.hasOwn(value.files, "catalog")
    ? TRANSACTION_FILE_KEYS
    : LEGACY_TRANSACTION_FILE_KEYS;
  const specs = { runtime: true, catalog: true, config: false, backup: true };
  const files = {};
  for (const key of fileKeys) {
    const pair = value.files[key];
    if (!exactObjectKeys(pair, ["new", "old"])) {
      fail("Cursor transaction journal is invalid", "invalid_journal");
    }
    files[key] = {
      old: decodeJournalSnapshot(pair.old, specs[key]),
      new: decodeJournalSnapshot(pair.new, specs[key]),
    };
  }
  if (value.operation === "provision" &&
      (!files.runtime.new.exists || files.runtime.new.mode !== 0o600 ||
       (files.catalog && (!files.catalog.new.exists || files.catalog.new.mode !== 0o600)) ||
       !files.backup.new.exists || files.backup.new.mode !== 0o600 ||
       !files.config.new.exists)) {
    fail("Cursor provision journal is invalid", "invalid_journal");
  }
  if (value.operation === "deprovision" &&
      (!files.runtime.old.exists || !files.backup.old.exists ||
       files.runtime.new.exists || files.backup.new.exists || value.newBridgeExpected)) {
    fail("Cursor deprovision journal is invalid", "invalid_journal");
  }
  return {
    operation: value.operation,
    files,
    fileKeys,
    oldBridgeExpected: value.oldBridgeExpected,
    newBridgeExpected: value.newBridgeExpected,
    snapshot,
  };
}

async function readTransactionJournal(paths) {
  const snapshot = await safeSnapshot(paths.journal, {
    privateFile: true,
    maxBytes: MAX_JOURNAL_BYTES,
  });
  return decodeTransactionJournal(snapshot);
}

async function writeTransactionJournal(paths, operation, files, bridge) {
  const existing = await safeSnapshot(paths.journal, {
    privateFile: true,
    maxBytes: MAX_JOURNAL_BYTES,
  });
  if (existing.exists) fail("A Cursor transaction is already pending", "pending_transaction");
  const data = encodeTransactionJournal(operation, files, bridge);
  if (data.length > MAX_JOURNAL_BYTES) fail("Cursor transaction journal is too large", "file_too_large");
  await atomicCompareAndSwap(paths.journal, existing, candidate(data));
  return decodeTransactionJournal(await safeSnapshot(paths.journal, {
    privateFile: true,
    maxBytes: MAX_JOURNAL_BYTES,
  }));
}

async function clearTransactionJournal(paths, journal) {
  await atomicCompareAndSwap(paths.journal, journal.snapshot, absentCandidate());
}

function runtimeFromJournalSnapshot(snapshot, description) {
  if (!snapshot.exists || snapshot.mode !== 0o600) {
    fail(`${description} is missing`, "invalid_journal");
  }
  return runtimeFromDisk(decodeJSON(snapshot.data, description));
}

async function classifyTransactionFiles(paths, journal) {
  const specs = transactionFileSpecs(paths);
  const state = {};
  for (const key of journal.fileKeys) {
    const current = await safeSnapshot(specs[key].path, {
      privateFile: specs[key].privateFile,
      maxBytes: specs[key].maxBytes,
    });
    const matchesOld = sameSnapshot(current, journal.files[key].old);
    const matchesNew = sameSnapshot(current, journal.files[key].new);
    if (!matchesOld && !matchesNew) {
      fail("A managed file drifted during transaction recovery", "transaction_drift");
    }
    state[key] = { current, matchesOld, matchesNew };
  }
  return state;
}

async function applyTransactionFiles(paths, journal, target, options = {}) {
  const specs = transactionFileSpecs(paths);
  const order = target === "new"
    ? (journal.operation === "provision"
        ? ["runtime", "catalog", "config", "backup"]
        : ["config", "catalog", "runtime", "backup"])
    : (journal.operation === "provision"
        ? ["backup", "config", "catalog", "runtime"]
        : ["backup", "runtime", "catalog", "config"]);
  for (const key of order.filter((candidate) => journal.fileKeys.includes(candidate))) {
    const current = await safeSnapshot(specs[key].path, {
      privateFile: specs[key].privateFile,
      maxBytes: specs[key].maxBytes,
    });
    const desired = journal.files[key][target];
    if (sameSnapshot(current, desired)) continue;
    const alternate = journal.files[key][target === "new" ? "old" : "new"];
    if (!sameSnapshot(current, alternate)) {
      fail("A managed file drifted during transaction recovery", "transaction_drift");
    }
    await atomicCompareAndSwap(specs[key].path, current, desired);
    if (target === "new") {
      crashAfter(options, journal.operation, `after-${key}-commit`);
    }
  }
}

async function stopJournalBridge(journal, target) {
  const snapshot = journal.files.runtime[target];
  if (!snapshot.exists) return;
  const runtime = runtimeFromJournalSnapshot(snapshot, "Cursor transaction runtime");
  await stopHealthyBridge(runtime);
}

async function completeTransactionTarget(paths, journal, target, options = {}) {
  if (target === "new") {
    if (journal.oldBridgeExpected) {
      await stopJournalBridge(journal, "old");
      crashAfter(options, journal.operation, "after-stop-old");
    }
    await applyTransactionFiles(paths, journal, "new", options);
    if (journal.operation === "provision" && journal.newBridgeExpected) {
      const runtime = runtimeFromJournalSnapshot(journal.files.runtime.new, "Cursor provision runtime");
      await ensureDetachedBridge({ ...options, runtime });
      crashAfter(options, journal.operation, "after-start-new");
    }
    if (journal.operation === "deprovision") await removeDedicatedXDGRoot(paths);
    return;
  }

  if (journal.newBridgeExpected) await stopJournalBridge(journal, "new");
  await applyTransactionFiles(paths, journal, "old", options);
  if (journal.operation === "provision" && !journal.files.runtime.old.exists &&
      !journal.files.backup.old.exists) {
    await removeDedicatedXDGRoot(paths);
  }
  if (journal.oldBridgeExpected) {
    const runtime = runtimeFromJournalSnapshot(journal.files.runtime.old, "Cursor rollback runtime");
    await ensureDetachedBridge({ ...options, runtime });
  }
}

async function rollbackTransaction(paths, journal, options = {}) {
  await classifyTransactionFiles(paths, journal);
  await completeTransactionTarget(paths, journal, "old", options);
  await clearTransactionJournal(paths, journal);
}

async function recoverPendingTransaction(paths, options = {}) {
  const journal = await readTransactionJournal(paths);
  if (!journal) return;
  const state = await classifyTransactionFiles(paths, journal);
  const allOld = journal.fileKeys.every((key) => state[key].matchesOld);
  if (allOld) {
    await completeTransactionTarget(paths, journal, "old", options);
    await clearTransactionJournal(paths, journal);
    return;
  }
  try {
    await completeTransactionTarget(paths, journal, "new", options);
    await clearTransactionJournal(paths, journal);
  } catch (error) {
    try {
      await rollbackTransaction(paths, journal, options);
    } catch {
      throw error;
    }
    throw new RemoteManagerError(
      "A pending Cursor transaction could not roll forward and was rolled back",
      "transaction_recovery_rolled_back",
    );
  }
}

function crashAfter(options, operation, stage) {
  if (options.crashAfter === `${operation}:${stage}`) process.exit(86);
}

export async function provision(inputValue, options = {}) {
  const environment = options.env ?? process.env;
  const paths = options.paths ?? managerPaths({ ...options, env: environment });
  const input = validateProvisionInput(inputValue);
  return withManagerLock(paths, async () => {
    // Establish a trusted old state before Cursor sees the new API key. A
    // drifted managed install must fail without touching its bridge or auth.
    const configSnapshot = await safeSnapshot(paths.config, { privateFile: false });
    const runtimeSnapshot = await safeSnapshot(
      paths.runtime,
      { privateFile: true, maxBytes: MAX_RUNTIME_BYTES },
    );
    const backupSnapshot = await safeSnapshot(
      paths.backup,
      { privateFile: true, maxBytes: MAX_PROVISION_BYTES },
    );
    const catalogSnapshot = await safeSnapshot(
      paths.catalog,
      { privateFile: true, maxBytes: MAX_CATALOG_BYTES },
    );
    const freshAttempt = !runtimeSnapshot.exists && !backupSnapshot.exists;
    let journal = null;
    try {
      const existingBackup = decodeBackup(backupSnapshot);
      let original;
      let oldRuntime = null;
      let selectedConfigModel = input.codexModel;
      let originalCatalog = catalogSnapshot;
      if (existingBackup) {
        if (!runtimeSnapshot.exists || runtimeSnapshot.hash !== existingBackup.runtimeSHA256 ||
            runtimeSnapshot.mode !== 0o600) {
          fail("Cursor runtime changed after provisioning", "cas_mismatch");
        }
        oldRuntime = runtimeFromDisk(decodeJSON(runtimeSnapshot.data, "Cursor remote runtime"));
        const managedState = managedConfigState(
          configSnapshot,
          existingBackup,
          oldRuntime,
          options,
        );
        selectedConfigModel = existingBackup.schemaVersion === 3
          ? managedState.selectedModel
          : input.codexModel;
        original = {
          exists: existingBackup.originalExisted || managedState.baseData.length > 0,
          data: managedState.baseData,
          hash: sha256(managedState.baseData),
          mode: existingBackup.originalMode,
        };
        if (existingBackup.schemaVersion === 3) {
          if (!catalogSnapshot.exists || catalogSnapshot.hash !== existingBackup.installedCatalogSHA256 ||
              catalogSnapshot.mode !== 0o600) {
            fail("Cursor model catalog changed after provisioning", "cas_mismatch");
          }
          originalCatalog = existingBackup.originalCatalogExisted
            ? candidate(existingBackup.originalCatalogData, existingBackup.originalCatalogMode)
            : absentCandidate();
        } else if (catalogSnapshot.exists) {
          fail("An unmanaged Cursor model catalog already exists", "catalog_collision");
        }
      } else {
        if (runtimeSnapshot.exists) fail("An unmanaged Cursor runtime already exists", "runtime_collision");
        if (catalogSnapshot.exists) fail("An unmanaged Cursor model catalog already exists", "catalog_collision");
        original = configSnapshot;
        originalCatalog = catalogSnapshot;
      }

      // A managed reprovision must replace the bridge generation even when the
      // model, port, and bearer token are unchanged (for example, API-key
      // rotation). Establish the trusted old runtime first; an occupied port
      // that cannot answer its authenticated health check then fails before the
      // new secret or any managed file is touched.
      const oldBridgeExpected = oldRuntime !== null;
      const oldBridgeHealth = oldBridgeExpected
        ? await ensureDetachedBridge({ ...options, paths, runtime: oldRuntime })
        : null;

      let originalText;
      try {
        originalText = new TextDecoder("utf-8", { fatal: true }).decode(original.data);
      } catch {
        fail("config.toml is not UTF-8", "invalid_config");
      }

      await ensureCursorXDGDirectories(paths);
      const runtime = await resolvedRuntime(input, paths, environment, options);
      await validateCursorSDK(runtime, environment);
      if (!Object.hasOwn(runtime.modelRoutes, selectedConfigModel) &&
          !runtime.nativeModels.includes(selectedConfigModel)) {
        selectedConfigModel = runtime.codexModel;
      }

      const patchedData = Buffer.from(patchCodexConfig(
        originalText,
        { ...runtime, codexModel: selectedConfigModel },
        options,
      ), "utf8");
      const runtimeData = privateJSON(runtime);
      const backup = {
        schemaVersion: 3,
        originalExisted: original.exists,
        originalDataBase64: original.data.toString("base64"),
        originalSHA256: original.exists ? sha256(original.data) : sha256(Buffer.alloc(0)),
        originalMode: original.exists ? original.mode : 0o600,
        installedModel: selectedConfigModel,
        installedSHA256: sha256(patchedData),
        originalCatalogExisted: originalCatalog.exists,
        originalCatalogDataBase64: originalCatalog.exists
          ? originalCatalog.data.toString("base64")
          : "",
        originalCatalogSHA256: originalCatalog.exists
          ? sha256(originalCatalog.data)
          : sha256(Buffer.alloc(0)),
        originalCatalogMode: originalCatalog.exists ? originalCatalog.mode : 0o600,
        installedCatalogSHA256: sha256(input.catalogData),
        runtimeSHA256: sha256(runtimeData),
      };
      const backupData = privateJSON(backup);
      const files = {
        runtime: { old: runtimeSnapshot, new: candidate(runtimeData) },
        catalog: { old: catalogSnapshot, new: candidate(input.catalogData) },
        config: { old: configSnapshot, new: candidate(patchedData) },
        backup: { old: backupSnapshot, new: candidate(backupData) },
      };
      const keepLiveBridge = Boolean(
        oldBridgeExpected &&
        oldBridgeHealth?.healthy &&
        sameBridgeProcessIdentity(oldRuntime, runtime),
      );
      const filesUnchanged = Object.values(files).every((pair) => sameSnapshot(pair.old, pair.new));
      if (keepLiveBridge && filesUnchanged) {
        return {
          provisioned: true,
          model: runtime.model,
          codexModel: runtime.codexModel,
          port: runtime.port,
          models: runtime.models,
          modelParameters: runtime.modelParameters,
          modelRoutes: runtime.modelRoutes,
          nativeModels: runtime.nativeModels,
          agentPath: runtime.agentPath,
        };
      }
      journal = await writeTransactionJournal(paths, "provision", files, {
        oldBridgeExpected: oldBridgeExpected && !keepLiveBridge,
        // Keep a live equivalent bridge. Replace it only when the process
        // identity (API key, token, model, or binary path) actually changed.
        newBridgeExpected: !keepLiveBridge,
      });
      crashAfter(options, "provision", "after-journal");
      if (oldBridgeExpected && !keepLiveBridge) {
        await stopHealthyBridge(oldRuntime, {
          required: true,
          expectedPID: oldBridgeHealth.pid,
          healthTimeoutMs: options.healthTimeoutMs,
        });
      }
      crashAfter(options, "provision", "after-stop-old");

      if (!sameSnapshot(runtimeSnapshot, files.runtime.new)) {
        await atomicCompareAndSwap(paths.runtime, runtimeSnapshot, files.runtime.new);
      }
      crashAfter(options, "provision", "after-runtime-commit");
      if (!sameSnapshot(catalogSnapshot, files.catalog.new)) {
        await atomicCompareAndSwap(paths.catalog, catalogSnapshot, files.catalog.new);
      }
      crashAfter(options, "provision", "after-catalog-commit");
      if (!sameSnapshot(configSnapshot, files.config.new)) {
        await atomicCompareAndSwap(paths.config, configSnapshot, files.config.new);
      }
      crashAfter(options, "provision", "after-config-commit");
      if (!sameSnapshot(backupSnapshot, files.backup.new)) {
        await atomicCompareAndSwap(paths.backup, backupSnapshot, files.backup.new);
      }
      crashAfter(options, "provision", "after-backup-commit");

      if (!keepLiveBridge) {
        await ensureDetachedBridge({ ...options, paths, runtime });
        crashAfter(options, "provision", "after-start-new");
      } else {
        const live = await healthRequest(runtime, options.healthTimeoutMs);
        if (!live.healthy) {
          await ensureDetachedBridge({ ...options, paths, runtime });
        }
      }
      await clearTransactionJournal(paths, journal);
      journal = null;

      return {
        provisioned: true,
        model: runtime.model,
        codexModel: runtime.codexModel,
        port: runtime.port,
        models: runtime.models,
        modelParameters: runtime.modelParameters,
        modelRoutes: runtime.modelRoutes,
        nativeModels: runtime.nativeModels,
        agentPath: runtime.agentPath,
      };
    } catch (error) {
      if (journal) {
        try {
          await rollbackTransaction(paths, journal, options);
          journal = null;
        } catch {
          fail("Cursor provisioning rollback could not be completed", "rollback_failed");
        }
      }
      if (freshAttempt) {
        const liveRuntime = await safeSnapshot(
          paths.runtime,
          { privateFile: true, maxBytes: MAX_RUNTIME_BYTES },
        );
        const liveBackup = await safeSnapshot(
          paths.backup,
          { privateFile: true, maxBytes: MAX_PROVISION_BYTES },
        );
        const liveJournal = await safeSnapshot(
          paths.journal,
          { privateFile: true, maxBytes: MAX_JOURNAL_BYTES },
        );
        if (!liveRuntime.exists && !liveBackup.exists && !liveJournal.exists) {
          try {
            await removeDedicatedXDGRoot(paths);
          } catch {
            fail("Cursor provisioning failed and XDG cleanup could not be completed", "rollback_failed");
          }
        }
      }
      throw error;
    }
  }, options);
}

function healthRequest(runtime, timeoutMs = DEFAULT_HEALTH_TIMEOUT_MS) {
  return new Promise((resolve) => {
    const request = http.request({
      host: "127.0.0.1",
      port: runtime.port,
      path: "/healthz",
      method: "GET",
      headers: { "X-SyncBar-Bridge-Token": runtime.bridgeToken },
    }, (response) => {
      const chunks = [];
      let bytes = 0;
      response.on("data", (chunk) => {
        bytes += chunk.length;
        if (bytes > 64 * 1024) request.destroy();
        else chunks.push(chunk);
      });
      response.on("end", () => {
        try {
          const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
          const healthy = response.statusCode === 200 && body.status === "ok" &&
            body.protocol === "responses" && body.cursor_backend === "sdk" &&
            body.model === runtime.model && Number.isInteger(body.pid);
          resolve({ healthy, pid: healthy ? body.pid : null });
        } catch {
          resolve({ healthy: false, pid: null });
        }
      });
    });
    request.setTimeout(timeoutMs, () => request.destroy());
    request.on("error", () => resolve({ healthy: false, pid: null }));
    request.end();
  });
}

export async function bridgeHealth(options = {}) {
  const runtime = options.runtime ?? await readRuntime(options);
  return healthRequest(runtime, options.timeoutMs);
}

function detachedBridgeEnvironment(runtime, base = process.env) {
  const environment = {
    ...cursorRuntimeEnvironment(runtime, base),
    SYNCBAR_CURSOR_BRIDGE_TOKEN: runtime.bridgeToken,
    SYNCBAR_CURSOR_MODELS_JSON: JSON.stringify(runtime.models),
    SYNCBAR_CURSOR_MODEL_PARAMETERS_JSON: JSON.stringify(runtime.modelParameters),
    // Cursor's Linux terminal sandbox requires host AppArmor support that is
    // absent on several SSH replicas. The remote bridge still enforces the
    // isolated empty workspace, ask mode, deny-all permissions, no MCP, and
    // fail-closed native-tool event handling.
    SYNCBAR_CURSOR_SANDBOX_MODE: "disabled",
    SYNCBAR_CURSOR_BACKEND: "sdk",
  };
  if (runtime.modelRoutes && Object.keys(runtime.modelRoutes).length > 0) {
    environment.SYNCBAR_CURSOR_MODEL_ROUTES_JSON = JSON.stringify(runtime.modelRoutes);
  }
  if (runtime.nativeModels?.length > 0) {
    environment.SYNCBAR_NATIVE_MODELS_JSON = JSON.stringify(runtime.nativeModels);
    environment.SYNCBAR_CODEX_AUTH_FILE = path.join(runtime.home, ".codex", "auth.json");
  }
  return environment;
}

export async function ensureDetachedBridge(options = {}) {
  const runtime = options.runtime ?? await readRuntime(options);
  const existing = await healthRequest(runtime, options.healthTimeoutMs);
  if (existing.healthy) return existing;

  let launchFailed = false;
  const child = spawn(runtime.nodePath, [
    runtime.bridgePath,
    "--host", "127.0.0.1",
    "--port", String(runtime.port),
    "--agent", runtime.agentPath,
    "--model", runtime.model,
    "--workspace", runtime.workspace,
  ], {
    cwd: runtime.home,
    env: detachedBridgeEnvironment(runtime, options.env ?? process.env),
    detached: true,
    stdio: "ignore",
  });
  child.once("error", () => { launchFailed = true; });
  child.unref();

  const deadline = Date.now() + (options.startTimeoutMs ?? DEFAULT_START_TIMEOUT_MS);
  while (Date.now() < deadline) {
    await sleep(100);
    if (launchFailed) fail("Cursor bridge failed to start", "bridge_start_failed");
    const status = await healthRequest(runtime, options.healthTimeoutMs);
    if (status.healthy) return status;
  }
  fail("Cursor bridge did not become healthy", "bridge_start_failed");
}

export async function providerAuth(options = {}) {
  const paths = options.paths ?? managerPaths(options);
  return withManagerLock(paths, async () => {
    const runtime = await readRuntime({ ...options, paths });
    await ensureDetachedBridge({ ...options, paths, runtime });
    return runtime.bridgeToken;
  }, options);
}

async function dedicatedXDGRootExists(paths) {
  if (path.dirname(paths.xdgRoot) !== paths.stateRoot ||
      path.basename(paths.xdgRoot) !== "cursor-remote-xdg") {
    fail("The dedicated Cursor XDG path is unsafe", "unsafe_path");
  }
  let info;
  try {
    info = await lstat(paths.xdgRoot);
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
  if (!info.isDirectory() || info.isSymbolicLink() || !ownedByCurrentUser(info) ||
      (info.mode & 0o022) !== 0) {
    fail("The dedicated Cursor XDG directory is unsafe", "unsafe_path");
  }
  return true;
}

async function removeDedicatedXDGRoot(paths) {
  if (!await dedicatedXDGRootExists(paths)) return;
  await rm(paths.xdgRoot, { recursive: true, force: false });
}

function processIsAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error?.code === "ESRCH") return false;
    fail("Cursor bridge process ownership could not be verified", "bridge_stop_failed");
  }
}

async function stopHealthyBridge(runtime, options = {}) {
  const health = await healthRequest(runtime, options.healthTimeoutMs);
  if (!health.healthy) {
    if (options.required) {
      fail("Cursor bridge ownership could not be authenticated before stop", "bridge_stop_failed");
    }
    return false;
  }
  const pid = health.pid;
  if (!Number.isSafeInteger(pid) || pid <= 1 || pid === process.pid) {
    fail("Cursor bridge returned an unsafe process id", "bridge_stop_failed");
  }
  if (options.expectedPID !== undefined && pid !== options.expectedPID) {
    fail("Cursor bridge process changed before stop", "bridge_stop_failed");
  }
  try {
    process.kill(pid, "SIGTERM");
  } catch (error) {
    if (error?.code === "ESRCH") return false;
    fail("Cursor bridge could not be stopped", "bridge_stop_failed");
  }
  for (let attempt = 0; attempt < 30; attempt += 1) {
    if (!processIsAlive(pid)) return true;
    await sleep(50);
  }
  fail("Cursor bridge did not stop after SIGTERM", "bridge_stop_failed");
}

export async function deprovision(options = {}) {
  const paths = options.paths ?? managerPaths(options);
  return withManagerLock(paths, async () => {
    const configSnapshot = await safeSnapshot(paths.config, { privateFile: false });
    const runtimeSnapshot = await safeSnapshot(paths.runtime, { privateFile: true, maxBytes: MAX_RUNTIME_BYTES });
    const backupSnapshot = await safeSnapshot(paths.backup, { privateFile: true, maxBytes: MAX_PROVISION_BYTES });
    const catalogSnapshot = await safeSnapshot(paths.catalog, {
      privateFile: true,
      maxBytes: MAX_CATALOG_BYTES,
    });
    const backup = decodeBackup(backupSnapshot);
    if (!backup) {
      if (!runtimeSnapshot.exists) {
        if (configSnapshot.data.includes(Buffer.from(MARKER_BEGIN)) ||
            configSnapshot.data.includes(Buffer.from(MARKER_END))) {
          fail("Managed Cursor config has no trusted backup", "missing_backup");
        }
        await removeDedicatedXDGRoot(paths);
        return { provisioned: false };
      }
      fail("Cursor runtime has no trusted config backup", "missing_backup");
    }
    if (!runtimeSnapshot.exists || runtimeSnapshot.hash !== backup.runtimeSHA256) {
      fail("Cursor runtime changed after provisioning", "cas_mismatch");
    }
    if (runtimeSnapshot.mode !== 0o600) {
      fail("Cursor remote runtime must have mode 0600", "unsafe_permissions");
    }
    await dedicatedXDGRootExists(paths);
    const runtime = runtimeFromDisk(decodeJSON(runtimeSnapshot.data, "Cursor remote runtime"));
    if (runtime.catalogPath) {
      if (runtime.catalogPath !== paths.catalog || !catalogSnapshot.exists ||
          backup.schemaVersion !== 3 || catalogSnapshot.hash !== backup.installedCatalogSHA256 ||
          catalogSnapshot.mode !== 0o600) {
        fail("Cursor model catalog changed after provisioning", "cas_mismatch");
      }
    } else if (catalogSnapshot.exists) {
      fail("An unmanaged Cursor model catalog already exists", "catalog_collision");
    }
    const managedState = managedConfigState(configSnapshot, backup, runtime, options);
    const original = backup.originalExisted || managedState.baseData.length > 0
      ? candidate(managedState.baseData, backup.originalMode)
      : absentCandidate();
    const originalCatalog = backup.originalCatalogExisted
      ? candidate(backup.originalCatalogData, backup.originalCatalogMode)
      : absentCandidate();
    // Never report deprovisioning success while a credential-bearing bridge
    // can remain on the managed port. Start the trusted runtime when idle so
    // ownership is authenticated; an unknown port occupant fails before the
    // transaction journal or managed files are changed.
    const bridgeHealth = await ensureDetachedBridge({ ...options, paths, runtime });
    const bridgeExpected = true;
    const files = {
      runtime: { old: runtimeSnapshot, new: absentCandidate() },
      catalog: { old: catalogSnapshot, new: originalCatalog },
      config: { old: configSnapshot, new: original },
      backup: { old: backupSnapshot, new: absentCandidate() },
    };
    let journal = await writeTransactionJournal(paths, "deprovision", files, {
      oldBridgeExpected: bridgeExpected,
      newBridgeExpected: false,
    });
    let filesCommitted = false;
    try {
      crashAfter(options, "deprovision", "after-journal");
      await stopHealthyBridge(runtime, {
        required: true,
        expectedPID: bridgeHealth.pid,
        healthTimeoutMs: options.healthTimeoutMs,
      });
      crashAfter(options, "deprovision", "after-stop-old");
      await atomicCompareAndSwap(paths.config, configSnapshot, files.config.new);
      crashAfter(options, "deprovision", "after-config-commit");
      await atomicCompareAndSwap(paths.catalog, catalogSnapshot, files.catalog.new);
      crashAfter(options, "deprovision", "after-catalog-commit");
      await atomicCompareAndSwap(paths.runtime, runtimeSnapshot, files.runtime.new);
      crashAfter(options, "deprovision", "after-runtime-commit");
      await atomicCompareAndSwap(paths.backup, backupSnapshot, files.backup.new);
      filesCommitted = true;
      crashAfter(options, "deprovision", "after-backup-commit");
      await removeDedicatedXDGRoot(paths);
      crashAfter(options, "deprovision", "after-xdg-remove");
      await clearTransactionJournal(paths, journal);
      journal = null;
    } catch (error) {
      if (journal && !filesCommitted) {
        try {
          await rollbackTransaction(paths, journal, options);
          journal = null;
        } catch {
          fail("Cursor deprovision rollback could not be completed", "rollback_failed");
        }
      }
      throw error;
    }

    return { provisioned: false };
  }, options);
}

export async function show(options = {}) {
  const paths = options.paths ?? managerPaths(options);
  const runtimeSnapshot = await safeSnapshot(paths.runtime, { privateFile: true, maxBytes: MAX_RUNTIME_BYTES });
  if (!runtimeSnapshot.exists) return { provisioned: false, healthy: false };
  if (runtimeSnapshot.mode !== 0o600) fail("Cursor remote runtime must have mode 0600", "unsafe_permissions");
  const runtime = runtimeFromDisk(decodeJSON(runtimeSnapshot.data, "Cursor remote runtime"));
  const health = await healthRequest(runtime, options.timeoutMs);
  return {
    provisioned: true,
    healthy: health.healthy,
    pid: health.pid,
    model: runtime.model,
    codexModel: runtime.codexModel,
    port: runtime.port,
    models: runtime.models,
    modelParameters: runtime.modelParameters,
    modelRoutes: runtime.modelRoutes,
    nativeModels: runtime.nativeModels,
    agentPath: runtime.agentPath,
  };
}

async function readStdinJSON() {
  const chunks = [];
  let bytes = 0;
  for await (const chunk of process.stdin) {
    bytes += chunk.length;
    if (bytes > MAX_RUNTIME_BYTES) fail("provision input is too large", "input_too_large");
    chunks.push(chunk);
  }
  return decodeJSON(Buffer.concat(chunks), "provision input");
}

function safeResult(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

export async function runCLI(argv = process.argv.slice(2), options = {}) {
  if (argv.length !== 1) fail("Usage: cursor-remote-manager.mjs <provision|auth|deprovision|show|health>", "invalid_command");
  switch (argv[0]) {
  case "provision":
    safeResult(await provision(await readStdinJSON(), options));
    return;
  case "auth":
    // This is the one intentional secret output: Codex provider auth commands
    // require the bridge bearer token, and stdout is their documented transport.
    process.stdout.write(`${await providerAuth(options)}\n`);
    return;
  case "deprovision":
    safeResult(await deprovision(options));
    return;
  case "show":
    safeResult(await show(options));
    return;
  case "health": {
    const health = await bridgeHealth(options).catch(() => ({ healthy: false, pid: null }));
    safeResult(health);
    if (!health.healthy) process.exitCode = 1;
    return;
  }
  default:
    fail("Unknown cursor remote manager command", "invalid_command");
  }
}

const invokedDirectly = process.argv[1] && path.resolve(process.argv[1]) === path.resolve(thisFile);
if (invokedDirectly) {
  runCLI().catch((error) => {
    const safeError = error instanceof RemoteManagerError
      ? error
      : new RemoteManagerError("Cursor remote manager failed");
    process.stderr.write(`cursor-remote-manager: ${safeError.message}\n`);
    process.exitCode = 1;
  });
}
