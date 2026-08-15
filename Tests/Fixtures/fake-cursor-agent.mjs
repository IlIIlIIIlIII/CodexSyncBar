#!/usr/bin/env node

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
if (
  prompt.includes("Exercise one harmless namespaced custom tool call") &&
  !prompt.includes("custom_tool_call_output")
) {
  const match = prompt.match(/<SYNCBAR_BACKEND_REQUEST>\n([\s\S]*?)\n<\/SYNCBAR_BACKEND_REQUEST>/);
  const payload = match ? JSON.parse(match[1]) : null;
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
  prompt.includes("custom_tool_call_output")
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
if (prompt.includes("Exercise one harmless tool call") && !prompt.includes("function_call_output")) {
  const match = prompt.match(/<SYNCBAR_BACKEND_REQUEST>\n([\s\S]*?)\n<\/SYNCBAR_BACKEND_REQUEST>/);
  const payload = match ? JSON.parse(match[1]) : null;
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
