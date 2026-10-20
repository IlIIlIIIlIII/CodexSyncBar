#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const controller = path.join(repositoryRoot, "Support/claude-switch");
const root = fs.mkdtempSync(path.join(os.tmpdir(), "claude-syncbar-usage-tests-"));
// The production helper rejects writable runtime assets. Keep tests independent
// of the checkout's group-writable directory/file permissions.
const usageHelper = path.join(root, "usage-summary.mjs");
fs.copyFileSync(path.join(repositoryRoot, "Support/usage-summary.mjs"), usageHelper);
fs.chmodSync(usageHelper, 0o755);
const claudeHome = path.join(root, ".claude");
const transcripts = path.join(claudeHome, "projects");
const cache = path.join(root, "usage-cache.json");
const now = Date.now();
const recent = (minutesAgo) => new Date(now - minutesAgo * 60_000).toISOString();

function usage(input, cacheRead, cacheWrite, output, extra = {}) {
  return {
    input_tokens: input,
    cache_read_input_tokens: cacheRead,
    cache_creation_input_tokens: cacheWrite,
    output_tokens: output,
    service_tier: "standard",
    speed: "standard",
    ...extra,
  };
}

// Claude Code writes one line per content block of a response; each line
// repeats the response's id, request id and usage.
function response(id, model, tokens, timestamp, lines = 1) {
  return Array.from({ length: lines }, (_, block) => ({
    type: "assistant",
    timestamp,
    requestId: `req_${id}`,
    apiBlockIndex: block,
    message: { id: `msg_${id}`, model, usage: tokens },
  }));
}

function writeTranscript(name, events) {
  const file = path.join(transcripts, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${events.flat().map((event) => JSON.stringify(event)).join("\n")}\n`);
  return file;
}

function collectNode() {
  const result = spawnSync("node", [usageHelper, transcripts, cache], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout);
}

function collectJQ() {
  const result = spawnSync("/bin/bash", [controller, "__node", "usage-summary"], {
    encoding: "utf8",
    env: {
      ...process.env,
      HOME: root,
      CLAUDE_CONFIG_DIR: claudeHome,
      CLAUDE_SWITCH_STATE_ROOT: path.join(root, ".local/share/claude-switch-jq"),
      CLAUDE_SWITCH_USAGE_HELPER: usageHelper,
      CLAUDE_SWITCH_NODE_BIN: "/missing/node",
    },
  });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout);
}

function comparable(summary) {
  const fields = [
    "schemaVersion", "scannedFiles", "requests", "inputTokens", "cachedInputTokens",
    "cacheWriteInputTokens", "cacheWriteOneHourInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens",
  ];
  return {
    ...Object.fromEntries(fields.map((field) => [field, summary[field]])),
    buckets: [...summary.buckets].sort((left, right) =>
      `${left.model}\u001f${left.serviceTier}\u001f${left.isLongContext}`
        .localeCompare(`${right.model}\u001f${right.serviceTier}\u001f${right.isLongContext}`)),
    errors: summary.errors,
  };
}

try {
  const empty = collectNode();
  assert.equal(empty.totalTokens, 0);
  assert.equal(empty.scannedFiles, 0);
  assert.deepEqual(comparable(collectJQ()), comparable(empty), "an absent transcript directory is empty usage");

  const opus = "claude-opus-5-5";
  const haiku = "claude-haiku-5-5";
  // 10 uncached + 40 cache-read + 5 cache-write prompt tokens, 20 output of which 8 are thinking.
  // Four of the five cache-write tokens are kept for an hour.
  const first = usage(10, 40, 5, 20, {
    output_tokens_details: { thinking_tokens: 8 },
    cache_creation: { ephemeral_1h_input_tokens: 4, ephemeral_5m_input_tokens: 1 },
  });
  const second = usage(100, 0, 0, 50);
  const session = writeTranscript("-home-user-project/session-a.jsonl", [
    { type: "user", timestamp: recent(30), message: { role: "user", content: "question" } },
    response("a1", opus, first, recent(29), 3),
    response("a2", opus, second, recent(28), 2),
    response("fast", opus, usage(7, 0, 0, 3, { speed: "fast" }), recent(27)),
    response("priority", haiku, usage(4, 0, 0, 1, { service_tier: "priority" }), recent(26)),
    // Locally generated notices and events without usage are not requests.
    response("notice", "<synthetic>", usage(999, 0, 0, 999), recent(25)),
    { type: "assistant", timestamp: recent(24), requestId: "req_none", message: { id: "msg_none", model: opus } },
    { type: "system", timestamp: recent(23), content: "\"assistant\" \"usage\" in unrelated text" },
  ]);
  fs.appendFileSync(session, "{\"type\":\"assistant\",\"message\":{\"usage\": not json\n");

  // A resumed session starts a new file with the earlier responses copied in.
  writeTranscript("-home-user-project/session-b.jsonl", [
    response("a1", opus, first, recent(29), 3),
    response("a2", opus, second, recent(28), 2),
    response("b1", opus, usage(1, 2, 3, 4), recent(10)),
  ]);
  // Sub-agent transcripts live below their session and hold their own requests.
  writeTranscript("-home-user-project/session-a/subagents/agent-1.jsonl", [
    response("sub1", haiku, usage(30, 0, 0, 6), recent(20), 2),
  ]);
  // Outside the 30-day window.
  writeTranscript("-home-user-old/session-old.jsonl", [
    response("old", opus, usage(5000, 0, 0, 5000), new Date(now - 31 * 24 * 60 * 60 * 1000).toISOString()),
  ]);
  writeTranscript("-home-user-stale/stale.jsonl", [response("stale", opus, usage(1, 0, 0, 1), recent(5))]);
  collectNode();
  fs.rmSync(path.join(transcripts, "-home-user-stale"), { recursive: true });

  const summary = collectNode();
  assert.equal(summary.schemaVersion, 6);
  assert.equal(summary.scannedFiles, 4);
  assert.equal(summary.requests, 6, "split and copied responses count once");
  assert.equal(summary.inputTokens, 55 + 100 + 7 + 4 + 6 + 30);
  assert.equal(summary.cachedInputTokens, 40 + 2);
  assert.equal(summary.cacheWriteInputTokens, 5 + 3);
  assert.equal(summary.cacheWriteOneHourInputTokens, 4);
  assert.equal(summary.outputTokens, 20 + 50 + 3 + 1 + 4 + 6);
  assert.equal(summary.reasoningOutputTokens, 8);
  assert.equal(summary.totalTokens, summary.inputTokens + summary.outputTokens);
  assert.deepEqual(
    comparable(summary).buckets.map(({ model, serviceTier, isLongContext, totalTokens, requests }) =>
      ({ model, serviceTier, isLongContext, totalTokens, requests })),
    [
      { model: haiku, serviceTier: "default", isLongContext: false, totalTokens: 36, requests: 1 },
      { model: haiku, serviceTier: "priority", isLongContext: false, totalTokens: 5, requests: 1 },
      { model: opus, serviceTier: "default", isLongContext: false, totalTokens: 75 + 150 + 10, requests: 3 },
      { model: opus, serviceTier: "fast", isLongContext: false, totalTokens: 10, requests: 1 },
    ],
  );
  const rebuiltCache = JSON.parse(fs.readFileSync(cache, "utf8"));
  assert.equal(rebuiltCache.schemaVersion, 6);
  assert.equal(rebuiltCache.files["-home-user-stale/stale.jsonl"], undefined);

  assert.deepEqual(comparable(collectNode()), comparable(summary), "unchanged cache scan must be idempotent");
  assert.deepEqual(comparable(collectJQ()), comparable(summary), "Node and jq paths must agree");

  // A response still being written is left for the next scan, then counted once.
  const appended = response("a3", opus, usage(9, 0, 0, 1), recent(1), 2).map((event) => JSON.stringify(event));
  fs.appendFileSync(session, `${appended[0]}\n${appended[1].slice(0, 40)}`);
  const partial = collectNode();
  assert.equal(partial.requests, summary.requests + 1);
  assert.equal(partial.totalTokens, summary.totalTokens + 10);
  fs.appendFileSync(session, `${appended[1].slice(40)}\n`);
  const completed = collectNode();
  assert.deepEqual(comparable(completed), comparable(partial));
  assert.deepEqual(comparable(collectJQ()), comparable(completed));

  // Prompts above 100K tokens can fall on a higher rate card; the cached
  // parts of the prompt count toward that size.
  writeTranscript("-home-user-long/long.jsonl", [
    response("short", opus, usage(50_000, 49_000, 1_000, 10), recent(4)),
    response("long", opus, usage(50_001, 49_000, 1_000, 10), recent(3)),
  ]);
  const long = collectNode();
  const longBuckets = long.buckets.filter((bucket) => bucket.model === opus && bucket.serviceTier === "default");
  assert.equal(longBuckets.find((bucket) => bucket.isLongContext).inputTokens, 100_001);
  assert.equal(longBuckets.find((bucket) => !bucket.isLongContext).inputTokens,
    100_000 + 55 + 100 + 6 + 9);
  assert.deepEqual(comparable(collectNode()), comparable(long));
  assert.deepEqual(comparable(collectJQ()), comparable(long));

  // A rewritten transcript is read again from the start without double counting.
  writeTranscript("-home-user-project/session-b.jsonl", [
    response("a1", opus, first, recent(29), 3),
    response("b1", opus, usage(1, 2, 3, 4), recent(10)),
    response("b2", opus, usage(2, 0, 0, 2), recent(2)),
  ]);
  const rewritten = collectNode();
  assert.equal(rewritten.requests, long.requests + 1);
  assert.equal(rewritten.totalTokens, long.totalTokens + 4);
  assert.deepEqual(comparable(collectJQ()), comparable(rewritten));
  process.stdout.write("usage-summary regression tests passed\n");

} finally {
  fs.rmSync(root, { recursive: true, force: true });
}
