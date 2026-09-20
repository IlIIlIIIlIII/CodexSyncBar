#!/usr/bin/env python3
"""Translate WSL file URIs for the official Windows node_repl MCP server.

Only file URIs inside codex/sandbox-state-meta are translated. Permission
decisions, tool arguments, approvals, messages, and server replies are preserved.
The official server and @oai/sky remain responsible for execution and UI access.
"""

import argparse
import functools
import json
import os
import re
import subprocess
import sys
import threading
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit


META_KEY = "codex/sandbox-state-meta"
MAX_REQUEST_BYTES = 32 * 1024 * 1024


def windows_path_uri(path):
    if path.startswith("\\\\"):
        host, separator, rest = path[2:].partition("\\")
        if not separator or not host or not rest:
            raise ValueError("Invalid UNC path returned by wslpath")
        return "file://" + host + "/" + quote(rest.replace("\\", "/"), safe="/")
    if re.match(r"^[a-zA-Z]:\\", path):
        return "file:///" + quote(path.replace("\\", "/"), safe="/:")
    raise ValueError("wslpath did not return an absolute Windows path")


@functools.lru_cache(maxsize=512)
def to_windows_uri(uri):
    parsed = urlsplit(uri)
    if parsed.scheme != "file" or parsed.netloc not in ("", "localhost"):
        return uri
    if re.match(r"^/[a-zA-Z]:/", parsed.path):
        return uri
    if parsed.query or parsed.fragment:
        raise ValueError("File URI must not contain query or fragment")
    path = unquote(parsed.path, errors="strict")
    if not path.startswith("/") or "\x00" in path:
        raise ValueError("File URI must contain an absolute path without NUL")
    mapped = subprocess.run(
        ["/usr/bin/wslpath", "-w", path],
        check=True, capture_output=True, text=True, timeout=5,
    ).stdout.rstrip("\r\n")
    return windows_path_uri(mapped)


def translate_paths(value, mapper=to_windows_uri):
    if isinstance(value, str) and value.startswith("file:"):
        return mapper(value)
    if isinstance(value, dict):
        return {key: translate_paths(item, mapper) for key, item in value.items()}
    if isinstance(value, list):
        return [translate_paths(item, mapper) for item in value]
    return value


def translate_request(raw, mapper=to_windows_uri):
    message = json.loads(raw)
    if not isinstance(message, dict) or message.get("method") != "tools/call":
        return raw
    params = message.get("params")
    meta = params.get("_meta") if isinstance(params, dict) else None
    if not isinstance(meta, dict) or META_KEY not in meta:
        return raw
    translated = translate_paths(meta[META_KEY], mapper)
    if translated == meta[META_KEY]:
        return raw
    meta[META_KEY] = translated
    return (json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode()


def runtime_server_path():
    """Resolve the official executable beside the app-provided Windows Node.

    The app supplies fresh environment/pipe settings to the original node_repl
    identity. CODEX_NODE_REPL_PATH redirects only the executable entry point.
    """
    node = os.environ.get("NODE_REPL_NODE_PATH", "")
    if re.match(r"^[a-zA-Z]:\\", node) or node.startswith("\\\\"):
        node = subprocess.run(["/usr/bin/wslpath", "-u", node], check=True,
                              capture_output=True, text=True, timeout=5).stdout.rstrip("\r\n")
    path = Path(node)
    if not path.is_absolute() or path.name.lower() != "node.exe":
        raise ValueError("Expected the app-provided absolute Windows Node executable")
    server = path.with_name("node_repl.exe")
    if not server.is_file():
        raise ValueError("Official node_repl.exe not found beside the app-provided Node")
    return str(server)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server", nargs="?", help="WSL path to the official Windows node_repl.exe")
    args = parser.parse_args()
    # MCP launchers may filter WSL_DISTRO_NAME from their child environment.
    # Verify the host kernel instead of requiring that optional variable.
    if sys.platform != "linux" or "microsoft" not in os.uname().release.casefold():
        parser.error("This adapter must run in WSL")
    child = subprocess.Popen([args.server or runtime_server_path()], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    output_lock = threading.Lock()

    def write_output(chunk):
        with output_lock:
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()

    def forward_replies():
        try:
            # Keep complete MCP frames together: a local validation-error reply
            # must never be inserted halfway through a server response.
            for frame in child.stdout:
                write_output(frame)
        except BrokenPipeError:
            child.terminate()

    replies = threading.Thread(target=forward_replies, daemon=True)
    replies.start()
    try:
        while raw := sys.stdin.buffer.readline(MAX_REQUEST_BYTES + 1):
            if len(raw) > MAX_REQUEST_BYTES:
                raise ValueError("MCP request exceeds adapter limit")
            try:
                translated = translate_request(raw)
            except (ValueError, subprocess.SubprocessError):
                # Fail closed: do not strip metadata or substitute a policy.
                message = json.loads(raw)
                if not isinstance(message, dict) or "id" not in message:
                    raise ValueError("Cannot translate MCP notification")
                error = {"jsonrpc": "2.0", "id": message["id"], "error": {
                    "code": -32602,
                    "message": "WSL file URI translation failed; permission metadata was not changed",
                }}
                write_output((json.dumps(error) + "\n").encode())
                continue
            child.stdin.write(translated)
            child.stdin.flush()
        child.stdin.close()
        child.wait(timeout=10)
        replies.join(timeout=5)
        return child.returncode
    except (BrokenPipeError, ValueError, subprocess.TimeoutExpired):
        print("WSL node_repl adapter stopped; no tool payloads were logged", file=sys.stderr)
        return 1
    finally:
        if child.poll() is None:
            child.terminate()


if __name__ == "__main__":
    sys.exit(main())
