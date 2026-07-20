#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import readline from "node:readline";

const SCHEMA_VERSION = 4;
const ROLLING_WINDOW_DAYS = 30;
const ROLLING_WINDOW_MS = ROLLING_WINDOW_DAYS * 24 * 60 * 60 * 1000;
const sessionsRoot = path.resolve(process.argv[2] ?? path.join(process.env.HOME ?? "", ".codex/sessions"));
const cachePath = path.resolve(process.argv[3] ?? path.join(process.env.HOME ?? "", ".local/share/gpt-switch/usage-cache.json"));

function emptyTokens() {
  return {
    inputTokens: 0,
    cachedInputTokens: 0,
    cacheWriteInputTokens: 0,
    outputTokens: 0,
    reasoningOutputTokens: 0,
    totalTokens: 0,
  };
}

function safeInteger(value) {
  return Number.isSafeInteger(value) && value >= 0 ? value : 0;
}

function tokensFrom(value) {
  return {
    inputTokens: safeInteger(value?.input_tokens),
    cachedInputTokens: safeInteger(value?.cached_input_tokens),
    cacheWriteInputTokens: safeInteger(value?.cache_write_input_tokens),
    outputTokens: safeInteger(value?.output_tokens),
    reasoningOutputTokens: safeInteger(value?.reasoning_output_tokens),
    totalTokens: safeInteger(value?.total_tokens),
  };
}

function addTokens(target, value) {
  for (const key of Object.keys(emptyTokens())) target[key] += value[key];
}

function deltaTokens(current, previous) {
  const delta = emptyTokens();
  let reset = false;
  for (const key of Object.keys(delta)) {
    if (current[key] < previous[key]) reset = true;
  }
  for (const key of Object.keys(delta)) {
    delta[key] = reset ? current[key] : current[key] - previous[key];
  }
  return delta;
}

function emptyFileState(stat) {
  return {
    dev: String(stat.dev),
    ino: String(stat.ino),
    offset: 0,
    size: stat.size,
    mtimeMs: stat.mtimeMs,
    model: "unknown",
    serviceTier: "default",
    cumulative: emptyTokens(),
    hasCumulative: false,
    buckets: {},
  };
}

function normalizeState(value, stat) {
  if (!value || value.dev !== String(stat.dev) || value.ino !== String(stat.ino)
      || !Number.isSafeInteger(value.offset) || value.offset < 0 || value.offset > stat.size) {
    return emptyFileState(stat);
  }
  value.model = typeof value.model === "string" && value.model ? value.model : "unknown";
  value.serviceTier = typeof value.serviceTier === "string" && value.serviceTier ? value.serviceTier : "default";
  value.cumulative = { ...emptyTokens(), ...(value.cumulative ?? {}) };
  value.hasCumulative = value.hasCumulative === true;
  value.buckets = value.buckets && typeof value.buckets === "object" ? value.buckets : {};
  return value;
}

function minuteStart(eventTimeMs) {
  return new Date(Math.floor(eventTimeMs / 60_000) * 60_000).toISOString();
}

function eventTimeMs(event, fallbackTimeMs) {
  const parsed = Date.parse(event?.timestamp ?? "");
  return Number.isFinite(parsed) ? parsed : fallbackTimeMs;
}

function bucketFor(state, eventTime) {
  const startedAt = minuteStart(eventTime);
  const key = `${startedAt}\u001f${state.model}\u001f${state.serviceTier}`;
  if (!state.buckets[key]) {
    state.buckets[key] = {
      startedAt,
      model: state.model,
      serviceTier: state.serviceTier,
      ...emptyTokens(),
      requests: 0,
    };
  }
  return state.buckets[key];
}

function processEvent(state, event, fallbackTimeMs, cutoffMs) {
  if (event?.type === "event_msg" && event.payload?.type === "thread_settings_applied") {
    const settings = event.payload.thread_settings;
    if (typeof settings?.model === "string" && settings.model) state.model = settings.model;
    if (typeof settings?.service_tier === "string" && settings.service_tier) {
      state.serviceTier = settings.service_tier;
    }
    return;
  }
  if (event?.type === "turn_context") {
    if (typeof event.payload?.model === "string" && event.payload.model) state.model = event.payload.model;
    if (typeof event.payload?.service_tier === "string" && event.payload.service_tier) {
      state.serviceTier = event.payload.service_tier;
    }
    return;
  }
  if (event?.type !== "event_msg" || event.payload?.type !== "token_count" || !event.payload.info) return;

  const current = tokensFrom(event.payload.info.total_token_usage);
  const last = tokensFrom(event.payload.info.last_token_usage);
  if (state.model === "unknown") {
    // Forked logs replay the parent's token events before their own settings.
    // Keep the cumulative baseline, but never charge that inherited history
    // to the new device session.
    if (current.totalTokens > 0) {
      state.cumulative = current;
      state.hasCumulative = true;
    }
    return;
  }
  let increment;
  if (current.totalTokens > 0) {
    if (state.hasCumulative) {
      increment = deltaTokens(current, state.cumulative);
    } else {
      // Forked/sub-agent logs can inherit a very large parent cumulative total.
      // Only the first request's last usage belongs to this new session file.
      increment = last.totalTokens > 0 ? last : current;
      state.hasCumulative = true;
    }
    state.cumulative = current;
  } else {
    increment = last;
  }
  if (increment.totalTokens <= 0) return;
  const occurredAt = eventTimeMs(event, fallbackTimeMs);
  if (occurredAt < cutoffMs) return;
  const bucket = bucketFor(state, occurredAt);
  addTokens(bucket, increment);
  bucket.requests += 1;
}

function pruneBuckets(state, cutoffMs) {
  for (const [key, bucket] of Object.entries(state.buckets)) {
    if (Date.parse(bucket.startedAt ?? "") < cutoffMs) delete state.buckets[key];
  }
}

async function scanFile(file, previous, cutoffMs) {
  const stat = await fs.promises.stat(file);
  let state = normalizeState(previous, stat);
  if (state.offset === stat.size && state.mtimeMs === stat.mtimeMs) {
    pruneBuckets(state, cutoffMs);
    return state;
  }
  if (state.offset === stat.size && state.mtimeMs !== stat.mtimeMs) state = emptyFileState(stat);

  const stream = fs.createReadStream(file, { start: state.offset, encoding: "utf8" });
  const lines = readline.createInterface({ input: stream, crlfDelay: Infinity });
  let consumed = state.offset;
  for await (const line of lines) {
    consumed += Buffer.byteLength(line, "utf8") + 1;
    if (!line.includes('"token_count"') && !line.includes('"turn_context"')
        && !line.includes('"thread_settings_applied"')) continue;
    try {
      processEvent(state, JSON.parse(line), stat.mtimeMs, cutoffMs);
    } catch {
      // A single malformed/incomplete event must not discard the rest of a session.
    }
  }
  state.offset = Math.min(consumed, stat.size);
  state.size = stat.size;
  state.mtimeMs = stat.mtimeMs;
  pruneBuckets(state, cutoffMs);
  return state;
}

async function sessionFiles(root) {
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
  const files = await sessionFiles(sessionsRoot);
  const active = new Set();
  const errors = [];
  for (const file of files) {
    const relative = path.relative(sessionsRoot, file);
    active.add(relative);
    try {
      cache.files[relative] = await scanFile(file, cache.files[relative], cutoffMs);
    } catch (error) {
      errors.push(`${relative}: ${error?.message ?? String(error)}`);
    }
  }
  for (const relative of Object.keys(cache.files)) {
    if (!active.has(relative)) delete cache.files[relative];
  }
  await saveCache(cache);

  const merged = {};
  for (const state of Object.values(cache.files)) {
    for (const bucket of Object.values(state.buckets ?? {})) {
      if (Date.parse(bucket.startedAt ?? "") < cutoffMs) continue;
      const key = `${bucket.model}\u001f${bucket.serviceTier}`;
      if (!merged[key]) {
        merged[key] = {
          model: bucket.model,
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
