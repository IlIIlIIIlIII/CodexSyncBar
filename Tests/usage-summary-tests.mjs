#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const usageHelper = path.join(repositoryRoot, "Support/usage-summary.mjs");
const controller = path.join(repositoryRoot, "Support/gpt-switch");
const root = fs.mkdtempSync(path.join(os.tmpdir(), "codex-syncbar-usage-tests-"));
const sessions = path.join(root, ".codex/sessions");
const cache = path.join(root, "usage-cache.json");

function usage(total, variant = 0) {
  const output = Math.max(1, Math.floor(total / 5) + variant);
  const input = total - output;
  return {
    input_tokens: input,
    cached_input_tokens: Math.floor(input / 2),
    cache_write_input_tokens: variant,
    output_tokens: output,
    reasoning_output_tokens: Math.floor(output / 3),
    total_tokens: total,
  };
}

function addUsage(left, right) {
  const result = {};
  for (const key of Object.keys(usage(1))) result[key] = left[key] + right[key];
  return result;
}

function sessionMeta(id, sessionID = id, forkedFromID = null, source = undefined) {
  return {
    type: "session_meta",
    payload: { id, session_id: sessionID, forked_from_id: forkedFromID, ...(source ? { source } : {}) },
  };
}

function turnContext(model, serviceTier = "default") {
  return { type: "turn_context", payload: { model, service_tier: serviceTier } };
}

function tokenCount(last, total, timestamp) {
  return {
    ...(timestamp ? { timestamp } : {}),
    type: "event_msg",
    payload: { type: "token_count", info: { last_token_usage: last, total_token_usage: total } },
  };
}

function writeSession(name, events) {
  const file = path.join(sessions, name);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${events.map((event) => JSON.stringify(event)).join("\n")}\n`);
  return file;
}

function collectNode() {
  const result = spawnSync("node", [usageHelper, sessions, cache], { encoding: "utf8" });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout);
}

function collectJQ() {
  const result = spawnSync("/bin/bash", [controller, "__node", "usage-summary"], {
    encoding: "utf8",
    env: {
      ...process.env,
      HOME: root,
      CODEX_HOME: path.join(root, ".codex"),
      GPT_SWITCH_STATE_ROOT: path.join(root, ".local/share/gpt-switch-jq"),
      GPT_SWITCH_USAGE_HELPER: usageHelper,
      GPT_SWITCH_NODE_BIN: "/missing/node",
    },
  });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return JSON.parse(result.stdout);
}

function comparable(summary) {
  const fields = [
    "schemaVersion", "scannedFiles", "requests", "inputTokens", "cachedInputTokens",
    "cacheWriteInputTokens", "outputTokens", "reasoningOutputTokens", "totalTokens",
  ];
  return {
    ...Object.fromEntries(fields.map((field) => [field, summary[field]])),
    buckets: [...summary.buckets].sort((left, right) =>
      `${left.model}\u001f${left.serviceTier}`.localeCompare(`${right.model}\u001f${right.serviceTier}`)),
    errors: summary.errors,
  };
}

try {
  fs.mkdirSync(sessions, { recursive: true });

  const p1 = usage(100, 1);
  const p2 = usage(80, 2);
  const p3 = usage(80, 3);
  const p1Total = p1;
  const p2Total = addUsage(p1Total, p2);
  const p3Total = addUsage(p2Total, p3);
  const parentTokens = [
    tokenCount(p1, p1Total),
    tokenCount(p2, p2Total),
    tokenCount(p3, p3Total),
  ];
  writeSession("z-parent.jsonl", [
    sessionMeta("root-1"),
    turnContext("gpt-5.6-sol"),
    ...parentTokens,
  ]);

  const c1 = usage(70, 4);
  const c2 = usage(70, 5);
  const c1Total = addUsage(p3Total, c1);
  const c2Total = addUsage(c1Total, c2);
  const childTokens = [
    ...parentTokens,
    tokenCount(c1, c1Total),
    tokenCount(c2, c2Total),
  ];
  writeSession("a-child.jsonl", [
    sessionMeta("child-1", "root-1", "root-1"),
    sessionMeta("root-1"),
    turnContext("gpt-5.6-sol"),
    ...parentTokens,
    turnContext("gpt-5.6-terra", "priority"),
    ...childTokens.slice(3),
  ]);

  const g1 = usage(50, 6);
  const g1Total = addUsage(c2Total, g1);
  writeSession("b-grandchild.jsonl", [
    sessionMeta("grandchild-1", "root-1", "child-1"),
    turnContext("gpt-5.6-sol"),
    ...childTokens.slice(1),
    turnContext("gpt-5.4-mini"),
    tokenCount(g1, g1Total),
  ]);

  const same = usage(90, 7);
  const independentOne = writeSession("independent-one.jsonl", [
    sessionMeta("independent-1"), turnContext("gpt-5.6-sol"), tokenCount(same, same),
  ]);
  writeSession("independent-two.jsonl", [
    sessionMeta("independent-2"), turnContext("gpt-5.6-sol"), tokenCount(same, same),
  ]);
  const standaloneSubagent = usage(35, 15);
  writeSession("standalone-subagent.jsonl", [
    sessionMeta("standalone-subagent-1", "standalone-subagent-1", null, { subagent: {} }),
    turnContext("gpt-5.4"),
    tokenCount(standaloneSubagent, standaloneSubagent),
  ]);

  const n1 = usage(60, 8);
  const n2 = usage(40, 9);
  const n2Total = addUsage(n1, n2);
  const reset = usage(30, 10);
  const fallback = usage(20, 11);
  writeSession("normal-reset-fallback.jsonl", [
    sessionMeta("normal-1"),
    turnContext("gpt-5.6-sol"),
    tokenCount(n1, n1),
    tokenCount(n1, n1),
    turnContext("gpt-5.6-terra", "priority"),
    tokenCount(n2, n2Total),
    tokenCount(reset, reset),
    tokenCount(fallback, usage(0)),
  ]);

  const oldTimestamp = new Date(Date.now() - 31 * 24 * 60 * 60 * 1000).toISOString();
  const old = usage(100, 12);
  const recent = usage(60, 13);
  writeSession("rolling-window.jsonl", [
    sessionMeta("rolling-1"),
    turnContext("gpt-5.6-sol"),
    tokenCount(old, old, oldTimestamp),
    tokenCount(recent, addUsage(old, recent)),
  ]);

  fs.writeFileSync(cache, `${JSON.stringify({
    schemaVersion: 4,
    files: { "stale.jsonl": { buckets: { stale: { totalTokens: 999_999_999 } } } },
  })}\n`);

  const first = collectNode();
  assert.equal(first.totalTokens, 875, "fork replay must not be charged");
  assert.equal(first.requests, 14);
  assert.equal(first.schemaVersion, 5);
  assert.deepEqual(
    first.buckets.map(({ model, serviceTier, totalTokens }) => ({ model, serviceTier, totalTokens })),
    [
      { model: "gpt-5.6-sol", serviceTier: "default", totalTokens: 560 },
      { model: "gpt-5.6-terra", serviceTier: "priority", totalTokens: 230 },
      { model: "gpt-5.4-mini", serviceTier: "default", totalTokens: 50 },
      { model: "gpt-5.4", serviceTier: "default", totalTokens: 35 },
    ],
  );
  const rebuiltCache = JSON.parse(fs.readFileSync(cache, "utf8"));
  assert.equal(rebuiltCache.schemaVersion, 5);
  assert.equal(rebuiltCache.files["stale.jsonl"], undefined);

  assert.deepEqual(comparable(collectNode()), comparable(first), "unchanged cache scan must be idempotent");

  const appended = usage(25, 14);
  fs.appendFileSync(independentOne, `${JSON.stringify(tokenCount(appended, addUsage(same, appended)))}\n`);
  const afterAppend = collectNode();
  assert.equal(afterAppend.totalTokens, 900);
  assert.equal(afterAppend.requests, 15);
  assert.deepEqual(comparable(collectNode()), comparable(afterAppend));

  const jqSummary = collectJQ();
  assert.deepEqual(comparable(jqSummary), comparable(afterAppend), "Node and jq paths must agree");

  process.stdout.write("usage-summary regression tests passed\n");
} finally {
  fs.rmSync(root, { recursive: true, force: true });
}
