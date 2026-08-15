#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import {
  chmod,
  mkdir,
  mkdtemp,
  readFile,
  realpath,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import test from "node:test";
import { pathToFileURL } from "node:url";

import {
  MARKER_BEGIN,
  MARKER_END,
  MAX_PROVISION_BYTES,
  RemoteManagerError,
  bridgeHealth,
  deprovision,
  managerPaths,
  patchCodexConfig,
  providerAuth,
  provision,
  readRuntime,
  show,
  validateProvisionInput,
} from "../Support/cursor-remote-manager.mjs";

const managerPath = path.resolve(
  path.dirname(new URL(import.meta.url).pathname),
  "../Support/cursor-remote-manager.mjs",
);
const gptSwitchPath = path.resolve(
  path.dirname(new URL(import.meta.url).pathname),
  "../Support/gpt-switch",
);
const productionBridgePath = path.resolve(
  path.dirname(new URL(import.meta.url).pathname),
  "../Support/cursor-codex-bridge.mjs",
);
const roots = new Set();
const detachedPIDs = new Set();

async function removeFixtureTree(root, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      await rm(root, { recursive: true, force: true });
      return;
    } catch (error) {
      if (error?.code === "ENOENT") return;
      if (!["EBUSY", "EEXIST", "ENOTEMPTY"].includes(error?.code) || Date.now() >= deadline) {
        throw error;
      }
      // Bundled Codex may finish an asynchronous plugin-cache rename just
      // after `codex exec` exits. Retry only the disposable fixture teardown.
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }
}

test.afterEach(async () => {
  for (const root of roots) {
    try {
      const launches = (await readFile(path.join(root, "bridge-launches.jsonl"), "utf8"))
        .trim().split("\n").filter(Boolean).map((line) => JSON.parse(line));
      for (const launch of launches) {
        if (Number.isSafeInteger(launch.pid)) detachedPIDs.add(launch.pid);
      }
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
  }
  const stoppingPIDs = [...detachedPIDs];
  for (const pid of stoppingPIDs) {
    try { process.kill(pid, "SIGTERM"); } catch {}
  }
  for (const pid of stoppingPIDs) {
    if (pidIsAlive(pid)) await waitForPIDExit(pid);
  }
  detachedPIDs.clear();
  for (const root of roots) await removeFixtureTree(root);
  roots.clear();
});

const allocatedTestPorts = new Set();

async function allocateLoopbackPort() {
  for (;;) {
    const server = net.createServer();
    const port = await new Promise((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", () => resolve(server.address().port));
    });
    await new Promise((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
    if (!allocatedTestPorts.has(port)) {
      allocatedTestPorts.add(port);
      return port;
    }
  }
}

async function makeFixture(options = {}) {
  const home = await mkdtemp(path.join(os.tmpdir(), "cursor-remote-manager-test-"));
  roots.add(home);
  const agentPath = path.join(home, "fake-agent.mjs");
  const bridgePath = options.productionBridge
    ? productionBridgePath
    : path.join(home, "fake-bridge.mjs");
  const bridgeLaunchLog = path.join(home, "bridge-launches.jsonl");
  const statusHold = options.holdAgentStatus ? path.join(home, "hold-agent-status") : "";
  const statusEntered = options.holdAgentStatus ? path.join(home, "agent-status-entered") : "";
  // A detached bridge from an interrupted prior run must not authenticate as
  // this fixture merely because the OS later reuses the same loopback port.
  const apiKey = `api_${randomBytes(32).toString("hex")}`;
  const bridgeToken = randomBytes(32).toString("hex");
  const models = ["composer-2.5", "gpt-5.6-sol-high-fast"];
  const modelParameters = {
    "composer-2.5": {
      model: "composer-2.5",
      fast: false,
      thinking: false,
    },
    "gpt-5.6-sol-high-fast": {
      model: "gpt-5.6-sol",
      effort: "high",
      fast: true,
      thinking: false,
    },
  };
  const port = options.port ?? await allocateLoopbackPort();
  const original = options.original ?? [
    "# retain this comment",
    'approval_policy = "on-request"',
    'model = "gpt-5.6-sol"',
    'model_provider = "openai"',
    "",
    "[mcp_servers.keep_me]",
    'command = "/usr/bin/true"',
    "",
  ].join(options.newline ?? "\n");

  if (statusHold) await writeFile(statusHold, "hold", { mode: 0o600 });

  await writeFile(agentPath, `#!/usr/bin/env node
import {existsSync, mkdirSync, writeFileSync} from 'node:fs';
import path from 'node:path';
const args = process.argv.slice(2);
const secret = process.env.CURSOR_API_KEY ?? '';
const realHome = ${JSON.stringify(home)};
const isolatedHome = path.join(realHome, '.local', 'share', 'gpt-switch', 'cursor-remote-xdg', 'home');
if (!secret || args.some((value) => value.includes(secret))) process.exit(91);
if (!process.env.XDG_CONFIG_HOME?.endsWith('/cursor-remote-xdg/config')) process.exit(92);
if (process.env.HOME !== isolatedHome) process.exit(94);
if (process.env.AGENT_CLI_CREDENTIAL_STORE !== 'file') process.exit(95);
if (args.length === 1 && args[0] === 'status') {
  writeFileSync(path.join(process.env.HOME, 'credential-store-marker'), secret, {mode:0o600});
  const hold = ${JSON.stringify(statusHold)};
  const entered = ${JSON.stringify(statusEntered)};
  if (entered) writeFileSync(entered, 'entered', { mode: 0o600 });
  while (hold && existsSync(hold)) await new Promise((resolve) => setTimeout(resolve, 25));
  process.stdout.write('{"authenticated":true}\\n');
  process.exit(0);
}
if (args.length === 1 && args[0] === '--list-models') {
  if (existsSync(path.join(realHome, 'fail-model-validation'))) {
    writeFileSync(path.join(process.env.HOME, 'api-key-marker'), secret, {mode:0o600});
    process.stdout.write('different-model - Different Model\\n');
    process.exit(0);
  }
  process.stdout.write('Available models\\n\\ncomposer-2.5 - Composer 2.5\\ngpt-5.6-sol-high-fast - GPT 5.6 Sol High Fast\\n');
  process.exit(0);
}
if (args.includes('-p') && args.includes('--output-format') && args.includes('stream-json')) {
  if (process.env.SYNCBAR_CURSOR_BRIDGE_TOKEN !== undefined) process.exit(96);
  let prompt = '';
  for await (const chunk of process.stdin) prompt += chunk;
  const promptSeen = prompt.includes('SYNCBAR_BACKEND_REQUEST');
  writeFileSync(path.join(process.env.HOME, 'agent-e2e-observation.json'), JSON.stringify({
    apiKeyInArgv: args.some((value) => value.includes(secret)),
    bridgeTokenEnvAbsent: process.env.SYNCBAR_CURSOR_BRIDGE_TOKEN === undefined,
    promptSeen,
    isolatedHome: process.env.HOME === isolatedHome,
    fileCredentialStore: process.env.AGENT_CLI_CREDENTIAL_STORE === 'file',
  }), {mode:0o600});
  if (!promptSeen) process.exit(97);
  process.stdout.write(JSON.stringify({
    type:'result', subtype:'success', result:'bridge-e2e-ok', session_id:'syncbar-e2e',
  }) + '\\n');
  process.exit(0);
}
process.exit(93);
`, { mode: 0o700 });
  await chmod(agentPath, 0o700);

  if (!options.productionBridge) await writeFile(bridgePath, `#!/usr/bin/env node
import http from 'node:http';
import {createHash} from 'node:crypto';
import {appendFileSync, existsSync, readFileSync} from 'node:fs';
import path from 'node:path';
const args = process.argv.slice(2);
const bridgeToken = process.env.SYNCBAR_CURSOR_BRIDGE_TOKEN ?? '';
const apiKey = process.env.CURSOR_API_KEY ?? '';
const modelParameters = JSON.parse(process.env.SYNCBAR_CURSOR_MODEL_PARAMETERS_JSON ?? 'null');
if (!bridgeToken || !apiKey || args.some((value) => value.includes(bridgeToken) || value.includes(apiKey))) process.exit(81);
if (!modelParameters || typeof modelParameters !== 'object') process.exit(85);
if (!process.env.HOME?.endsWith('/cursor-remote-xdg/home')) process.exit(82);
if (process.env.AGENT_CLI_CREDENTIAL_STORE !== 'file') process.exit(83);
const value = (name) => args[args.indexOf(name) + 1];
const port = Number(value('--port'));
const model = value('--model');
const realHome = ${JSON.stringify(home)};
const launchLog = path.join(realHome, 'bridge-launches.jsonl');
const secretFingerprint = createHash('sha256').update(apiKey).digest('hex');
const generation = existsSync(launchLog)
  ? readFileSync(launchLog, 'utf8').trim().split('\\n').filter(Boolean).length + 1
  : 1;
appendFileSync(launchLog, JSON.stringify({generation, pid:process.pid, model, modelParameters, secretFingerprint}) + '\\n', {mode:0o600});
if (model === 'gpt-5.6-sol-high-fast' && existsSync(path.join(realHome, 'fail-new-bridge'))) process.exit(84);
const server = http.createServer((request, response) => {
  if (request.url !== '/healthz' || request.headers['x-syncbar-bridge-token'] !== bridgeToken) {
    response.writeHead(401, {'content-type':'application/json'});
    response.end('{"error":"unauthorized"}');
    return;
  }
  const respond = () => {
    response.writeHead(200, {'content-type':'application/json'});
    response.end(JSON.stringify({status:'ok', protocol:'responses', model, pid:process.pid, generation, secretFingerprint}));
  };
  if (existsSync(path.join(realHome, 'delay-bridge-health'))) setTimeout(respond, 800);
  else respond();
});
server.listen(port, '127.0.0.1');
process.on('SIGTERM', () => server.close(() => process.exit(0)));
`, { mode: 0o600 });

  if (options.createConfig !== false) {
    await mkdir(path.join(home, ".codex"), { recursive: true, mode: 0o700 });
    await writeFile(path.join(home, ".codex", "config.toml"), original, {
      mode: options.configMode ?? 0o600,
    });
    await chmod(path.join(home, ".codex", "config.toml"), options.configMode ?? 0o600);
  }

  const env = {
    ...process.env,
    HOME: home,
    CURSOR_REMOTE_AGENT_PATH: agentPath,
    CURSOR_REMOTE_BRIDGE_PATH: bridgePath,
    CURSOR_REMOTE_NODE_PATH: process.execPath,
  };
  const input = {
    schemaVersion: 1,
    apiKey,
    model: "composer-2.5",
    port,
    bridgeToken,
    models,
    modelParameters,
  };
  return {
    home, agentPath, bridgePath, apiKey, bridgeToken, models, modelParameters, port, original,
    env, input, statusHold, statusEntered, bridgeLaunchLog,
  };
}

function spawnCaptured(executable, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, {
      cwd: options.cwd,
      env: options.env,
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    let timedOut = false;
    const timer = options.timeoutMs ? setTimeout(() => {
      timedOut = true;
      child.kill("SIGKILL");
    }, options.timeoutMs) : null;
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.on("error", reject);
    child.on("close", (code, signal) => {
      if (timer) clearTimeout(timer);
      resolve({ code, signal, stdout, stderr, timedOut });
    });
    child.stdin.end(options.stdin ?? "");
  });
}

function spawnManager(command, fixture, stdin = "") {
  return spawnCaptured(process.execPath, [managerPath, command], {
    cwd: fixture.home,
    env: fixture.env,
    stdin,
  });
}

function fingerprint(secret) {
  return createHash("sha256").update(secret).digest("hex");
}

function pidIsAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    if (error?.code === "ESRCH") return false;
    throw error;
  }
}

async function waitForPIDExit(pid, timeoutMs = 3_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (!pidIsAlive(pid)) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  assert.fail(`timed out waiting for pid ${pid} to exit`);
}

async function bundledCodexPath() {
  const explicit = process.env.CURSOR_REMOTE_CODEX_PATH;
  const candidates = explicit ? [explicit] : [
    "/Applications/ChatGPT.app/Contents/Resources/codex",
    "/Applications/Codex.app/Contents/Resources/codex",
  ];
  for (const candidate of candidates) {
    try {
      const resolved = await realpath(candidate);
      const info = await stat(resolved);
      if (!info.isFile() || (info.mode & 0o111) === 0) {
        throw new Error("Codex candidate is not an executable regular file");
      }
      return resolved;
    } catch (error) {
      if (explicit) throw error;
      if (error?.code !== "ENOENT" && error?.code !== "ENOTDIR") throw error;
    }
  }
  return null;
}

async function bridgeLaunches(fixture, minimum = 1, timeoutMs = 3_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      const launches = (await readFile(fixture.bridgeLaunchLog, "utf8"))
        .trim()
        .split("\n")
        .filter(Boolean)
        .map((line) => JSON.parse(line));
      if (launches.length >= minimum || Date.now() >= deadline) return launches;
    } catch (error) {
      if (error?.code !== "ENOENT" || Date.now() >= deadline) throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

function agentMessageText(item) {
  if (typeof item?.text === "string") return item.text;
  if (!Array.isArray(item?.content)) return "";
  return item.content.map((part) => {
    if (typeof part === "string") return part;
    if (typeof part?.text === "string") return part.text;
    if (typeof part?.output_text === "string") return part.output_text;
    return "";
  }).join("");
}

function spawnCrashable(operation, fixture, stage, input = null) {
  const managerURL = pathToFileURL(managerPath).href;
  const script = `
import { ${operation} } from ${JSON.stringify(managerURL)};
const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const options = {
  home: process.env.HOME,
  env: process.env,
  crashAfter: ${JSON.stringify(stage)},
  healthTimeoutMs: 100,
  startTimeoutMs: 1500,
};
const input = chunks.length ? JSON.parse(Buffer.concat(chunks).toString('utf8')) : undefined;
await ${operation}(input === undefined ? options : input, input === undefined ? undefined : options);
`;
  return spawnCaptured(process.execPath, ["--input-type=module", "--eval", script], {
    cwd: fixture.home,
    env: fixture.env,
    stdin: input === null ? "" : JSON.stringify(input),
  });
}

async function waitForFile(file, timeoutMs = 5_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await readFile(file);
      return await stat(file);
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  assert.fail(`timed out waiting for ${file}`);
}

test("a live manager lock is not stolen and SIGKILL releases it for the next operation", async () => {
  const fixture = await makeFixture({ holdAgentStatus: true });
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  const holder = spawn(process.execPath, [managerPath, "provision"], {
    cwd: fixture.home,
    env: fixture.env,
    stdio: ["pipe", "pipe", "pipe"],
  });
  holder.stdout.resume();
  holder.stderr.resume();
  holder.stdin.end(JSON.stringify(fixture.input));

  try {
    await waitForFile(fixture.statusEntered);
    const lockInfo = await stat(paths.lock);
    assert.equal(lockInfo.isFile(), true);
    assert.equal(lockInfo.mode & 0o777, 0o600);

    const contender = await spawnManager("provision", fixture, JSON.stringify(fixture.input));
    assert.equal(contender.code, 1);
    assert.match(contender.stderr, /Another remote manager operation is running/);

    const closed = new Promise((resolve) => {
      holder.once("close", (code, signal) => resolve({ code, signal }));
    });
    holder.kill("SIGKILL");
    assert.deepEqual(await closed, { code: null, signal: "SIGKILL" });
    await rm(fixture.statusHold, { force: true });

    const recovered = await spawnManager("provision", fixture, JSON.stringify(fixture.input));
    assert.equal(recovered.code, 0, recovered.stderr);
    assert.equal(JSON.parse(recovered.stdout).provisioned, true);
  } finally {
    await rm(fixture.statusHold, { force: true });
    try { holder.kill("SIGKILL"); } catch {}
  }
});

test("provision writes a 0600 runtime, validates isolated Cursor CLI, and preserves unrelated TOML", async () => {
  const fixture = await makeFixture();
  const result = await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  const runtimeData = await readFile(paths.runtime);
  const runtime = JSON.parse(runtimeData);
  const config = await readFile(paths.config, "utf8");

  assert.deepEqual(result, {
    provisioned: true,
    model: fixture.input.model,
    port: fixture.port,
    models: fixture.models,
    modelParameters: fixture.modelParameters,
    agentPath: await realpath(fixture.agentPath),
  });
  assert.equal((await stat(paths.runtime)).mode & 0o777, 0o600);
  assert.equal((await stat(paths.backup)).mode & 0o777, 0o600);
  assert.equal((await stat(paths.config)).mode & 0o777, 0o600);
  assert.equal(runtime.apiKey, fixture.apiKey);
  assert.equal(runtime.bridgeToken, fixture.bridgeToken);
  assert.deepEqual(runtime.modelParameters, fixture.modelParameters);
  assert.match(runtime.cursorHome, /cursor-remote-xdg\/home$/);
  assert.match(runtime.xdgConfig, /cursor-remote-xdg\/config$/);
  assert.equal((await stat(paths.cursorHome)).mode & 0o777, 0o700);
  assert.equal(
    await readFile(path.join(paths.cursorHome, "credential-store-marker"), "utf8"),
    fixture.apiKey,
  );

  assert.match(config, /# retain this comment/);
  assert.match(config, /approval_policy = "on-request"/);
  assert.match(config, /\[mcp_servers\.keep_me\]/);
  assert.match(config, /model = "composer-2\.5"/);
  assert.match(config, /model_provider = "syncbar_cursor_bridge"/);
  assert.match(config, new RegExp(MARKER_BEGIN));
  assert.match(config, new RegExp(MARKER_END));
  assert.match(config, /auth = \{ command = .*cursor-remote-manager\.mjs.*"auth"/);
  assert.doesNotMatch(config, /\bcwd\s*=/);
  assert.doesNotMatch(config, /requires_openai_auth/);
  assert.doesNotMatch(config, new RegExp(fixture.apiKey));
  assert.doesNotMatch(config, new RegExp(fixture.bridgeToken));
  assert.doesNotMatch(JSON.stringify(result), new RegExp(fixture.apiKey));
  assert.doesNotMatch(JSON.stringify(result), new RegExp(fixture.bridgeToken));
});

test("a fresh validation failure removes API-key XDG residue and leaves config/runtime untouched", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  const configBefore = await readFile(paths.config);
  await writeFile(path.join(fixture.home, "fail-model-validation"), "1", { mode: 0o600 });

  await assert.rejects(
    provision(fixture.input, { home: fixture.home, env: fixture.env }),
    (error) => error instanceof RemoteManagerError && error.code === "cursor_model_mismatch",
  );
  assert.deepEqual(await readFile(paths.config), configBefore);
  await assert.rejects(stat(paths.xdgRoot), { code: "ENOENT" });
  await assert.rejects(stat(paths.runtime), { code: "ENOENT" });
  await assert.rejects(stat(paths.backup), { code: "ENOENT" });
});

test("a failed reprovision preserves the previous runtime, backup, config, and XDG auth", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const configBefore = await readFile(paths.config);
  const runtimeBefore = await readFile(paths.runtime);
  const backupBefore = await readFile(paths.backup);
  await writeFile(path.join(fixture.home, "fail-model-validation"), "1", { mode: 0o600 });

  await assert.rejects(
    provision(fixture.input, { home: fixture.home, env: fixture.env }),
    (error) => error instanceof RemoteManagerError && error.code === "cursor_model_mismatch",
  );
  assert.deepEqual(await readFile(paths.config), configBefore);
  assert.deepEqual(await readFile(paths.runtime), runtimeBefore);
  assert.deepEqual(await readFile(paths.backup), backupBefore);
  assert.equal(
    await readFile(path.join(paths.cursorHome, "api-key-marker"), "utf8"),
    fixture.apiKey,
  );
});

test("provision crash recovery rolls back before the first CAS and rolls forward every committed stage", async (context) => {
  for (const stage of ["after-journal", "after-runtime-commit", "after-config-commit", "after-backup-commit"]) {
    await context.test(stage, async () => {
      const fixture = await makeFixture();
      const paths = managerPaths({ home: fixture.home, env: fixture.env });
      const crashed = await spawnCrashable("provision", fixture, `provision:${stage}`, fixture.input);
      assert.equal(crashed.code, 86, crashed.stderr);
      assert.equal((await stat(paths.journal)).mode & 0o777, 0o600);

      if (stage === "after-journal") {
        await assert.rejects(
          providerAuth({ home: fixture.home, env: fixture.env }),
          (error) => error instanceof RemoteManagerError && error.code === "not_provisioned",
        );
        assert.equal(await readFile(paths.config, "utf8"), fixture.original);
        await assert.rejects(stat(paths.runtime), { code: "ENOENT" });
        await assert.rejects(stat(paths.backup), { code: "ENOENT" });
        await assert.rejects(stat(paths.xdgRoot), { code: "ENOENT" });
      } else {
        assert.equal(
          await providerAuth({ home: fixture.home, env: fixture.env }),
          fixture.bridgeToken,
        );
        const health = await bridgeHealth({ home: fixture.home, env: fixture.env });
        assert.equal(health.healthy, true);
        detachedPIDs.add(health.pid);
        assert.equal((await stat(paths.runtime)).mode & 0o777, 0o600);
        assert.equal((await stat(paths.backup)).mode & 0o777, 0o600);
      }
      await assert.rejects(stat(paths.journal), { code: "ENOENT" });
    });
  }
});

test("pending transaction recovery fails closed when a file matches neither journal snapshot", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  const crashed = await spawnCrashable(
    "provision",
    fixture,
    "provision:after-runtime-commit",
    fixture.input,
  );
  assert.equal(crashed.code, 86, crashed.stderr);
  const drifted = Buffer.from(`${fixture.original}external_drift = true\n`, "utf8");
  await writeFile(paths.config, drifted, { mode: 0o600 });

  await assert.rejects(
    providerAuth({ home: fixture.home, env: fixture.env }),
    (error) => error instanceof RemoteManagerError && error.code === "transaction_drift",
  );
  assert.deepEqual(await readFile(paths.config), drifted);
  assert.equal((await stat(paths.runtime)).mode & 0o777, 0o600);
  await assert.rejects(stat(paths.backup), { code: "ENOENT" });
  assert.equal((await stat(paths.journal)).mode & 0o777, 0o600);
});

test("healthy reprovision replaces the bridge generation and API-key environment", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  await providerAuth({ home: fixture.home, env: fixture.env });
  const oldHealth = await bridgeHealth({ home: fixture.home, env: fixture.env });
  assert.equal(oldHealth.healthy, true);
  detachedPIDs.add(oldHealth.pid);

  const newAPIKey = `api_${"c".repeat(60)}`;
  const newInput = {
    ...fixture.input,
    apiKey: newAPIKey,
    bridgeToken: "d".repeat(64),
    model: "gpt-5.6-sol-high-fast",
  };
  await provision(newInput, {
    home: fixture.home,
    env: fixture.env,
    healthTimeoutMs: 100,
    startTimeoutMs: 2_000,
  });
  const newRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
  const newHealth = await bridgeHealth({ runtime: newRuntime });
  assert.equal(newHealth.healthy, true);
  assert.notEqual(newHealth.pid, oldHealth.pid);
  detachedPIDs.delete(oldHealth.pid);
  detachedPIDs.add(newHealth.pid);

  const launches = await bridgeLaunches(fixture, 2);
  assert.equal(launches.length, 2);
  assert.deepEqual(launches.map(({ generation, model, secretFingerprint }) => ({
    generation, model, secretFingerprint,
  })), [
    { generation: 1, model: fixture.input.model, secretFingerprint: fingerprint(fixture.apiKey) },
    { generation: 2, model: newInput.model, secretFingerprint: fingerprint(newAPIKey) },
  ]);
  const serialized = JSON.stringify(launches);
  assert.doesNotMatch(serialized, new RegExp(fixture.apiKey));
  assert.doesNotMatch(serialized, new RegExp(newAPIKey));
  await assert.rejects(stat(paths.journal), { code: "ENOENT" });
});

test("managed reprovision rotates the API key with the same bridge identity", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const oldHealth = await bridgeHealth({ home: fixture.home, env: fixture.env });
  assert.equal(oldHealth.healthy, true);
  detachedPIDs.add(oldHealth.pid);

  const newAPIKey = `api_${"6".repeat(60)}`;
  const newInput = { ...fixture.input, apiKey: newAPIKey };
  await provision(newInput, {
    home: fixture.home,
    env: fixture.env,
    healthTimeoutMs: 100,
    startTimeoutMs: 2_000,
  });

  const runtime = await readRuntime({ home: fixture.home, env: fixture.env });
  const health = await bridgeHealth({ runtime });
  assert.equal(health.healthy, true);
  assert.notEqual(health.pid, oldHealth.pid);
  detachedPIDs.delete(oldHealth.pid);
  detachedPIDs.add(health.pid);
  const launches = await bridgeLaunches(fixture, 2);
  assert.deepEqual(launches.map(({ generation, model, secretFingerprint }) => ({
    generation, model, secretFingerprint,
  })), [
    { generation: 1, model: fixture.input.model, secretFingerprint: fingerprint(fixture.apiKey) },
    { generation: 2, model: fixture.input.model, secretFingerprint: fingerprint(newAPIKey) },
  ]);
  assert.notEqual(launches[0].pid, launches[1].pid);
  assert.equal(pidIsAlive(launches[0].pid), false);
  await assert.rejects(stat(paths.journal), { code: "ENOENT" });
});

test("idle managed bridges are established before reprovision and deprovision", async (context) => {
  for (const operation of ["reprovision", "deprovision"]) {
    await context.test(operation, async () => {
      const fixture = await makeFixture();
      const paths = managerPaths({ home: fixture.home, env: fixture.env });
      await provision(fixture.input, { home: fixture.home, env: fixture.env });
      const initialRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
      const initialHealth = await bridgeHealth({ runtime: initialRuntime });
      assert.equal(initialHealth.healthy, true);
      process.kill(initialHealth.pid, "SIGTERM");
      await waitForPIDExit(initialHealth.pid);
      assert.equal((await bridgeHealth({ runtime: initialRuntime })).healthy, false);

      if (operation === "reprovision") {
        const newAPIKey = `api_${"5".repeat(60)}`;
        await provision({ ...fixture.input, apiKey: newAPIKey }, {
          home: fixture.home,
          env: fixture.env,
          healthTimeoutMs: 100,
          startTimeoutMs: 2_000,
        });
        const newRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
        const newHealth = await bridgeHealth({ runtime: newRuntime });
        assert.equal(newHealth.healthy, true);
        detachedPIDs.add(newHealth.pid);
        const launches = await bridgeLaunches(fixture, 3);
        assert.deepEqual(launches.map(({ generation, secretFingerprint }) => ({
          generation, secretFingerprint,
        })), [
          { generation: 1, secretFingerprint: fingerprint(fixture.apiKey) },
          { generation: 2, secretFingerprint: fingerprint(fixture.apiKey) },
          { generation: 3, secretFingerprint: fingerprint(newAPIKey) },
        ]);
        assert.equal(pidIsAlive(launches[1].pid), false);
      } else {
        assert.deepEqual(
          await deprovision({ home: fixture.home, env: fixture.env }),
          { provisioned: false },
        );
        const launches = await bridgeLaunches(fixture, 2);
        assert.equal(launches.length, 2);
        assert.equal(pidIsAlive(launches[1].pid), false);
        assert.equal((await bridgeHealth({ runtime: initialRuntime })).healthy, false);
        await assert.rejects(stat(paths.runtime), { code: "ENOENT" });
        await assert.rejects(stat(paths.backup), { code: "ENOENT" });
      }
      await assert.rejects(stat(paths.journal), { code: "ENOENT" });
    });
  }
});

test("managed operations fail closed when a live bridge cannot complete health checks", async (context) => {
  for (const operation of ["reprovision", "deprovision"]) {
    await context.test(operation, async () => {
      const fixture = await makeFixture();
      const paths = managerPaths({ home: fixture.home, env: fixture.env });
      await provision(fixture.input, { home: fixture.home, env: fixture.env });
      await providerAuth({ home: fixture.home, env: fixture.env });
      const oldRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
      const oldHealth = await bridgeHealth({ runtime: oldRuntime });
      assert.equal(oldHealth.healthy, true);
      detachedPIDs.add(oldHealth.pid);
      const before = {
        config: await readFile(paths.config),
        runtime: await readFile(paths.runtime),
        backup: await readFile(paths.backup),
      };
      const delayMarker = path.join(fixture.home, "delay-bridge-health");
      await writeFile(delayMarker, "1", { mode: 0o600 });

      const attempt = operation === "reprovision"
        ? provision({ ...fixture.input, apiKey: `api_${"9".repeat(60)}` }, {
            home: fixture.home,
            env: fixture.env,
            healthTimeoutMs: 25,
            startTimeoutMs: 250,
          })
        : deprovision({
            home: fixture.home,
            env: fixture.env,
            healthTimeoutMs: 25,
            startTimeoutMs: 250,
          });
      await assert.rejects(
        attempt,
        (error) => error instanceof RemoteManagerError && error.code === "bridge_start_failed",
      );

      assert.deepEqual(await readFile(paths.config), before.config);
      assert.deepEqual(await readFile(paths.runtime), before.runtime);
      assert.deepEqual(await readFile(paths.backup), before.backup);
      await assert.rejects(stat(paths.journal), { code: "ENOENT" });
      assert.equal(
        await readFile(path.join(paths.cursorHome, "credential-store-marker"), "utf8"),
        fixture.apiKey,
      );
      await rm(delayMarker, { force: true });
      const recoveredHealth = await bridgeHealth({ runtime: oldRuntime, timeoutMs: 200 });
      assert.equal(recoveredHealth.healthy, true);
      assert.equal(recoveredHealth.pid, oldHealth.pid);
    });
  }
});

test("reprovision crash recovery restores the old bridge after stop and keeps the new bridge after commit", async (context) => {
  for (const stage of ["after-stop-old", "after-start-new"]) {
    await context.test(stage, async () => {
      const fixture = await makeFixture();
      const paths = managerPaths({ home: fixture.home, env: fixture.env });
      await provision(fixture.input, { home: fixture.home, env: fixture.env });
      await providerAuth({ home: fixture.home, env: fixture.env });
      const oldRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
      const oldHealth = await bridgeHealth({ runtime: oldRuntime });
      detachedPIDs.add(oldHealth.pid);
      const newKey = `api_${"7".repeat(60)}`;
      const newInput = {
        ...fixture.input,
        apiKey: newKey,
        bridgeToken: "8".repeat(64),
        model: "gpt-5.6-sol-high-fast",
      };

      const crashed = await spawnCrashable(
        "provision",
        fixture,
        `provision:${stage}`,
        newInput,
      );
      assert.equal(crashed.code, 86, crashed.stderr);
      assert.equal((await stat(paths.journal)).mode & 0o777, 0o600);
      const expectedToken = stage === "after-stop-old" ? fixture.bridgeToken : newInput.bridgeToken;
      assert.equal(
        await providerAuth({ home: fixture.home, env: fixture.env }),
        expectedToken,
      );
      const recoveredRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
      const recoveredHealth = await bridgeHealth({ runtime: recoveredRuntime });
      assert.equal(recoveredHealth.healthy, true);
      detachedPIDs.delete(oldHealth.pid);
      detachedPIDs.add(recoveredHealth.pid);
      if (stage === "after-stop-old") {
        assert.equal(recoveredRuntime.model, fixture.input.model);
        assert.equal((await bridgeLaunches(fixture)).at(-1).secretFingerprint, fingerprint(fixture.apiKey));
      } else {
        assert.equal(recoveredRuntime.model, newInput.model);
        assert.equal((await bridgeLaunches(fixture)).at(-1).secretFingerprint, fingerprint(newKey));
      }
      await assert.rejects(stat(paths.journal), { code: "ENOENT" });
    });
  }
});

test("failed bridge restart rolls files back and restores the old runtime bridge", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  await providerAuth({ home: fixture.home, env: fixture.env });
  const oldRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
  const oldHealth = await bridgeHealth({ runtime: oldRuntime });
  detachedPIDs.add(oldHealth.pid);
  const before = {
    config: await readFile(paths.config),
    runtime: await readFile(paths.runtime),
    backup: await readFile(paths.backup),
  };
  await writeFile(path.join(fixture.home, "fail-new-bridge"), "1", { mode: 0o600 });
  const failingKey = `api_${"e".repeat(60)}`;
  const failingInput = {
    ...fixture.input,
    apiKey: failingKey,
    bridgeToken: "f".repeat(64),
    model: "gpt-5.6-sol-high-fast",
  };

  await assert.rejects(
    provision(failingInput, {
      home: fixture.home,
      env: fixture.env,
      healthTimeoutMs: 100,
      startTimeoutMs: 600,
    }),
    (error) => error instanceof RemoteManagerError && error.code === "bridge_start_failed",
  );
  assert.deepEqual(await readFile(paths.config), before.config);
  assert.deepEqual(await readFile(paths.runtime), before.runtime);
  assert.deepEqual(await readFile(paths.backup), before.backup);
  await assert.rejects(stat(paths.journal), { code: "ENOENT" });

  const restored = await bridgeHealth({ runtime: oldRuntime });
  assert.equal(restored.healthy, true);
  assert.notEqual(restored.pid, oldHealth.pid);
  detachedPIDs.delete(oldHealth.pid);
  detachedPIDs.add(restored.pid);
  const launches = await bridgeLaunches(fixture);
  assert.equal(launches.at(-1).model, fixture.input.model);
  assert.equal(launches.at(-1).secretFingerprint, fingerprint(fixture.apiKey));
  assert.ok(launches.some((launch) => launch.secretFingerprint === fingerprint(failingKey)));
  assert.doesNotMatch(JSON.stringify(launches), new RegExp(failingKey));
});

test("deprovision restores the exact original bytes and original safe mode", async () => {
  const original = "# original\r\napproval_policy = \"never\"\r\n\r\n[features]\r\nfoo = true\r\n";
  const fixture = await makeFixture({ original, configMode: 0o644 });
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const runtime = await readRuntime({ home: fixture.home, env: fixture.env });
  const beforeHealth = await bridgeHealth({ runtime });
  assert.equal(beforeHealth.healthy, true);
  detachedPIDs.add(beforeHealth.pid);

  assert.deepEqual(await deprovision({ home: fixture.home, env: fixture.env }), { provisioned: false });
  const launches = await bridgeLaunches(fixture);
  assert.equal(launches.length, 1);
  assert.equal(pidIsAlive(launches[0].pid), false);
  detachedPIDs.delete(beforeHealth.pid);
  assert.equal((await bridgeHealth({ runtime })).healthy, false);
  assert.equal(await readFile(paths.config, "utf8"), original);
  assert.equal((await stat(paths.config)).mode & 0o777, 0o644);
  await assert.rejects(readFile(paths.runtime), { code: "ENOENT" });
  await assert.rejects(readFile(paths.backup), { code: "ENOENT" });
  await assert.rejects(stat(paths.xdgRoot), { code: "ENOENT" });
});

test("deprovision refuses drift without stopping the bridge or removing dedicated XDG auth", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const authResult = await spawnManager("auth", fixture);
  assert.equal(authResult.code, 0, authResult.stderr);
  const beforeHealth = await bridgeHealth({ home: fixture.home, env: fixture.env });
  assert.equal(beforeHealth.healthy, true);
  detachedPIDs.add(beforeHealth.pid);
  const xdgSentinel = path.join(paths.xdgConfig, "keep-on-drift");
  await writeFile(xdgSentinel, "keep", { mode: 0o600 });
  const changed = `${await readFile(paths.config, "utf8")}unrelated_after_provision = true\n`;
  await writeFile(paths.config, changed, { mode: 0o600 });

  await assert.rejects(
    deprovision({ home: fixture.home, env: fixture.env }),
    (error) => error instanceof RemoteManagerError && error.code === "cas_mismatch",
  );
  assert.equal(await readFile(paths.config, "utf8"), changed);
  assert.equal((await stat(paths.runtime)).mode & 0o777, 0o600);
  assert.equal((await stat(paths.backup)).mode & 0o777, 0o600);
  assert.equal(await readFile(xdgSentinel, "utf8"), "keep");
  const afterHealth = await bridgeHealth({ home: fixture.home, env: fixture.env });
  assert.equal(afterHealth.healthy, true);
  assert.equal(afterHealth.pid, beforeHealth.pid);
});

test("deprovision stops an authenticated healthy bridge and removes only the dedicated XDG tree", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const runtime = await readRuntime({ home: fixture.home, env: fixture.env });
  const unrelated = path.join(paths.stateRoot, "unrelated.keep");
  await writeFile(unrelated, "keep", { mode: 0o600 });
  await writeFile(path.join(paths.xdgData, "cursor-auth-state"), "private", { mode: 0o600 });
  const authResult = await spawnManager("auth", fixture);
  assert.equal(authResult.code, 0, authResult.stderr);
  const beforeHealth = await bridgeHealth({ runtime });
  assert.equal(beforeHealth.healthy, true);
  detachedPIDs.add(beforeHealth.pid);

  assert.deepEqual(await deprovision({ home: fixture.home, env: fixture.env }), { provisioned: false });
  const afterHealth = await bridgeHealth({ runtime });
  assert.equal(afterHealth.healthy, false);
  detachedPIDs.delete(beforeHealth.pid);
  assert.equal(await readFile(paths.config, "utf8"), fixture.original);
  assert.equal(await readFile(unrelated, "utf8"), "keep");
  await assert.rejects(stat(paths.xdgRoot), { code: "ENOENT" });
  await assert.rejects(stat(paths.runtime), { code: "ENOENT" });
  await assert.rejects(stat(paths.backup), { code: "ENOENT" });
});

test("deprovision crash recovery handles bridge-stop and every file/XDG stage", async (context) => {
  const stages = [
    "after-journal",
    "after-stop-old",
    "after-config-commit",
    "after-runtime-commit",
    "after-backup-commit",
    "after-xdg-remove",
  ];
  for (const stage of stages) {
    await context.test(stage, async () => {
      const fixture = await makeFixture();
      const paths = managerPaths({ home: fixture.home, env: fixture.env });
      await provision(fixture.input, { home: fixture.home, env: fixture.env });
      await providerAuth({ home: fixture.home, env: fixture.env });
      const oldRuntime = await readRuntime({ home: fixture.home, env: fixture.env });
      const oldHealth = await bridgeHealth({ runtime: oldRuntime });
      assert.equal(oldHealth.healthy, true);
      detachedPIDs.add(oldHealth.pid);

      const crashed = await spawnCrashable(
        "deprovision",
        fixture,
        `deprovision:${stage}`,
      );
      assert.equal(crashed.code, 86, crashed.stderr);
      assert.equal((await stat(paths.journal)).mode & 0o777, 0o600);

      if (["after-journal", "after-stop-old"].includes(stage)) {
        assert.equal(
          await providerAuth({ home: fixture.home, env: fixture.env }),
          fixture.bridgeToken,
        );
        const recovered = await bridgeHealth({ runtime: oldRuntime });
        assert.equal(recovered.healthy, true);
        if (stage === "after-journal") assert.equal(recovered.pid, oldHealth.pid);
        else assert.notEqual(recovered.pid, oldHealth.pid);
        detachedPIDs.delete(oldHealth.pid);
        detachedPIDs.add(recovered.pid);
        assert.equal((await stat(paths.runtime)).mode & 0o777, 0o600);
        assert.equal((await stat(paths.backup)).mode & 0o777, 0o600);
      } else {
        await assert.rejects(
          providerAuth({ home: fixture.home, env: fixture.env }),
          (error) => error instanceof RemoteManagerError && error.code === "not_provisioned",
        );
        detachedPIDs.delete(oldHealth.pid);
        assert.equal(await readFile(paths.config, "utf8"), fixture.original);
        await assert.rejects(stat(paths.runtime), { code: "ENOENT" });
        await assert.rejects(stat(paths.backup), { code: "ENOENT" });
        await assert.rejects(stat(paths.xdgRoot), { code: "ENOENT" });
      }
      await assert.rejects(stat(paths.journal), { code: "ENOENT" });
    });
  }
});

test("a pre-existing provider id or damaged marker is never overwritten", async () => {
  const runtime = {
    model: "composer-2.5",
    port: 32125,
    bridgeToken: "b".repeat(64),
    nodePath: process.execPath,
    managerPath,
    home: os.tmpdir(),
  };
  for (const text of [
    `[model_providers.syncbar_cursor_bridge]\nname = "manual"\n`,
    `${MARKER_BEGIN}\n`,
  ]) {
    assert.throws(
      () => patchCodexConfig(text, runtime),
      (error) => error instanceof RemoteManagerError &&
        ["provider_collision", "managed_marker_exists"].includes(error.code),
    );
  }
});

test("patching a config without a trailing newline does not concatenate assignments", () => {
  const patched = patchCodexConfig("approval_policy = \"never\"", {
    model: "composer-2.5",
    port: 32125,
    bridgeToken: "b".repeat(64),
    nodePath: process.execPath,
    managerPath,
    home: os.tmpdir(),
  });
  assert.match(patched, /approval_policy = "never"\nmodel = "composer-2\.5"\n/);
  assert.doesNotMatch(patched, /never"model/);
});

test("provision input is exact and API keys reject whitespace and control characters", () => {
  const base = {
    schemaVersion: 1,
    apiKey: "a".repeat(16),
    model: "composer-2.5",
    port: 32125,
    bridgeToken: "b".repeat(64),
    models: ["composer-2.5"],
    modelParameters: {
      "composer-2.5": { model: "composer-2.5", fast: false, thinking: false },
    },
  };
  assert.deepEqual(validateProvisionInput(base), base);
  for (const value of [
    { ...base, unknown: true },
    { ...base, apiKey: `short key` },
    { ...base, apiKey: `${"a".repeat(16)}\u0000` },
    { ...base, apiKey: `${"a".repeat(16)}\u200b` },
    { ...base, apiKey: `${"a".repeat(16)}\ud800` },
    { ...base, apiKey: "가".repeat(342) },
  ]) {
    assert.throws(
      () => validateProvisionInput(value),
      (error) => error instanceof RemoteManagerError &&
        ["invalid_input", "invalid_secret"].includes(error.code),
    );
  }

  for (const modelParameters of [
    {},
    { ...base.modelParameters, unknown: { model: "unknown", fast: false, thinking: false } },
    { "composer-2.5": { model: "composer-2.5", fast: false } },
    { "composer-2.5": { model: "composer-2.5", fast: false, thinking: false, unknown: true } },
    { "composer-2.5": { model: "composer-2.5", context: "auto", fast: false, thinking: false } },
    { "composer-2.5": { model: "composer-2.5", effort: "default", fast: false, thinking: false } },
    { "composer-2.5": { model: "composer-2.5", fast: 0, thinking: false } },
  ]) {
    assert.throws(
      () => validateProvisionInput({ ...base, modelParameters }),
      (error) => error instanceof RemoteManagerError && error.code === "invalid_model_parameters",
    );
  }
});

test("the maximum model catalog remains inside the bounded provision payload", () => {
  const models = Array.from({ length: 512 }, (_, index) =>
    `model-${String(index).padStart(3, "0")}-${"x".repeat(118)}`);
  const modelParameters = Object.fromEntries(models.map((model) => [
    model,
    { model, context: "1m", effort: "xhigh", fast: true, thinking: true },
  ]));
  const input = {
    schemaVersion: 1,
    apiKey: "a".repeat(1024),
    model: models[0],
    port: 65535,
    bridgeToken: "b".repeat(64),
    models,
    modelParameters,
  };
  const encoded = Buffer.from(JSON.stringify(input));
  assert.ok(encoded.length > 128 * 1024);
  assert.ok(encoded.length <= MAX_PROVISION_BYTES);
  assert.deepEqual(validateProvisionInput(input), input);
});

test("the provision CLI enforces the exact serialized input boundary", async () => {
  const fixture = await makeFixture();
  const atLimit = `{}${" ".repeat(MAX_PROVISION_BYTES - 2)}`;
  const accepted = await spawnManager("provision", fixture, atLimit);
  assert.equal(accepted.code, 1);
  assert.match(accepted.stderr, /unsupported provision schemaVersion/);
  assert.doesNotMatch(accepted.stderr, /too large/);

  const oversized = `${atLimit} `;
  const rejected = await spawnManager("provision", fixture, oversized);
  assert.equal(rejected.code, 1);
  assert.match(rejected.stderr, /provision input is too large/);
});

test("gpt-switch enforces the same model metadata and payload contract", async () => {
  const source = await readFile(gptSwitchPath, "utf8");
  assert.match(source, /head -c 524289/);
  assert.match(source, /\[ "\$bytes" -le 524288 \]/);
  const marker = `printf '%s' "$payload" | /usr/bin/jq -e '\n`;
  const start = source.indexOf(marker) + marker.length;
  const end = source.indexOf("\n  ' >/dev/null", start);
  assert.ok(start >= marker.length && end > start, "Cursor jq validator must remain extractable");
  const filter = source.slice(start, end);
  const valid = {
    schemaVersion: 1,
    apiKey: "a".repeat(16),
    model: "composer-2.5",
    port: 32125,
    bridgeToken: "b".repeat(64),
    models: ["composer-2.5"],
    modelParameters: {
      "composer-2.5": {
        model: "composer-2.5",
        context: "1m",
        effort: "high",
        fast: false,
        thinking: true,
      },
    },
  };
  const check = (value) => spawnCaptured("/usr/bin/jq", ["-e", filter], {
    stdin: JSON.stringify(value),
  });
  assert.equal((await check(valid)).code, 0);
  assert.notEqual((await check({ ...valid, modelParameters: {} })).code, 0);
  assert.notEqual((await check({
    ...valid,
    modelParameters: {
      "composer-2.5": { ...valid.modelParameters["composer-2.5"], context: "2m" },
    },
  })).code, 0);
  assert.notEqual((await check({
    ...valid,
    modelParameters: {
      "composer-2.5": { ...valid.modelParameters["composer-2.5"], unknown: true },
    },
  })).code, 0);
});

test("show and provision CLI output never disclose stored secrets", async () => {
  const fixture = await makeFixture();
  const provisionResult = await spawnManager("provision", fixture, JSON.stringify(fixture.input));
  assert.equal(provisionResult.code, 0, provisionResult.stderr);
  assert.doesNotMatch(provisionResult.stdout, new RegExp(fixture.apiKey));
  assert.doesNotMatch(provisionResult.stdout, new RegExp(fixture.bridgeToken));
  assert.doesNotMatch(provisionResult.stderr, new RegExp(fixture.apiKey));
  assert.doesNotMatch(provisionResult.stderr, new RegExp(fixture.bridgeToken));
  const launches = await bridgeLaunches(fixture);
  assert.equal(launches.length, 1);
  assert.deepEqual(launches[0].modelParameters, fixture.modelParameters);
  assert.doesNotThrow(() => process.kill(launches[0].pid, 0));

  const showResult = await spawnManager("show", fixture);
  assert.equal(showResult.code, 0, showResult.stderr);
  const status = JSON.parse(showResult.stdout);
  assert.equal(status.provisioned, true);
  assert.equal(status.healthy, true);
  assert.ok(Number.isSafeInteger(status.pid));
  assert.deepEqual(status.modelParameters, fixture.modelParameters);
  detachedPIDs.add(status.pid);
  assert.doesNotMatch(showResult.stdout, new RegExp(fixture.apiKey));
  assert.doesNotMatch(showResult.stdout, new RegExp(fixture.bridgeToken));

  const directStatus = await show({ home: fixture.home, env: fixture.env });
  assert.equal(directStatus.model, fixture.input.model);
  assert.deepEqual(directStatus.modelParameters, fixture.modelParameters);
  assert.equal(Object.hasOwn(directStatus, "apiKey"), false);
  assert.equal(Object.hasOwn(directStatus, "bridgeToken"), false);
});

test("auth starts the bridge detached and writes only the bearer token to stdout", async () => {
  const fixture = await makeFixture();
  await provision(fixture.input, { home: fixture.home, env: fixture.env });

  const authResult = await spawnManager("auth", fixture);
  assert.equal(authResult.code, 0, authResult.stderr);
  assert.equal(authResult.stdout, `${fixture.bridgeToken}\n`);
  assert.equal(authResult.stderr, "");
  assert.doesNotMatch(authResult.stdout, new RegExp(fixture.apiKey));

  const health = await bridgeHealth({ home: fixture.home, env: fixture.env });
  assert.equal(health.healthy, true);
  assert.ok(Number.isInteger(health.pid));
  detachedPIDs.add(health.pid);

  const second = await spawnManager("auth", fixture);
  assert.equal(second.code, 0, second.stderr);
  assert.equal(second.stdout, `${fixture.bridgeToken}\n`);
  const secondHealth = await bridgeHealth({ home: fixture.home, env: fixture.env });
  assert.equal(secondHealth.pid, health.pid, "healthy auth must not launch a second bridge");
});

test("readRuntime rejects widened runtime permissions", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  await chmod(paths.runtime, 0o644);
  await assert.rejects(
    readRuntime({ home: fixture.home, env: fixture.env }),
    (error) => error instanceof RemoteManagerError && error.code === "unsafe_permissions",
  );
});

test("readRuntime migrates legacy model slugs without guessing context", async () => {
  const fixture = await makeFixture();
  const paths = managerPaths({ home: fixture.home, env: fixture.env });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const legacy = JSON.parse(await readFile(paths.runtime, "utf8"));
  delete legacy.modelParameters;
  await writeFile(paths.runtime, `${JSON.stringify(legacy, null, 2)}\n`, { mode: 0o600 });
  await chmod(paths.runtime, 0o600);

  const migrated = await readRuntime({ home: fixture.home, env: fixture.env });
  assert.deepEqual(migrated.modelParameters, {
    "composer-2.5": { model: "composer-2.5", fast: false, thinking: false },
    "gpt-5.6-sol-high-fast": {
      model: "gpt-5.6-sol",
      effort: "high",
      fast: true,
      thinking: false,
    },
  });
  assert.equal(Object.hasOwn(migrated.modelParameters["gpt-5.6-sol-high-fast"], "context"), false);
});

test("provision can use an injected installer URL without putting the API key in installer argv or env", async () => {
  const fixture = await makeFixture();
  await rm(fixture.agentPath, { force: true });
  const installedAgent = path.join(fixture.home, ".local", "bin", "agent");
  const installer = path.join(fixture.home, "fake-installer.sh");
  const agentSource = `#!/usr/bin/env node
import {writeFileSync} from 'node:fs';
import path from 'node:path';
const args = process.argv.slice(2);
if (process.env.CURSOR_API_KEY === undefined) process.exit(72);
if (args.some((value) => value.includes(process.env.CURSOR_API_KEY))) process.exit(74);
if (!process.env.HOME?.endsWith('/cursor-remote-xdg/home')) process.exit(75);
if (process.env.AGENT_CLI_CREDENTIAL_STORE !== 'file') process.exit(76);
if (args[0] === 'status') {
  writeFileSync(path.join(process.env.HOME, 'installed-agent-environment-ok'), 'ok', {mode:0o600});
  process.exit(0);
}
if (args[0] === '--list-models') {
  process.stdout.write('composer-2.5 - Composer 2.5\\ngpt-5.6-sol-high-fast - GPT 5.6 Sol High Fast\\n');
  process.exit(0);
}
process.exit(73);
`;
  const encoded = Buffer.from(agentSource).toString("base64");
  await writeFile(installer, `#!/bin/sh
set -eu
test -z "\${CURSOR_API_KEY:-}"
test -z "\${AGENT_CLI_CREDENTIAL_STORE:-}"
case "$HOME" in *cursor-remote-xdg*) exit 77 ;; esac
printf 'ok' > "$HOME/installer-environment-ok"
mkdir -p "$HOME/.local/bin"
printf '%s' '${encoded}' | base64 -d > "$HOME/.local/bin/agent"
chmod 700 "$HOME/.local/bin/agent"
`, { mode: 0o600 });
  const env = {
    ...fixture.env,
    CURSOR_REMOTE_AGENT_PATH: installedAgent,
    CURSOR_REMOTE_INSTALL_URL: pathToFileURL(installer).href,
  };

  await provision(fixture.input, { home: fixture.home, env });
  assert.equal((await stat(installedAgent)).mode & 0o777, 0o700);
  assert.equal(await readFile(path.join(fixture.home, "installer-environment-ok"), "utf8"), "ok");
  const paths = managerPaths({ home: fixture.home, env });
  assert.equal(
    await readFile(path.join(paths.cursorHome, "installed-agent-environment-ok"), "utf8"),
    "ok",
  );
  const runtime = await readRuntime({ home: fixture.home, env });
  assert.equal(runtime.agentPath, await realpath(installedAgent));
});

test("generated provider and command auth complete a bundled Codex exec through the Responses bridge", async (context) => {
  const codexPath = await bundledCodexPath();
  if (!codexPath) {
    context.skip("no bundled Codex executable is installed");
    return;
  }
  const fixture = await makeFixture({ productionBridge: true });
  await provision(fixture.input, { home: fixture.home, env: fixture.env });
  const runtime = await readRuntime({ home: fixture.home, env: fixture.env });
  const health = await bridgeHealth({ runtime });
  assert.equal(health.healthy, true);
  detachedPIDs.add(health.pid);

  const codexArgs = ["exec", "--ephemeral", "--skip-git-repo-check", "--json", "-"];
  const codexEnvironment = {
    ...fixture.env,
    HOME: fixture.home,
    CODEX_HOME: path.join(fixture.home, ".codex"),
    NO_COLOR: "1",
  };
  delete codexEnvironment.OPENAI_API_KEY;
  delete codexEnvironment.CODEX_API_KEY;
  const result = await spawnCaptured(codexPath, codexArgs, {
    cwd: fixture.home,
    env: codexEnvironment,
    stdin: "Reply with exactly bridge-e2e-ok.\n",
    timeoutMs: 60_000,
  });
  assert.equal(result.timedOut, false);
  assert.equal(result.signal, null);
  assert.equal(result.code, 0, result.stderr);
  const events = result.stdout.trim().split("\n").filter(Boolean).map((line) => JSON.parse(line));
  assert.ok(events.some((event) => event.item?.type === "agent_message" &&
    agentMessageText(event.item).trim() === "bridge-e2e-ok"));
  assert.ok(events.some((event) => event.type === "turn.completed"));
  assert.equal(events.some((event) => ["error", "turn.failed"].includes(event.type)), false);

  const observationPath = path.join(
    managerPaths({ home: fixture.home, env: fixture.env }).cursorHome,
    "agent-e2e-observation.json",
  );
  assert.equal((await waitForFile(observationPath)).mode & 0o777, 0o600);
  assert.deepEqual(JSON.parse(await readFile(observationPath, "utf8")), {
    apiKeyInArgv: false,
    bridgeTokenEnvAbsent: true,
    promptSeen: true,
    isolatedHome: true,
    fileCredentialStore: true,
  });

  for (const output of [result.stdout, result.stderr, JSON.stringify(codexArgs)]) {
    assert.doesNotMatch(output, new RegExp(fixture.apiKey));
    assert.doesNotMatch(output, new RegExp(fixture.bridgeToken));
  }
});
