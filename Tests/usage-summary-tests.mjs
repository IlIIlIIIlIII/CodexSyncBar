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
const epochMinute = (timestamp) => Math.floor(Date.parse(timestamp) / 60_000);
const TOKEN_FIELDS = [
  "inputTokens", "cachedInputTokens", "cacheWriteInputTokens", "cacheWriteOneHourInputTokens",
  "outputTokens", "reasoningOutputTokens",
];

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

// The prompt fields repeat on every line, while the output and thinking
// counts grow; each entry of `outputs` is one line's [output, thinking].
function growing(id, model, input, outputs, timestamp) {
  return outputs.map(([output, thinking], block) => ({
    ...response(id, model, usage(input, 0, 0, output, { output_tokens_details: { thinking_tokens: thinking } }), timestamp)[0],
    apiBlockIndex: block,
  }));
}

function writeTranscript(name, events) {
  const file = path.join(transcripts, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${events.flat().map((event) => JSON.stringify(event)).join("\n")}\n`);
  return file;
}

function appendTranscript(file, events) {
  fs.appendFileSync(file, `${events.flat().map((event) => JSON.stringify(event)).join("\n")}\n`);
}

// The per-minute timeline must hold exactly the 30-day totals and buckets.
function checkTimeline(summary) {
  const { models, tiers, minutes } = summary;
  assert.deepEqual(models, [...new Set(models)].sort(), "models are distinct and sorted");
  assert.deepEqual(tiers, [...new Set(tiers)].sort(), "tiers are distinct and sorted");
  const sums = {};
  const totals = Object.fromEntries([...TOKEN_FIELDS, "requests"].map((field) => [field, 0]));
  minutes.forEach((row, index) => {
    assert.equal(row.length, 11);
    assert.ok(row.every(Number.isSafeInteger), "timeline rows hold integers");
    assert.ok(row[1] < models.length && row[2] < tiers.length && (row[3] === 0 || row[3] === 1));
    if (index > 0) {
      const previous = minutes[index - 1];
      const order = previous[0] - row[0] || previous[1] - row[1] || previous[2] - row[2] || previous[3] - row[3];
      assert.ok(order < 0, "timeline rows are sorted and unique");
    }
    const key = `${models[row[1]]}\u001f${tiers[row[2]]}\u001f${row[3] === 1}`;
    sums[key] ??= Object.fromEntries([...TOKEN_FIELDS, "requests"].map((field) => [field, 0]));
    [...TOKEN_FIELDS, "requests"].forEach((field, column) => {
      sums[key][field] += row[column + 4];
      totals[field] += row[column + 4];
    });
  });
  for (const field of TOKEN_FIELDS) assert.equal(totals[field], summary[field], `timeline ${field}`);
  assert.equal(totals.requests, summary.requests, "timeline requests");
  assert.deepEqual(
    Object.fromEntries(summary.buckets.map((bucket) => [
      `${bucket.model}\u001f${bucket.serviceTier}\u001f${bucket.isLongContext}`,
      Object.fromEntries([...TOKEN_FIELDS, "requests"].map((field) => [field, bucket[field]])),
    ])),
    sums,
    "timeline matches the buckets",
  );
  assert.deepEqual(models, [...new Set(summary.buckets.map((bucket) => bucket.model))].sort());
  assert.deepEqual(tiers, [...new Set(summary.buckets.map((bucket) => bucket.serviceTier))].sort());
  assert.equal(summary.firstRecordAt, minutes.length > 0 ? new Date(minutes[0][0] * 60_000).toISOString() : null);
  return summary;
}

function collectNode() {
  const result = spawnSync("node", [usageHelper, transcripts, cache], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return checkTimeline(JSON.parse(result.stdout));
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
  return checkTimeline(JSON.parse(result.stdout));
}

function comparable(summary) {
  const fields = [
    "schemaVersion", "windowDays", "firstRecordAt", "scannedFiles", "requests", "inputTokens", "cachedInputTokens",
    "cacheWriteInputTokens", "cacheWriteOneHourInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens",
    "models", "tiers", "minutes", "sessions",
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
  assert.equal(empty.firstRecordAt, null);
  assert.deepEqual([empty.models, empty.tiers, empty.minutes], [[], [], []]);
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
  assert.equal(summary.schemaVersion, 7);
  assert.equal(summary.windowDays, 30);
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
  // The timeline starts at the minute of the earliest response in the window
  // and names models and tiers by their index in the sorted lists.
  assert.equal(summary.firstRecordAt, new Date(epochMinute(recent(29)) * 60_000).toISOString());
  assert.deepEqual(summary.models, [haiku, opus]);
  assert.deepEqual(summary.tiers, ["default", "fast", "priority"]);
  assert.deepEqual(summary.minutes, [
    [epochMinute(recent(29)), 1, 0, 0, 55, 40, 5, 4, 20, 8, 1],
    [epochMinute(recent(28)), 1, 0, 0, 100, 0, 0, 0, 50, 0, 1],
    [epochMinute(recent(27)), 1, 1, 0, 7, 0, 0, 0, 3, 0, 1],
    [epochMinute(recent(26)), 0, 2, 0, 4, 0, 0, 0, 1, 0, 1],
    [epochMinute(recent(20)), 0, 0, 0, 30, 0, 0, 0, 6, 0, 1],
    [epochMinute(recent(10)), 1, 0, 0, 6, 2, 3, 0, 4, 0, 1],
  ]);
  const rebuiltCache = JSON.parse(fs.readFileSync(cache, "utf8"));
  assert.equal(rebuiltCache.schemaVersion, 7);
  assert.equal(rebuiltCache.files["-home-user-stale/stale.jsonl"], undefined);
  for (const state of Object.values(rebuiltCache.files)) {
    for (const entry of Object.values(state.responses)) {
      assert.ok(Array.isArray(entry) && entry.length === 4 && Number.isFinite(entry[0])
        && Number.isSafeInteger(entry[1]) && Number.isSafeInteger(entry[2])
        && (entry[3] === null || state.buckets[entry[3]] !== undefined), "cached responses name their bucket");
    }
  }

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
  assert.ok(long.minutes.some((row) => row[0] === epochMinute(recent(3)) && row[3] === 1 && row[4] === 100_001));
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

  // The output count of a response grows over its lines; the largest counts.
  // Its thinking count is the largest one too, capped at the largest output.
  const sonnet = "claude-sonnet-5-5";
  const grow = writeTranscript("-home-user-grow/grow.jsonl", [
    growing("m1", sonnet, 10, [[10, 4], [25, 12]], recent(8)),
    growing("t1", sonnet, 5, [[10, 30], [25, 20]], recent(8)),
    // A response whose first line has no tokens is not a request.
    growing("z1", sonnet, 0, [[0, 0], [5, 0]], recent(8)),
    // Only this response's first line is written before the next scan.
    growing("g1", sonnet, 7, [[10, 4]], recent(7)),
  ]);
  const grown = collectNode();
  assert.equal(grown.requests, rewritten.requests + 3);
  assert.equal(grown.inputTokens, rewritten.inputTokens + 10 + 5 + 7);
  assert.equal(grown.outputTokens, rewritten.outputTokens + 25 + 25 + 10);
  assert.equal(grown.reasoningOutputTokens, rewritten.reasoningOutputTokens + 12 + 25 + 4);
  assert.equal(grown.totalTokens, grown.inputTokens + grown.outputTokens);
  assert.deepEqual(grown.models, [haiku, opus, sonnet]);
  assert.deepEqual(grown.minutes.filter((row) => row[1] === 2), [
    [epochMinute(recent(8)), 2, 0, 0, 15, 0, 0, 0, 50, 37, 2],
    [epochMinute(recent(7)), 2, 0, 0, 7, 0, 0, 0, 10, 4, 1],
  ]);
  assert.deepEqual(comparable(collectJQ()), comparable(grown));

  // The larger lines can arrive after the first one was read in an earlier scan.
  appendTranscript(grow, [growing("g1", sonnet, 7, [[10, 4], [40, 16], [30, 20]], recent(7)).slice(1)]);
  const finished = collectNode();
  assert.equal(finished.requests, grown.requests);
  assert.equal(finished.inputTokens, grown.inputTokens);
  assert.equal(finished.outputTokens, grown.outputTokens + 30);
  assert.equal(finished.reasoningOutputTokens, grown.reasoningOutputTokens + 16);
  assert.deepEqual(grown.minutes.filter((row) => row[1] === 2)[0],
    finished.minutes.filter((row) => row[1] === 2)[0]);
  assert.deepEqual(finished.minutes.filter((row) => row[1] === 2)[1],
    [epochMinute(recent(7)), 2, 0, 0, 7, 0, 0, 0, 40, 20, 1]);
  const grownCache = JSON.parse(fs.readFileSync(cache, "utf8")).files["-home-user-grow/grow.jsonl"];
  assert.deepEqual(Object.values(grownCache.responses).map((entry) => entry.slice(1, 3)).sort((a, b) => a[0] - b[0] || a[1] - b[1]),
    [[0, 0], [25, 12], [25, 30], [40, 20]]);
  assert.deepEqual(comparable(collectNode()), comparable(finished));
  assert.deepEqual(comparable(collectJQ()), comparable(finished));

  // A resumed session's copy of a response, even with more output, is ignored.
  writeTranscript("-home-user-grow/resumed.jsonl", [
    growing("m1", sonnet, 10, [[10, 4], [25, 12], [60, 30]], recent(8)),
    growing("g1", sonnet, 7, [[90, 0]], recent(7)),
    growing("r1", sonnet, 3, [[2, 0], [6, 1]], recent(6)),
  ]);
  const resumed = collectNode();
  assert.equal(resumed.requests, finished.requests + 1);
  assert.equal(resumed.outputTokens, finished.outputTokens + 6);
  assert.equal(resumed.reasoningOutputTokens, finished.reasoningOutputTokens + 1);
  assert.deepEqual(comparable(collectJQ()), comparable(resumed));
  // A full rescan without the cache reaches the same totals.
  fs.rmSync(cache);
  assert.deepEqual(comparable(collectNode()), comparable(resumed));

  // A cache written by the previous schema is rebuilt from the logs.
  const current = JSON.parse(fs.readFileSync(cache, "utf8"));
  const stale = current.files["-home-user-grow/grow.jsonl"];
  for (const bucket of Object.values(stale.buckets)) bucket.outputTokens += 1_000;
  fs.writeFileSync(cache, `${JSON.stringify({ ...current, schemaVersion: 6 })}\n`);
  assert.deepEqual(comparable(collectNode()), comparable(resumed));
  assert.equal(JSON.parse(fs.readFileSync(cache, "utf8")).schemaVersion, 7);

  // Claude Desktop's copy of a session that ran on another machine counts under that session, and a
  // session another device already counted is left out when it is named.
  writeTranscript("ssh-remote1/workflow-agent-a1.jsonl", [response("x1", sonnet, usage(5, 0, 0, 5), recent(4))]);
  const withCopy = collectNode();
  assert.ok(withCopy.sessions.includes("remote1"), JSON.stringify(withCopy.sessions));
  assert.ok(withCopy.sessions.includes("grow"), JSON.stringify(withCopy.sessions));
  assert.deepEqual(comparable(collectJQ()), comparable(withCopy));
  const skip = path.join(root, "skip-sessions.json");
  fs.writeFileSync(skip, JSON.stringify(["remote1"]));
  const skipped = spawnSync("node", [usageHelper, transcripts, cache, skip], { encoding: "utf8" });
  assert.equal(skipped.status, 0, skipped.stderr || skipped.stdout);
  const without = checkTimeline(JSON.parse(skipped.stdout));
  assert.equal(without.totalTokens, withCopy.totalTokens - 10);
  assert.ok(!without.sessions.includes("remote1"));
  process.stdout.write("usage-summary regression tests passed\n");

} finally {
  fs.rmSync(root, { recursive: true, force: true });
}
