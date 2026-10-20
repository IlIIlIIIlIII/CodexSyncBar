#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

const SCHEMA_VERSION = 6;
const ROLLING_WINDOW_DAYS = 30;
const ROLLING_WINDOW_MS = ROLLING_WINDOW_DAYS * 24 * 60 * 60 * 1000;
// Some models bill a prompt above this size on a higher rate card.
const LONG_CONTEXT_TOKENS = 100_000;
const TOKEN_KEYS = [
  "inputTokens",
  "cachedInputTokens",
  "cacheWriteInputTokens",
  "cacheWriteOneHourInputTokens",
  "outputTokens",
  "reasoningOutputTokens",
  "totalTokens",
];
const transcriptsRoot = path.resolve(process.argv[2] ?? path.join(process.env.HOME ?? "", ".claude/projects"));
const cachePath = path.resolve(process.argv[3] ?? path.join(process.env.HOME ?? "", ".local/share/claude-switch/usage-cache.json"));

function emptyTokens() {
  return {
    inputTokens: 0,
    cachedInputTokens: 0,
    cacheWriteInputTokens: 0,
    cacheWriteOneHourInputTokens: 0,
    outputTokens: 0,
    reasoningOutputTokens: 0,
    totalTokens: 0,
  };
}

function safeInteger(value) {
  return Number.isSafeInteger(value) && value >= 0 ? value : 0;
}

// Claude reports uncached, cache-read and cache-write prompt tokens apart.
// The summary's inputTokens is the whole prompt, with the cached parts as
// subsets of it, which is what the cost estimate expects. Cache writes kept
// for an hour cost more than the default five minutes, so they are split out.
function tokensFrom(usage) {
  const cached = safeInteger(usage?.cache_read_input_tokens);
  const cacheWrite = safeInteger(usage?.cache_creation_input_tokens);
  const input = safeInteger(usage?.input_tokens) + cached + cacheWrite;
  const output = safeInteger(usage?.output_tokens);
  return {
    inputTokens: input,
    cachedInputTokens: cached,
    cacheWriteInputTokens: cacheWrite,
    cacheWriteOneHourInputTokens: Math.min(cacheWrite, safeInteger(usage?.cache_creation?.ephemeral_1h_input_tokens)),
    outputTokens: output,
    reasoningOutputTokens: Math.min(output, safeInteger(usage?.output_tokens_details?.thinking_tokens)),
    totalTokens: input + output,
  };
}

function addTokens(target, value) {
  for (const key of TOKEN_KEYS) target[key] += safeInteger(value[key]);
}

function serviceTierFrom(usage) {
  if (usage?.speed === "fast") return "fast";
  const tier = usage?.service_tier;
  return typeof tier === "string" && tier && tier !== "standard" ? tier : "default";
}

function responseKey(event) {
  const id = event.message?.id;
  if (typeof id !== "string" || !id) return null;
  const request = typeof event.requestId === "string" ? event.requestId : "";
  return crypto.createHash("sha256").update(`${id}\u001f${request}`).digest().subarray(0, 9).toString("base64url");
}

function emptyFileState(stat) {
  return {
    dev: String(stat.dev),
    ino: String(stat.ino),
    offset: 0,
    size: stat.size,
    mtimeMs: stat.mtimeMs,
    responses: {},
    buckets: {},
  };
}

function normalizeState(value, stat) {
  if (!value || value.dev !== String(stat.dev) || value.ino !== String(stat.ino)
      || !Number.isSafeInteger(value.offset) || value.offset < 0 || value.offset > stat.size) {
    return emptyFileState(stat);
  }
  value.responses = value.responses && typeof value.responses === "object" ? value.responses : {};
  value.buckets = value.buckets && typeof value.buckets === "object" ? value.buckets : {};
  return value;
}

function minuteStart(eventTimeMs) {
  return new Date(Math.floor(eventTimeMs / 60_000) * 60_000).toISOString();
}

function bucketFor(state, eventTime, model, serviceTier, isLongContext) {
  const startedAt = minuteStart(eventTime);
  const key = `${startedAt}\u001f${model}\u001f${serviceTier}\u001f${isLongContext}`;
  if (!state.buckets[key]) {
    state.buckets[key] = {
      startedAt,
      isLongContext,
      model,
      serviceTier,
      ...emptyTokens(),
      requests: 0,
    };
  }
  return state.buckets[key];
}

function processEvent(state, relative, event, fallbackTimeMs, cutoffMs, owners) {
  if (event?.type !== "assistant") return;
  const usage = event.message?.usage;
  const model = event.message?.model;
  // Locally generated notices carry a placeholder model and no real usage.
  if (!usage || typeof usage !== "object" || typeof model !== "string" || !model || model === "<synthetic>") return;
  const key = responseKey(event);
  if (!key) return;
  // One response is written as several lines, and a resumed session copies
  // earlier responses into its new file. Each response counts once.
  if (owners.has(key)) return;
  const parsed = Date.parse(event.timestamp ?? "");
  const occurredAt = Number.isFinite(parsed) ? parsed : fallbackTimeMs;
  if (occurredAt < cutoffMs) return;
  owners.set(key, relative);
  state.responses[key] = occurredAt;
  const tokens = tokensFrom(usage);
  if (tokens.totalTokens <= 0) return;
  const bucket = bucketFor(state, occurredAt, model, serviceTierFrom(usage), tokens.inputTokens > LONG_CONTEXT_TOKENS);
  addTokens(bucket, tokens);
  bucket.requests += 1;
}

function prune(state, cutoffMs) {
  for (const [key, bucket] of Object.entries(state.buckets)) {
    if (Date.parse(bucket.startedAt ?? "") < cutoffMs) delete state.buckets[key];
  }
  for (const [key, occurredAt] of Object.entries(state.responses)) {
    if (!(occurredAt >= cutoffMs)) delete state.responses[key];
  }
}

async function scanFile(file, relative, previous, cutoffMs, owners) {
  const stat = await fs.promises.stat(file);
  let state = normalizeState(previous, stat);
  if (state.offset === stat.size && state.mtimeMs === stat.mtimeMs) {
    prune(state, cutoffMs);
    return state;
  }
  if (state.offset === stat.size && state.mtimeMs !== stat.mtimeMs) state = emptyFileState(stat);
  if (state.offset === 0) {
    // The file is read again from the start; forget what it claimed before.
    for (const [key, owner] of owners) if (owner === relative) owners.delete(key);
  }

  const stream = fs.createReadStream(file, { start: state.offset });
  let pending = Buffer.alloc(0);
  let consumed = state.offset;
  for await (const chunk of stream) {
    pending = pending.length === 0 ? chunk : Buffer.concat([pending, chunk]);
    let lineEnd;
    while ((lineEnd = pending.indexOf(0x0a)) >= 0) {
      const line = pending.subarray(0, lineEnd).toString("utf8");
      pending = pending.subarray(lineEnd + 1);
      consumed += lineEnd + 1;
      if (!line.includes('"assistant"') || !line.includes('"usage"')) continue;
      try {
        processEvent(state, relative, JSON.parse(line), stat.mtimeMs, cutoffMs, owners);
      } catch {
        // A single malformed event must not discard the rest of a session.
      }
    }
  }
  // A line still being written stays unread until its newline arrives.
  state.offset = Math.min(consumed, stat.size);
  state.size = stat.size;
  state.mtimeMs = stat.mtimeMs;
  prune(state, cutoffMs);
  return state;
}

async function transcriptFiles(root) {
  const result = [];
  async function visit(directory) {
    let entries;
    try {
      entries = await fs.promises.readdir(directory, { withFileTypes: true });
    } catch (error) {
      if (error?.code === "ENOENT") return;
      throw error;
    }
    for (const entry of entries) {
      const child = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(child);
      else if (entry.isFile() && entry.name.endsWith(".jsonl")) result.push(child);
    }
  }
  await visit(root);
  return result.sort();
}

async function loadCache() {
  try {
    const parsed = JSON.parse(await fs.promises.readFile(cachePath, "utf8"));
    if (parsed?.schemaVersion === SCHEMA_VERSION && parsed.files && typeof parsed.files === "object") return parsed;
  } catch {
    // Missing or corrupt cache is rebuilt from the source logs.
  }
  return { schemaVersion: SCHEMA_VERSION, files: {} };
}

async function saveCache(cache) {
  await fs.promises.mkdir(path.dirname(cachePath), { recursive: true, mode: 0o700 });
  const temporary = `${cachePath}.${process.pid}.tmp`;
  await fs.promises.writeFile(temporary, `${JSON.stringify(cache)}\n`, { mode: 0o600 });
  await fs.promises.rename(temporary, cachePath);
  await fs.promises.chmod(cachePath, 0o600);
}

async function main() {
  const generatedAt = new Date();
  const cutoffMs = generatedAt.getTime() - ROLLING_WINDOW_MS;
  const cache = await loadCache();
  const files = await transcriptFiles(transcriptsRoot);
  const relatives = files.map((file) => path.relative(transcriptsRoot, file));
  const active = new Set(relatives);
  for (const relative of Object.keys(cache.files)) {
    if (!active.has(relative)) delete cache.files[relative];
  }
  const owners = new Map();
  for (const [relative, state] of Object.entries(cache.files)) {
    for (const key of Object.keys(state?.responses ?? {})) {
      if (!owners.has(key)) owners.set(key, relative);
    }
  }

  const errors = [];
  for (let index = 0; index < files.length; index += 1) {
    const relative = relatives[index];
    try {
      cache.files[relative] = await scanFile(files[index], relative, cache.files[relative], cutoffMs, owners);
    } catch (error) {
      errors.push(`${relative}: ${error?.message ?? String(error)}`);
    }
  }
  await saveCache(cache);

  const merged = {};
  for (const state of Object.values(cache.files)) {
    for (const bucket of Object.values(state.buckets ?? {})) {
      if (Date.parse(bucket.startedAt ?? "") < cutoffMs) continue;
      const key = `${bucket.model}\u001f${bucket.serviceTier}\u001f${bucket.isLongContext}`;
      if (!merged[key]) {
        merged[key] = {
          model: bucket.model,
          isLongContext: bucket.isLongContext,
          serviceTier: bucket.serviceTier,
          ...emptyTokens(),
          requests: 0,
        };
      }
      addTokens(merged[key], bucket);
      merged[key].requests += safeInteger(bucket.requests);
    }
  }
  const buckets = Object.values(merged).sort((a, b) => b.totalTokens - a.totalTokens);
  const totals = emptyTokens();
  let requests = 0;
  for (const bucket of buckets) {
    addTokens(totals, bucket);
    requests += bucket.requests;
  }
  process.stdout.write(`${JSON.stringify({
    schemaVersion: SCHEMA_VERSION,
    generatedAt: generatedAt.toISOString(),
    windowDays: ROLLING_WINDOW_DAYS,
    windowStartedAt: new Date(cutoffMs).toISOString(),
    scannedFiles: files.length,
    requests,
    ...totals,
    buckets,
    errors: errors.slice(0, 20),
  })}\n`);
}

main().catch((error) => {
  process.stderr.write(`usage-summary: ${error?.message ?? String(error)}\n`);
  process.exitCode = 1;
});
