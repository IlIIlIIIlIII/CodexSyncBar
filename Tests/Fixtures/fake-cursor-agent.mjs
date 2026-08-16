#!/usr/bin/env node

import path from "node:path";

const args = process.argv.slice(2);
if (args[0] === "status") {
  if (args.length !== 1) process.exit(4);
  process.stdout.write('{"authenticated":true}\n');
  process.exit(0);
}
if (args[0] === "models" || args.includes("--list-models")) {
  process.stdout.write(`Available models

auto - Auto (default)
composer-2.5 - Composer 2.5
gpt-5.6-sol-high-fast - GPT-5.6 Sol High Fast
gpt-5.3-codex-low - Codex 5.3 Low
`);
  process.exit(0);
}
if (args.includes("--force") || args.includes("--yolo")) process.exit(9);
if (!args.includes("--mode=ask") || !args.includes("--stream-partial-output")) process.exit(8);
if (args.some((argument) => argument.includes("<SYNCBAR_BACKEND_REQUEST>"))) process.exit(7);

let prompt = "";
for await (const chunk of process.stdin) prompt += chunk;
const backendMatch = prompt.match(/<SYNCBAR_BACKEND_REQUEST>\n([\s\S]*?)\n<\/SYNCBAR_BACKEND_REQUEST>/);
const backendPayload = backendMatch ? JSON.parse(backendMatch[1]) : null;
function nestedValue(value, predicate) {
  if (predicate(value)) return value;
  if (Array.isArray(value)) {
    for (const item of value) {
      const found = nestedValue(item, predicate);
      if (found !== undefined) return found;
    }
  } else if (value && typeof value === "object") {
    for (const item of Object.values(value)) {
      const found = nestedValue(item, predicate);
      if (found !== undefined) return found;
    }
  }
  return undefined;
}
const hasCustomToolOutput = nestedValue(
  backendPayload?.conversation,
  (value) => value?.type === "custom_tool_call_output",
) !== undefined;
const hasFunctionToolOutput = nestedValue(
  backendPayload?.conversation,
  (value) => value?.type === "function_call_output",
) !== undefined;
if (
  prompt.includes("Exercise one local attachment tool read") &&
  !hasCustomToolOutput
) {
  const payload = backendPayload;
  let namespace;
  let found = false;
  for (const tool of payload?.available_tools ?? []) {
    if (tool.type === "custom" && tool.name === "exec") found = true;
    if (
      tool.type === "namespace" &&
      tool.tools?.some((nested) => nested.type === "custom" && nested.name === "exec")
    ) {
      namespace = tool.name;
      found = true;
    }
  }
  if (!found) process.exit(10);
  const attachmentText = nestedValue(
    payload?.conversation,
    (value) => typeof value === "string" && value.includes("## attachment.txt: "),
  );
  const attachmentMatch = attachmentText?.match(/^## attachment\.txt: (\/[^\r\n]+)$/m);
  if (!attachmentMatch) process.exit(11);
  const attachmentPath = attachmentMatch[1];
  const attachmentName = path.basename(attachmentPath);
  if (attachmentName !== "attachment.txt") process.exit(12);
  const input = [
    `const result = await tools.exec_command(${JSON.stringify({
      cmd: "/bin/cat -- attachment.txt",
      workdir: path.dirname(attachmentPath),
      yield_time_ms: 10_000,
      max_output_tokens: 1_000,
    })});`,
    "text(result.output);",
  ].join("\n");
  const text = `<SYNCBAR_TOOL_CALL>${JSON.stringify({
    ...(namespace ? { namespace } : {}),
    name: "exec",
    input,
  })}</SYNCBAR_TOOL_CALL>`;
  process.stdout.write(`${JSON.stringify({
    type: "assistant",
    timestamp_ms: 1,
    message: { content: [{ type: "text", text }] },
  })}\n`);
  process.stdout.write(`${JSON.stringify({ type: "result", subtype: "success", result: text })}\n`);
  process.exit(0);
}
if (
  prompt.includes("Exercise one local attachment tool read") &&
  hasCustomToolOutput
) {
  if (!prompt.includes("local-attachment-content-42")) process.exit(13);
  const text = "cursor local attachment passed";
  process.stdout.write(`${JSON.stringify({
    type: "assistant",
    timestamp_ms: 1,
    message: { content: [{ type: "text", text }] },
  })}\n`);
  process.stdout.write(`${JSON.stringify({ type: "result", subtype: "success", result: text })}\n`);
  process.exit(0);
}
if (
  prompt.includes("Exercise one harmless namespaced custom tool call") &&
  !hasCustomToolOutput
) {
  const payload = backendPayload;
  let namespace;
  for (const tool of payload?.available_tools ?? []) {
    if (
      tool.type === "namespace" &&
      tool.tools?.some((nested) => nested.type === "custom" && nested.name === "exec")
    ) {
      namespace = tool.name;
      break;
    }
  }
  if (!namespace) process.exit(5);
  const text = `<SYNCBAR_TOOL_CALL>${JSON.stringify({
    namespace,
    name: "exec",
    input: 'text("custom-tool-smoke-ok");',
  })}</SYNCBAR_TOOL_CALL>`;
  process.stdout.write(`${JSON.stringify({
    type: "assistant",
    timestamp_ms: 1,
    message: { content: [{ type: "text", text }] },
  })}\n`);
  process.stdout.write(`${JSON.stringify({ type: "result", subtype: "success", result: text })}\n`);
  process.exit(0);
}
if (
  prompt.includes("Exercise one harmless namespaced custom tool call") &&
  hasCustomToolOutput
) {
  const text = "cursor bridge namespaced custom smoke passed";
  process.stdout.write(`${JSON.stringify({
    type: "assistant",
    timestamp_ms: 1,
    message: { content: [{ type: "text", text }] },
  })}\n`);
  process.stdout.write(`${JSON.stringify({ type: "result", subtype: "success", result: text })}\n`);
  process.exit(0);
}
if (prompt.includes("Exercise one harmless tool call") && !hasFunctionToolOutput) {
  const payload = backendPayload;
  let namespace;
  let found = false;
  for (const tool of payload?.available_tools ?? []) {
    if (tool.type === "function" && tool.name === "update_plan") found = true;
    if (tool.type === "namespace" && tool.tools?.some((nested) => nested.name === "update_plan")) {
      namespace = tool.name;
      found = true;
    }
  }
  if (!found) process.exit(6);
  const envelope = {
    ...(namespace ? { namespace } : {}),
    name: "update_plan",
    arguments: { plan: [{ step: "bridge smoke", status: "completed" }] },
  };
  const text = `<SYNCBAR_TOOL_CALL>${JSON.stringify(envelope)}</SYNCBAR_TOOL_CALL>`;
  process.stdout.write(`${JSON.stringify({
    type: "assistant",
    timestamp_ms: 1,
    message: { content: [{ type: "text", text }] },
  })}\n`);
  process.stdout.write(`${JSON.stringify({ type: "result", subtype: "success", result: text })}\n`);
  process.exit(0);
}

process.stdout.write(`${JSON.stringify({
  type: "assistant",
  timestamp_ms: 1,
  message: { content: [{ type: "text", text: "cursor bridge smoke passed" }] },
})}\n`);
process.stdout.write(`${JSON.stringify({
  type: "result",
  subtype: "success",
  result: "cursor bridge smoke passed",
  session_id: "fixture-session",
})}\n`);
