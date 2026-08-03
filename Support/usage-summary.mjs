#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import readline from "node:readline";
import crypto from "node:crypto";

const SCHEMA_VERSION = 5;
const ROLLING_WINDOW_DAYS = 30;
const ROLLING_WINDOW_MS = ROLLING_WINDOW_DAYS * 24 * 60 * 60 * 1000;
const TOKEN_KEYS = [
  "inputTokens",
  "cachedInputTokens",
  "cacheWriteInputTokens",
  "outputTokens",
  "reasoningOutputTokens",
  "totalTokens",
];
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
  for (const key of TOKEN_KEYS) target[key] += value[key];
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

function tokenFingerprint(current, last) {
  // Token events do not expose a stable ID. Hash every field from both counters;
  // matching is still restricted to a child's direct-parent replay below.
  const serialized = [...TOKEN_KEYS.map((key) => current[key]), ...TOKEN_KEYS.map((key) => last[key])].join(":");
  return crypto.createHash("sha256").update(serialized).digest().subarray(0, 12).toString("base64url");
}

function normalizedMetadata(value) {
  const payload = value && typeof value === "object" ? value : {};
  return {
    id: typeof payload.id === "string" && payload.id ? payload.id : null,
    sessionId: typeof payload.session_id === "string" && payload.session_id ? payload.session_id : null,
    forkedFromId: typeof payload.forked_from_id === "string" && payload.forked_from_id
      ? payload.forked_from_id
      : null,
  };
}

async function readSessionMetadata(file) {
  const handle = await fs.promises.open(file, "r");
  const chunks = [];
  let position = 0;
  try {
    while (position < 1024 * 1024) {
      const buffer = Buffer.alloc(64 * 1024);
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, position);
      if (bytesRead <= 0) break;
      const chunk = buffer.subarray(0, bytesRead);
      const lineEnd = chunk.indexOf(0x0a);
      chunks.push(lineEnd >= 0 ? chunk.subarray(0, lineEnd) : chunk);
      if (lineEnd >= 0) break;
      position += bytesRead;
    }
  } finally {
    await handle.close();
  }
  try {
    const event = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    if (event?.type === "session_meta") return normalizedMetadata(event.payload);
  } catch {
    // Sessions without valid leading metadata are aggregated independently.
  }
  return normalizedMetadata(null);
}

function emptyFileState(stat, metadata) {
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
    metadata,
    tokenFingerprints: [],
    inheritance: {
      active: Boolean(metadata.forkedFromId),
      initialized: false,
      candidates: [],
    },
    buckets: {},
  };
}

function normalizeState(value, stat, metadata) {
  if (!value || value.dev !== String(stat.dev) || value.ino !== String(stat.ino)
      || !Number.isSafeInteger(value.offset) || value.offset < 0 || value.offset > stat.size) {
    return emptyFileState(stat, metadata);
  }
  value.model = typeof value.model === "string" && value.model ? value.model : "unknown";
  value.serviceTier = typeof value.serviceTier === "string" && value.serviceTier ? value.serviceTier : "default";
  value.cumulative = { ...emptyTokens(), ...(value.cumulative ?? {}) };
  value.hasCumulative = value.hasCumulative === true;
  value.metadata = metadata;
  value.tokenFingerprints = Array.isArray(value.tokenFingerprints)
    ? value.tokenFingerprints.filter((item) => typeof item === "string")
    : [];
  if (!value.inheritance || typeof value.inheritance !== "object") {
    value.inheritance = {
      active: Boolean(metadata.forkedFromId),
      initialized: false,
      candidates: [],
    };
  }
  value.inheritance.active = value.inheritance.active === true && Boolean(metadata.forkedFromId);
  value.inheritance.initialized = value.inheritance.initialized === true;
  value.inheritance.candidates = Array.isArray(value.inheritance.candidates)
    ? value.inheritance.candidates.filter((candidate) =>
      typeof candidate?.relative === "string" && Array.isArray(candidate.positions))
    : [];
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

function isInheritedToken(state, fingerprint, ancestors) {
  if (!state.inheritance.active) return false;

  // A resumed/forked log can begin at any point inside its parent's token
  // history. Only suppress the initial contiguous match; the first mismatch
  // permanently starts the child's own usage, even if later values coincide.
  let candidates;
  if (!state.inheritance.initialized) {
    candidates = [];
    for (const ancestor of ancestors) {
      const positions = [];
      const sequence = ancestor.state.tokenFingerprints ?? [];
      for (let index = 0; index < sequence.length; index += 1) {
        if (sequence[index] === fingerprint) positions.push(index + 1);
      }
      if (positions.length > 0) candidates.push({ relative: ancestor.relative, positions });
    }
    state.inheritance.initialized = true;
  } else {
    const states = new Map(ancestors.map((ancestor) => [ancestor.relative, ancestor.state]));
    candidates = [];
    for (const candidate of state.inheritance.candidates) {
      const sequence = states.get(candidate.relative)?.tokenFingerprints ?? [];
      const positions = candidate.positions
        .filter((position) => position < sequence.length && sequence[position] === fingerprint)
        .map((position) => position + 1);
      if (positions.length > 0) candidates.push({ relative: candidate.relative, positions });
    }
  }

  state.inheritance.candidates = candidates;
  if (candidates.length > 0) return true;
  state.inheritance.active = false;
  return false;
}

function updateCumulativeBaseline(state, current) {
  if (current.totalTokens <= 0) return;
  state.cumulative = current;
  state.hasCumulative = true;
}

function processEvent(state, event, fallbackTimeMs, cutoffMs, ancestors) {
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
  const fingerprint = tokenFingerprint(current, last);
  state.tokenFingerprints.push(fingerprint);
  if (isInheritedToken(state, fingerprint, ancestors)) {
    updateCumulativeBaseline(state, current);
    return;
  }
  if (state.model === "unknown") {
    updateCumulativeBaseline(state, current);
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

async function scanFile(file, previous, cutoffMs, metadata, ancestors) {
  const stat = await fs.promises.stat(file);
  let state = normalizeState(previous, stat, metadata);
  if (state.offset === stat.size && state.mtimeMs === stat.mtimeMs) {
    pruneBuckets(state, cutoffMs);
    return state;
  }
  if (state.offset === stat.size && state.mtimeMs !== stat.mtimeMs) state = emptyFileState(stat, metadata);

  const stream = fs.createReadStream(file, { start: state.offset, encoding: "utf8" });
  const lines = readline.createInterface({ input: stream, crlfDelay: Infinity });
  let consumed = state.offset;
  for await (const line of lines) {
    consumed += Buffer.byteLength(line, "utf8") + 1;
    if (!line.includes('"token_count"') && !line.includes('"turn_context"')
        && !line.includes('"thread_settings_applied"')) continue;
    try {
      processEvent(state, JSON.parse(line), stat.mtimeMs, cutoffMs, ancestors);
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

function registerUnique(mapping, id, relative) {
  if (!id) return;
  if (!mapping.has(id)) mapping.set(id, relative);
  else if (mapping.get(id) !== relative) mapping.set(id, null);
}

function buildHierarchy(relatives, metadataByFile) {
  const byId = new Map();
  const bySessionId = new Map();
  for (const relative of relatives) {
    const metadata = metadataByFile.get(relative);
    registerUnique(byId, metadata.id, relative);
    registerUnique(bySessionId, metadata.sessionId, relative);
  }

  const parentByFile = new Map();
  for (const relative of relatives) {
    const forkedFromId = metadataByFile.get(relative).forkedFromId;
    const parent = byId.get(forkedFromId) ?? bySessionId.get(forkedFromId) ?? null;
    if (parent && parent !== relative) parentByFile.set(relative, parent);
  }

  const ordered = [];
  const visited = new Set();
  const visiting = new Set();
  function visit(relative) {
    if (visited.has(relative)) return;
    if (visiting.has(relative)) return;
    visiting.add(relative);
    const parent = parentByFile.get(relative);
    if (parent) visit(parent);
    visiting.delete(relative);
    visited.add(relative);
    ordered.push(relative);
  }
  for (const relative of relatives) visit(relative);
  return { ordered, parentByFile };
}

function parentStates(relative, parentByFile, states) {
  const parent = parentByFile.get(relative);
  return parent && states[parent] ? [{ relative: parent, state: states[parent] }] : [];
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
  const relatives = files.map((file) => path.relative(sessionsRoot, file));
  const filesByRelative = new Map(relatives.map((relative, index) => [relative, files[index]]));
  const metadataByFile = new Map();
  for (const relative of relatives) {
    try {
      metadataByFile.set(relative, await readSessionMetadata(filesByRelative.get(relative)));
    } catch {
      metadataByFile.set(relative, normalizedMetadata(null));
    }
  }
  const hierarchy = buildHierarchy(relatives, metadataByFile);
  const active = new Set();
  const errors = [];
  for (const relative of hierarchy.ordered) {
    const file = filesByRelative.get(relative);
    active.add(relative);
    try {
      cache.files[relative] = await scanFile(
        file,
        cache.files[relative],
        cutoffMs,
        metadataByFile.get(relative),
        parentStates(relative, hierarchy.parentByFile, cache.files),
      );
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
