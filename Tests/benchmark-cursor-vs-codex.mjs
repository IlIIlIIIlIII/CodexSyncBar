#!/usr/bin/env node

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash, randomBytes, randomUUID } from "node:crypto";
import {
  chmod,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  stat,
  writeFile,
} from "node:fs/promises";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repository = path.dirname(here);
const model = process.env.CURSOR_BENCH_MODEL ?? "gpt-5.6-sol-high";
const cursorAgent = process.env.CURSOR_AGENT_PATH ?? path.join(os.homedir(), ".local", "bin", "agent");
const codexCLI = process.env.CODEX_CLI_PATH ?? "/Applications/ChatGPT.app/Contents/Resources/codex";
const bridgePath = process.env.CURSOR_BRIDGE_PATH ?? path.join(repository, "Support", "cursor-codex-bridge.mjs");
const timeoutMs = Number(process.env.CURSOR_BENCH_TIMEOUT_MS ?? 12 * 60 * 1000);
const requestedArms = new Set(
  (process.env.CURSOR_BENCH_ARMS ?? "cursor-direct,codex-cursor")
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean),
);
const requestedFixtures = process.env.CURSOR_BENCH_FIXTURES
  ? new Set(process.env.CURSOR_BENCH_FIXTURES.split(",").map((value) => value.trim()).filter(Boolean))
  : null;
const outputPath = process.env.CURSOR_BENCH_OUTPUT ?? path.join(
  os.tmpdir(),
  `codex-syncbar-cursor-benchmark-${new Date().toISOString().replaceAll(":", "-")}.json`,
);

const prompt = [
  "Implement the task described in README.md.",
  "Work only inside the current workspace, do not use the network, and do not modify README.md or test.mjs.",
  "Inspect the existing implementation, make the smallest complete fix, and run `node test.mjs` before finishing.",
].join("\n");

const fixtures = [
  {
    id: "interval-merge",
    files: {
      "README.md": `# Interval merge\n\nImplement \`mergeIntervals(intervals)\` in \`src/intervals.mjs\`.\n\n- The input is an array of two-number \`[start, end]\` pairs. Every number must be finite and \`start <= end\`; otherwise throw \`TypeError\`.\n- Sort by start and merge overlapping intervals, including intervals whose endpoints are equal.\n- Return newly allocated pairs and never mutate the input array or its pairs.\n- Empty input returns an empty array.\n`,
      "src/intervals.mjs": `export function mergeIntervals(intervals) {\n  if (!Array.isArray(intervals)) throw new TypeError("intervals must be an array");\n  const sorted = intervals.sort((left, right) => left[0] - right[0]);\n  const merged = [];\n  for (const range of sorted) {\n    const [start, end] = range;\n    const previous = merged.at(-1);\n    if (!previous || start > previous[1]) merged.push(range);\n    else previous[1] = Math.min(previous[1], end);\n  }\n  return merged;\n}\n`,
      "test.mjs": `import assert from "node:assert/strict";\nimport { mergeIntervals } from "./src/intervals.mjs";\nassert.deepEqual(mergeIntervals([[3, 4], [1, 2]]), [[1, 2], [3, 4]]);\nconsole.log("visible interval test passed");\n`,
    },
    async score(workspace) {
      const { mergeIntervals } = await freshImport(path.join(workspace, "src", "intervals.mjs"));
      assert.deepEqual(mergeIntervals([]), []);
      assert.deepEqual(mergeIntervals([[1, 4], [2, 7], [9, 10]]), [[1, 7], [9, 10]]);
      assert.deepEqual(mergeIntervals([[1, 2], [2, 3], [-2, -1]]), [[-2, -1], [1, 3]]);
      assert.deepEqual(mergeIntervals([[1, 10], [3, 4], [5, 6]]), [[1, 10]]);
      const input = [[5, 8], [1, 3], [2, 4]];
      const original = structuredClone(input);
      const result = mergeIntervals(input);
      assert.deepEqual(input, original);
      assert.deepEqual(result, [[1, 4], [5, 8]]);
      assert.notEqual(result[0], input[1]);
      assert.ok(result.every((pair) => !input.includes(pair)));
      for (const invalid of [null, [[1]], [[1, 2, 3]], [[2, 1]], [[0, Infinity]], [[0, Number.NaN]], [["0", 1]]]) {
        assert.throws(() => mergeIntervals(invalid), TypeError);
      }
    },
  },
  {
    id: "lru-ttl-cache",
    files: {
      "README.md": `# LRU cache with TTL\n\nComplete the named export \`LRUCache\` in \`src/lru.mjs\`.\n\n- \`new LRUCache(capacity, now = Date.now)\` requires a positive integer capacity and a function clock.\n- \`set(key, value, ttlMs = null)\` inserts or replaces a value. A TTL must be a positive finite number. Replacing a key refreshes recency and replaces its TTL. Return \`this\`.\n- \`get(key)\` returns the value or \`undefined\`; a successful get makes the key most-recently used.\n- \`has(key)\` removes expired entries but does not change recency.\n- \`delete(key)\` returns whether an entry existed. \`size\` excludes expired entries.\n- Entries expire when \`now() >= expiresAt\`. Before capacity eviction, discard expired entries; otherwise evict exactly the least-recently used key.\n`,
      "src/lru.mjs": `export class LRUCache {\n  constructor(capacity, now = Date.now) {\n    this.capacity = capacity;\n    this.now = now;\n    this.entries = new Map();\n  }\n\n  set(key, value) {\n    this.entries.set(key, { value });\n    if (this.entries.size > this.capacity) this.entries.delete(this.entries.keys().next().value);\n  }\n\n  get(key) {\n    return this.entries.get(key)?.value;\n  }\n\n  has(key) {\n    return this.entries.has(key);\n  }\n\n  delete(key) {\n    this.entries.delete(key);\n  }\n\n  get size() {\n    return this.entries.size;\n  }\n}\n`,
      "test.mjs": `import assert from "node:assert/strict";\nimport { LRUCache } from "./src/lru.mjs";\nconst cache = new LRUCache(2);\ncache.set("a", 1).set("b", 2);\nassert.equal(cache.get("a"), 1);\nconsole.log("visible cache test passed");\n`,
    },
    async score(workspace) {
      const { LRUCache } = await freshImport(path.join(workspace, "src", "lru.mjs"));
      assert.throws(() => new LRUCache(0), TypeError);
      assert.throws(() => new LRUCache(1.5), TypeError);
      assert.throws(() => new LRUCache(1, 1), TypeError);
      let now = 100;
      const cache = new LRUCache(2, () => now);
      assert.equal(cache.set("a", 1), cache);
      cache.set("b", 2);
      assert.equal(cache.get("a"), 1);
      cache.set("c", 3);
      assert.equal(cache.has("b"), false);
      assert.equal(cache.has("a"), true);
      assert.equal(cache.has("c"), true);
      cache.set("ttl", 4, 10);
      now = 109;
      assert.equal(cache.get("ttl"), 4);
      now = 110;
      assert.equal(cache.get("ttl"), undefined);
      assert.equal(cache.size, 1);
      cache.set("x", undefined, 5);
      assert.equal(cache.has("x"), true);
      assert.equal(cache.get("x"), undefined);
      cache.set("y", 5);
      assert.equal(cache.has("x"), true);
      assert.equal(cache.has("c"), false);
      cache.set("x", "new", 20);
      now = 125;
      assert.equal(cache.get("x"), "new");
      assert.equal(cache.delete("x"), true);
      assert.equal(cache.delete("x"), false);
      assert.throws(() => cache.set("bad", 1, 0), TypeError);
      assert.throws(() => cache.set("bad", 1, Infinity), TypeError);

      now = 0;
      const hasOrder = new LRUCache(2, () => now);
      hasOrder.set("first", 1).set("second", 2);
      assert.equal(hasOrder.has("first"), true);
      hasOrder.set("third", 3);
      assert.equal(hasOrder.has("first"), false);
      assert.equal(hasOrder.has("second"), true);

      const expiry = new LRUCache(2, () => now);
      expiry.set("expired", 1, 1).set("live", 2);
      now = 1;
      assert.equal(expiry.size, 1);
      expiry.set("next", 3);
      assert.equal(expiry.has("expired"), false);
      assert.equal(expiry.has("live"), true);
      assert.equal(expiry.has("next"), true);
      expiry.set("persistent", 4, 1);
      expiry.set("persistent", 5, null);
      now = 100;
      assert.equal(expiry.get("persistent"), 5);
    },
  },
  {
    id: "dotenv-parser",
    files: {
      "README.md": `# Small dotenv parser\n\nImplement \`parseEnv(text)\` in \`src/env.mjs\`.\n\n- Accept only a string and return a plain object. Ignore blank lines and lines whose first non-space character is \`#\`. An optional \`export \` prefix is allowed.\n- Keys must match \`[A-Za-z_][A-Za-z0-9_]*\`; split on the first \`=\` and ignore surrounding whitespace around the key and separator. Invalid lines throw \`SyntaxError\` whose message includes the one-based line number.\n- Trim unquoted values. In unquoted values, a \`#\` begins a comment only when it is preceded by whitespace.\n- Single-quoted values are literal. Double-quoted values support only \`\\n\`, \`\\r\`, \`\\t\`, \`\\\\\`, and \`\\\"\`. After a closing quote, only whitespace or a comment is allowed.\n- Duplicate keys use the last value. CRLF and a final line without a newline must work.\n`,
      "src/env.mjs": `export function parseEnv(text) {\n  const result = {};\n  for (const line of text.split("\\n")) {\n    const trimmed = line.trim();\n    if (!trimmed || trimmed.startsWith("#")) continue;\n    const [key, value = ""] = trimmed.split("=");\n    result[key] = value.trim().replace(/^['\"]|['\"]$/g, "");\n  }\n  return result;\n}\n`,
      "test.mjs": `import assert from "node:assert/strict";\nimport { parseEnv } from "./src/env.mjs";\nassert.deepEqual(parseEnv("A=1\\nB=two"), { A: "1", B: "two" });\nconsole.log("visible env test passed");\n`,
    },
    async score(workspace) {
      const { parseEnv } = await freshImport(path.join(workspace, "src", "env.mjs"));
      const basic = parseEnv("# c\r\n export A = one \r\nB=two=three");
      assert.deepEqual(basic, { A: "one", B: "two=three" });
      assert.equal(Object.getPrototypeOf(basic), Object.prototype);
      assert.deepEqual(parseEnv("A=hello # note\nB=hello#kept\nA=last"), {
        A: "last",
        B: "hello#kept",
      });
      assert.deepEqual(parseEnv(`S='a # b \\n'\nD="a\\n\\t\\r\\\\\\\"b" # ok`), {
        S: "a # b \\n",
        D: "a\n\t\r\\\"b",
      });
      assert.throws(() => parseEnv(null), TypeError);
      for (const [source, line] of [
        ["OK=1\nNO DASH=2", 2],
        ["MISSING", 1],
        ["A=\"unterminated", 1],
        ["A='x' trailing", 1],
        ["A=\"bad\\q\"", 1],
      ]) {
        assert.throws(
          () => parseEnv(source),
          (error) => error instanceof SyntaxError && error.message.includes(String(line)),
        );
      }
    },
  },
];

async function freshImport(file) {
  return import(`${pathToFileURL(file).href}?bench=${randomUUID()}`);
}

function sha256(value) {
  return createHash("sha256").update(value).digest("hex");
}

async function snapshot(root) {
  const result = new Map();
  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      const relative = path.relative(root, absolute);
      if (entry.isDirectory()) await visit(absolute);
      else if (entry.isFile()) result.set(relative, sha256(await readFile(absolute)));
      else result.set(relative, `special:${entry.name}`);
    }
  }
  await visit(root);
  return result;
}

function changedFiles(before, after) {
  const paths = new Set([...before.keys(), ...after.keys()]);
  return [...paths].filter((name) => before.get(name) !== after.get(name)).sort();
}

async function writeFixture(workspace, fixture) {
  for (const [relative, contents] of Object.entries(fixture.files)) {
    const destination = path.join(workspace, relative);
    await mkdir(path.dirname(destination), { recursive: true, mode: 0o700 });
    await writeFile(destination, contents, { mode: 0o600 });
  }
}

function signalProcessTree(child, signal) {
  if (!child) return;
  if (process.platform !== "win32" && Number.isInteger(child.pid) && child.pid > 1) {
    try {
      process.kill(-child.pid, signal);
      return;
    } catch {}
  }
  if (child.exitCode === null && child.signalCode === null) {
    try { child.kill(signal); } catch {}
  }
}

function waitForExit(child, milliseconds) {
  if (child.exitCode !== null || child.signalCode !== null) return Promise.resolve(true);
  return new Promise((resolve) => {
    const timer = setTimeout(() => resolve(false), milliseconds);
    child.once("exit", () => {
      clearTimeout(timer);
      resolve(true);
    });
  });
}

function runProcess(executable, args, { cwd, env = process.env, input = "", timeout = timeoutMs } = {}) {
  return new Promise((resolve) => {
    const startedAt = performance.now();
    const child = spawn(executable, args, {
      cwd,
      env,
      detached: process.platform !== "win32",
      shell: false,
      stdio: ["pipe", "pipe", "pipe"],
    });
    const stdout = [];
    const stderr = [];
    let timedOut = false;
    let settled = false;
    let killTimer = null;
    let deadlineTimer = null;
    const finish = (status, signal, extraStderr = "") => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (killTimer) clearTimeout(killTimer);
      if (deadlineTimer) clearTimeout(deadlineTimer);
      resolve({
        status,
        signal,
        timedOut,
        wallMs: Math.round(performance.now() - startedAt),
        stdout: Buffer.concat(stdout).toString("utf8"),
        stderr: `${Buffer.concat(stderr).toString("utf8")}${extraStderr}`,
      });
    };
    const timer = setTimeout(() => {
      timedOut = true;
      signalProcessTree(child, "SIGTERM");
      killTimer = setTimeout(() => signalProcessTree(child, "SIGKILL"), 1500);
      killTimer.unref();
      deadlineTimer = setTimeout(
        () => finish(null, "TIMEOUT", "\nprocess group did not close after SIGKILL"),
        4000,
      );
      deadlineTimer.unref();
    }, timeout);
    timer.unref();
    child.stdout.on("data", (chunk) => stdout.push(chunk));
    child.stderr.on("data", (chunk) => stderr.push(chunk));
    child.on("error", (error) => {
      signalProcessTree(child, "SIGKILL");
      finish(null, null, `\n${error.message}`);
    });
    child.on("close", (status, signal) => {
      finish(status, signal);
    });
    child.stdin.on("error", () => {});
    child.stdin.end(input);
  });
}

function parseJSONLines(text) {
  const events = [];
  let malformed = 0;
  for (const line of text.split(/\r?\n/)) {
    if (!line.trim()) continue;
    try { events.push(JSON.parse(line)); }
    catch { malformed += 1; }
  }
  return { events, malformed };
}

function directMetrics(stdout) {
  const parsed = parseJSONLines(stdout);
  const modelCalls = new Set();
  const toolCalls = new Set();
  let toolEvents = 0;
  let terminal = null;
  for (const event of parsed.events) {
    const modelCallID = event.model_call_id ?? event.message?.model_call_id;
    if (modelCallID) modelCalls.add(modelCallID);
    if (event.type === "tool_call") {
      toolEvents += 1;
      const toolCallID = event.tool_call_id ?? event.call_id ?? event.tool_call?.id ?? event.id;
      if (toolCallID) toolCalls.add(toolCallID);
    }
    if (event.type === "result") terminal = event;
  }
  return {
    malformedLines: parsed.malformed,
    eventCount: parsed.events.length,
    modelCallIDsObserved: modelCalls.size || null,
    toolEvents,
    uniqueToolCalls: toolCalls.size || null,
    cursorDurationMs: terminal?.duration_ms ?? null,
    cursorAPIDurationMs: terminal?.duration_api_ms ?? null,
  };
}

function codexMetrics(stdout) {
  const parsed = parseJSONLines(stdout);
  const itemTypes = {};
  const itemIDs = new Set();
  const toolItemIDs = new Set();
  let startedItems = 0;
  let completedItems = 0;
  for (const event of parsed.events) {
    if (event.type === "item.started" || event.type === "item.completed") {
      const type = event.item?.type ?? "unknown";
      const itemID = event.item?.id;
      const uniqueKey = itemID ?? `${type}:${JSON.stringify(event.item ?? {})}`;
      if (!itemIDs.has(uniqueKey)) {
        itemIDs.add(uniqueKey);
        itemTypes[type] = (itemTypes[type] ?? 0) + 1;
      }
      if (["command_execution", "file_change", "mcp_tool_call", "web_search", "function_call", "custom_tool_call"].includes(type)) {
        toolItemIDs.add(uniqueKey);
      }
      if (event.type === "item.started") startedItems += 1;
      else completedItems += 1;
    }
  }
  return {
    malformedLines: parsed.malformed,
    eventCount: parsed.events.length,
    startedItems,
    completedItems,
    uniqueItems: itemIDs.size,
    uniqueToolItems: toolItemIDs.size,
    itemTypes,
  };
}

async function scoreTrial(workspace, fixture, processResult, before) {
  const after = await snapshot(workspace);
  const changes = changedFiles(before, after);
  const protectedChanged = ["README.md", "test.mjs"].filter(
    (name) => before.get(name) !== after.get(name),
  );
  const visible = await runProcess(process.execPath, ["test.mjs"], {
    cwd: workspace,
    timeout: 30_000,
  });
  const hiddenSource = [
    'import assert from "node:assert/strict";',
    'import { randomUUID } from "node:crypto";',
    'import path from "node:path";',
    'import { pathToFileURL } from "node:url";',
    'async function freshImport(file) {',
    '  return import(`${pathToFileURL(file).href}?bench=${randomUUID()}`);',
    '}',
    `const scorer = { ${fixture.score.toString()} };`,
    'await scorer.score(process.argv[2]);',
  ].join("\n");
  const hidden = await runProcess(process.execPath, ["--input-type=module", "-", workspace], {
    cwd: path.dirname(workspace),
    input: hiddenSource,
    timeout: 30_000,
  });
  const hiddenPassed = hidden.status === 0 && !hidden.timedOut;
  const hiddenError = hiddenPassed
    ? null
    : hidden.stderr.trim().slice(-2000) || `hidden scorer status ${hidden.status ?? "null"}`;
  return {
    success: processResult.status === 0 && !processResult.timedOut && visible.status === 0 &&
      hiddenPassed && protectedChanged.length === 0,
    agentStatus: processResult.status,
    agentSignal: processResult.signal,
    timedOut: processResult.timedOut,
    wallMs: processResult.wallMs,
    visibleStatus: visible.status,
    visiblePassed: visible.status === 0,
    hiddenStatus: hidden.status,
    hiddenTimedOut: hidden.timedOut,
    hiddenPassed,
    hiddenError,
    protectedChanged,
    changedFiles: changes,
  };
}

async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : null;
  await new Promise((resolve) => server.close(resolve));
  if (!port) throw new Error("Could not allocate a benchmark port");
  return port;
}

async function makeAgentWrapper(root, logPath) {
  const wrapper = path.join(root, "cursor-agent-wrapper.mjs");
  const source = `#!/usr/bin/env node\nimport { appendFileSync } from "node:fs";\nimport { spawn } from "node:child_process";\nconst target = ${JSON.stringify(cursorAgent)};\nconst log = ${JSON.stringify(logPath)};\nappendFileSync(log, JSON.stringify({ at: Date.now(), args: process.argv.slice(2) }) + "\\n", { mode: 0o600 });\nconst child = spawn(target, process.argv.slice(2), { env: process.env, stdio: "inherit", shell: false });\nfor (const signal of ["SIGTERM", "SIGINT"]) process.on(signal, () => child.kill(signal));\nchild.on("error", () => process.exit(127));\nchild.on("exit", (code, signal) => {\n  if (signal) process.kill(process.pid, signal);\n  else process.exit(code ?? 1);\n});\n`;
  await writeFile(wrapper, source, { mode: 0o700 });
  await chmod(wrapper, 0o700);
  return wrapper;
}

async function startBridge(root) {
  const port = await freePort();
  const token = randomBytes(32).toString("hex");
  const workspace = path.join(root, "bridge-workspace");
  const callLog = path.join(root, "inner-cursor-calls.jsonl");
  await mkdir(workspace, { recursive: true, mode: 0o700 });
  await writeFile(callLog, "", { mode: 0o600 });
  const wrapper = await makeAgentWrapper(root, callLog);
  const child = spawn(process.execPath, [
    bridgePath,
    "--host", "127.0.0.1",
    "--port", String(port),
    "--agent", wrapper,
    "--model", model,
    "--workspace", workspace,
    "--timeout-ms", String(timeoutMs),
  ], {
    cwd: repository,
    detached: process.platform !== "win32",
    env: {
      ...process.env,
      SYNCBAR_CURSOR_BRIDGE_TOKEN: token,
      SYNCBAR_CURSOR_MODELS_JSON: JSON.stringify([model]),
    },
    shell: false,
    stdio: ["ignore", "pipe", "pipe"],
  });
  const stderr = [];
  child.stderr.on("data", (chunk) => stderr.push(chunk));
  try {
    await new Promise((resolve, reject) => {
    let buffer = "";
    const timer = setTimeout(() => reject(new Error("Bridge ready timeout")), 15_000);
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      buffer += chunk;
      const newline = buffer.indexOf("\n");
      if (newline < 0) return;
      try {
        const ready = JSON.parse(buffer.slice(0, newline));
        if (ready.event !== "ready") throw new Error("Unexpected bridge startup event");
        clearTimeout(timer);
        resolve();
      } catch (error) {
        clearTimeout(timer);
        reject(error);
      }
    });
    child.once("exit", (code) => {
      clearTimeout(timer);
      reject(new Error(`Bridge exited during startup (${code}): ${Buffer.concat(stderr).toString("utf8")}`));
    });
    });
  } catch (error) {
    signalProcessTree(child, "SIGTERM");
    await Promise.race([waitForExit(child, 1500), new Promise((resolve) => setTimeout(resolve, 1500))]);
    signalProcessTree(child, "SIGKILL");
    await waitForExit(child, 1000);
    throw error;
  }
  return {
    port,
    token,
    callLog,
    child,
    async stop() {
      signalProcessTree(child, "SIGTERM");
      await Promise.race([waitForExit(child, 3000), new Promise((resolve) => setTimeout(resolve, 3000))]);
      signalProcessTree(child, "SIGKILL");
      await waitForExit(child, 1000);
    },
  };
}

async function innerCallCount(callLog) {
  const text = await readFile(callLog, "utf8");
  return text.split(/\r?\n/).filter(Boolean).length;
}

async function runDirect(workspace) {
  return runProcess(cursorAgent, [
    "-p",
    "--force",
    "--trust",
    "--sandbox", "enabled",
    "--workspace", workspace,
    "--model", model,
    "--output-format", "stream-json",
    "--stream-partial-output",
  ], { cwd: workspace, input: prompt });
}

async function runCodex(workspace, root, bridge) {
  const codexHome = path.join(root, `codex-home-${randomUUID()}`);
  await mkdir(codexHome, { recursive: true, mode: 0o700 });
  return runProcess(codexCLI, [
    "exec",
    "--ephemeral",
    "--ignore-user-config",
    "--ignore-rules",
    "--skip-git-repo-check",
    "--json",
    "--color", "never",
    "-C", workspace,
    "-s", "workspace-write",
    "-m", model,
    "-c", 'model_provider="cursor_benchmark"',
    "-c", 'approval_policy="never"',
    "-c", 'model_providers.cursor_benchmark.name="Cursor benchmark bridge"',
    "-c", `model_providers.cursor_benchmark.base_url="http://127.0.0.1:${bridge.port}/v1"`,
    "-c", 'model_providers.cursor_benchmark.wire_api="responses"',
    "-c", "model_providers.cursor_benchmark.requires_openai_auth=false",
    "-c", 'model_providers.cursor_benchmark.env_http_headers={"X-SyncBar-Bridge-Token"="CURSOR_BENCH_BRIDGE_TOKEN"}',
    "-c", "model_providers.cursor_benchmark.request_max_retries=0",
    "-c", "model_providers.cursor_benchmark.stream_max_retries=0",
    "-",
  ], {
    cwd: workspace,
    input: prompt,
    env: {
      ...process.env,
      CODEX_HOME: codexHome,
      CURSOR_BENCH_BRIDGE_TOKEN: bridge.token,
    },
  });
}

async function version(executable) {
  const result = await runProcess(executable, ["--version"], { timeout: 15_000 });
  return result.status === 0 ? result.stdout.trim() : `unavailable (status ${result.status})`;
}

function mean(values) {
  return values.length ? Math.round(values.reduce((sum, value) => sum + value, 0) / values.length) : null;
}

async function main() {
  const startedAt = new Date().toISOString();
  for (const file of [cursorAgent, codexCLI, bridgePath]) {
    const info = await stat(file);
    if (!info.isFile()) throw new Error(`Required executable/file is missing: ${file}`);
  }
  if (!Number.isInteger(timeoutMs) || timeoutMs < 30_000) throw new Error("Invalid CURSOR_BENCH_TIMEOUT_MS");
  if (requestedArms.size === 0 ||
      [...requestedArms].some((arm) => !["cursor-direct", "codex-cursor"].includes(arm))) {
    throw new Error("CURSOR_BENCH_ARMS must contain cursor-direct and/or codex-cursor");
  }
  if (requestedFixtures &&
      (requestedFixtures.size === 0 ||
       [...requestedFixtures].some((id) => !fixtures.some((fixture) => fixture.id === id)))) {
    throw new Error("CURSOR_BENCH_FIXTURES contains an unknown fixture id");
  }
  const root = await mkdtemp(path.join(os.tmpdir(), "cursor-harness-bench-"));
  const logs = path.join(root, "logs");
  await mkdir(logs, { mode: 0o700 });
  const bridge = await startBridge(root);
  const trials = [];
  try {
    for (let index = 0; index < fixtures.length; index += 1) {
      const fixture = fixtures[index];
      if (requestedFixtures && !requestedFixtures.has(fixture.id)) continue;
      const arms = (index % 2 === 0
        ? ["cursor-direct", "codex-cursor"]
        : ["codex-cursor", "cursor-direct"]).filter((arm) => requestedArms.has(arm));
      for (const arm of arms) {
        const workspace = path.join(root, `${fixture.id}-${arm}`);
        await mkdir(workspace, { recursive: true, mode: 0o700 });
        await writeFixture(workspace, fixture);
        const before = await snapshot(workspace);
        const callsBefore = await innerCallCount(bridge.callLog);
        const processResult = arm === "cursor-direct"
          ? await runDirect(workspace)
          : await runCodex(workspace, root, bridge);
        const callsAfter = await innerCallCount(bridge.callLog);
        const score = await scoreTrial(workspace, fixture, processResult, before);
        const metrics = arm === "cursor-direct"
          ? directMetrics(processResult.stdout)
          : { ...codexMetrics(processResult.stdout), innerCursorInvocations: callsAfter - callsBefore };
        const stem = `${String(trials.length + 1).padStart(2, "0")}-${fixture.id}-${arm}`;
        await writeFile(path.join(logs, `${stem}.stdout.log`), processResult.stdout, { mode: 0o600 });
        await writeFile(path.join(logs, `${stem}.stderr.log`), processResult.stderr, { mode: 0o600 });
        trials.push({ fixture: fixture.id, arm, ...score, metrics, logStem: stem });
        process.stdout.write(`${arm} ${fixture.id}: ${score.success ? "PASS" : "FAIL"} ${score.wallMs}ms\n`);
      }
    }
  } finally {
    await bridge.stop();
  }

  const byArm = Object.fromEntries(["cursor-direct", "codex-cursor"].map((arm) => {
    const selected = trials.filter((trial) => trial.arm === arm);
    const successful = selected.filter((trial) => trial.success);
    return [arm, {
      successes: successful.length,
      trials: selected.length,
      successAt1: selected.length ? successful.length / selected.length : null,
      meanWallMsAllAttempts: mean(selected.map((trial) => trial.wallMs)),
      meanWallMsSuccessful: mean(successful.map((trial) => trial.wallMs)),
    }];
  }));
  const allTrialsPassed = Object.values(byArm).every((arm) => arm.successes === arm.trials);
  const directMean = byArm["cursor-direct"].meanWallMsSuccessful;
  const codexMean = byArm["codex-cursor"].meanWallMsSuccessful;
  const result = {
    schemaVersion: 1,
    startedAt,
    completedAt: new Date().toISOString(),
    scope: "end-to-end local coding/edit/test on three isolated deterministic Node fixtures",
    caveat: "One paired trial per fixture is directional, not statistically conclusive. This does not measure Desktop Computer Use or general IDE performance.",
    tokenMetric: "unavailable: Cursor CLI stream-json has no documented token or billing fields; bridge Codex usage is not Cursor billing usage",
    model,
    versions: {
      cursor: await version(cursorAgent),
      codex: await version(codexCLI),
      node: process.version,
    },
    platform: { arch: process.arch, os: process.platform, release: os.release() },
    ordering: "alternating AB/BA by fixture",
    requestedArms: [...requestedArms],
    requestedFixtures: requestedFixtures ? [...requestedFixtures] : fixtures.map((fixture) => fixture.id),
    promptSha256: sha256(prompt),
    summary: {
      ...byArm,
      codexCursorSuccessfulWallRatio: allTrialsPassed && directMean && codexMean
        ? Number((codexMean / directMean).toFixed(3))
        : null,
    },
    trials,
    rawLogsDirectory: logs,
  };
  await mkdir(path.dirname(outputPath), { recursive: true, mode: 0o700 });
  await writeFile(outputPath, `${JSON.stringify(result, null, 2)}\n`, { mode: 0o600 });
  process.stdout.write(`${outputPath}\n`);
}

await main();
