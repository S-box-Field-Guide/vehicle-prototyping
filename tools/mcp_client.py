#!/usr/bin/env python3
"""Minimal reusable client for the s&box editor MCP server (vehicle_prototyping).

The editor serves the Model Context Protocol over HTTP once you enable it in
Edit > Preferences > MCP Server. This is the durable driver for the
vehicle_prototyping agent-playtest loop (tools/vp_test.py): it does the JSON-RPC
call, unwraps the s&box `call_tool` meta-tool for you, prints results, and can
rip a base64 PNG screenshot straight to disk.

The direct precedent for the agent playtest loop; pointed at this project's port.

The s&box MCP surface is a handful of ENTRY-POINT tools (editor_status,
read_console, search_tools, list_toolsets, describe_toolset, call_tool,
call_tools). The real project/engine tools (vp_status, vp_spawn, vp_drive,
vp_audit, compile_status, play_start, set_editor_camera,
editor_camera_screenshot, ...) are NOT top-level -- they are invoked THROUGH
`call_tool`. This client hides that: name an entry-point tool and it is called
directly; name anything else and it is auto-wrapped in call_tool. The server is
stateless HTTP (no session id, no initialize handshake required), so every call
is a single independent POST.

PORT (multi-agent rule): the MCP port is PER-EDITOR configurable in the
editor's settings (Edit > Preferences > MCP Server -> "Mcp Server Port"; the
page shows "Running at http://127.0.0.1:<port>/mcp" when live). With several
editors open concurrently each project gets its OWN port. Assignments on this
machine, vehicle_prototyping = 7290.
NEVER talk to another project's port. The default here is this repo's assigned
port 7290; override with the VP_MCP_URL env var or --url. ALWAYS identity-probe
before any mutating call: editor_status must report Project=vehicle_prototyping
AND search_tools 'vp_' must list vp_drive/vp_audit (only this project's Editor
assembly defines them).

Usage:
  python tools/mcp_client.py <tool> ['<json args>'] [options]

  <tool>       tool name. Entry-point tools called directly; any other name is
               wrapped via call_tool {name:<tool>, arguments:<json args>}.
  '<json args>'  optional JSON object of arguments, e.g. '{"width":1280}'.

Options:
  --save-b64 <field> <outfile>   decode a base64 PNG to <outfile>.
                                 <field> = 'image' or 'data' -> the first image
                                 content block's data (this is how
                                 editor_camera_screenshot returns a PNG).
                                 <field> = any other key -> that key parsed out
                                 of the tool's JSON text/structured result.
  --url <url>                    MCP endpoint. Default: $VP_MCP_URL if set,
                                 else http://127.0.0.1:7290/mcp (this repo's
                                 assigned port).
  --raw                          print the raw JSON-RPC response and exit.
  --no-wrap                      never auto-wrap; call <tool> as a top-level tool.
  --quiet                        only print the unwrapped text payload.

Examples:
  python tools/mcp_client.py editor_status
  python tools/mcp_client.py search_tools '{"query":"vp_"}'
  python tools/mcp_client.py vp_status '{}'
  python tools/mcp_client.py set_editor_camera '{"position":"-8000,-8000,6000","angles":"-30,45,0"}'
  python tools/mcp_client.py editor_camera_screenshot '{"width":1280,"height":720}' \
      --save-b64 image screenshots/shot.png
  python tools/mcp_client.py read_console '{"limit":40}'
"""

import argparse
import base64
import json
import os
import sys
import urllib.request
import urllib.error

# vehicle_prototyping's assigned MCP port (one port per project; never point this
# client at another project's editor).
DEFAULT_URL = os.environ.get("VP_MCP_URL", "http://127.0.0.1:7290/mcp")

# The s&box MCP entry-point tools -- called directly, never wrapped.
ENTRY_TOOLS = {
    "call_tool", "call_tools", "describe_toolset", "editor_status",
    "list_toolsets", "read_console", "search_tools",
}

_next_id = [0]


def _rpc(url, method, params, timeout):
    _next_id[0] += 1
    body = json.dumps({
        "jsonrpc": "2.0", "id": _next_id[0], "method": method, "params": params,
    }).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST", headers={
        "Content-Type": "application/json",
        # s&box streams over the standard MCP transport; it answers plain JSON
        # for these calls but wants the SSE accept type advertised.
        "Accept": "application/json, text/event-stream",
    })
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
    except urllib.error.URLError as e:
        raise SystemExit(
            f"[mcp_client] cannot reach {url}: {e}\n"
            "Is the editor open with Edit > Preferences > MCP Server enabled, "
            "and is the port this project's assigned one (vehicle_prototyping=7290)? "
            "The settings page shows 'Running at http://127.0.0.1:<port>/mcp'."
        )
    # The transport may answer as an SSE frame ('data: {json}') or plain JSON.
    text = raw.strip()
    if text.startswith("data:"):
        text = "\n".join(
            ln[len("data:"):].strip() for ln in text.splitlines()
            if ln.startswith("data:")
        )
    return json.loads(text)


def call(url, tool, arguments, timeout, wrap=True):
    """Invoke a tool, auto-wrapping non-entry tools through call_tool."""
    if tool in ENTRY_TOOLS or not wrap:
        params = {"name": tool}
        if arguments:
            params["arguments"] = arguments
    else:
        params = {"name": "call_tool", "arguments": {"name": tool}}
        if arguments:
            params["arguments"]["arguments"] = arguments
    resp = _rpc(url, "tools/call", params, timeout)
    if "error" in resp:
        raise SystemExit(f"[mcp_client] JSON-RPC error: {json.dumps(resp['error'])}")
    return resp.get("result", {})


def _text_payload(result):
    """The concatenated text content blocks of a tool result."""
    return "".join(
        c.get("text", "") for c in result.get("content", [])
        if c.get("type") == "text"
    )


def _first_image_b64(result):
    for c in result.get("content", []):
        if c.get("type") == "image" and c.get("data"):
            return c["data"]
    return None


def _extract_field_b64(result, field):
    """Pull a base64 value out of a tool result by field name."""
    if field in ("image", "data"):
        b64 = _first_image_b64(result)
        if b64 is None:
            raise SystemExit("[mcp_client] no image content block in result")
        return b64
    # Otherwise look the key up in structuredContent, then in parsed text JSON.
    sc = result.get("structuredContent")
    if isinstance(sc, dict) and field in sc:
        return sc[field]
    txt = _text_payload(result)
    try:
        obj = json.loads(txt)
    except (ValueError, TypeError):
        raise SystemExit(
            f"[mcp_client] result text is not JSON; cannot read field '{field}'"
        )
    # Defensive: unwrap one extra JSON layer in case a tool's string return
    # arrives double-encoded. (NOT observed in practice: string returns land as
    # plain single-encoded JSON in the text content block.)
    if isinstance(obj, str):
        try:
            obj = json.loads(obj)
        except (ValueError, TypeError):
            pass
    if isinstance(obj, dict) and field in obj:
        return obj[field]
    raise SystemExit(f"[mcp_client] field '{field}' not found in result")


def main(argv):
    ap = argparse.ArgumentParser(add_help=True, description="s&box editor MCP client")
    ap.add_argument("tool", help="tool name (entry-point or project/engine tool)")
    ap.add_argument("args", nargs="?", default=None, help="JSON object of arguments")
    ap.add_argument("--url", default=DEFAULT_URL)
    ap.add_argument("--timeout", type=float, default=60.0)
    ap.add_argument("--raw", action="store_true", help="print raw JSON-RPC result and exit")
    ap.add_argument("--no-wrap", action="store_true", help="do not auto-wrap via call_tool")
    ap.add_argument("--quiet", action="store_true", help="print only the text payload")
    ap.add_argument("--save-b64", nargs=2, metavar=("FIELD", "OUTFILE"),
                    help="decode a base64 PNG field to OUTFILE")
    opts = ap.parse_args(argv)

    arguments = None
    if opts.args:
        try:
            arguments = json.loads(opts.args)
        except ValueError as e:
            raise SystemExit(f"[mcp_client] args is not valid JSON: {e}")

    result = call(opts.url, opts.tool, arguments, opts.timeout, wrap=not opts.no_wrap)

    if opts.save_b64:
        field, outfile = opts.save_b64
        b64 = _extract_field_b64(result, field)
        d = os.path.dirname(outfile)
        if d:
            os.makedirs(d, exist_ok=True)
        with open(outfile, "wb") as f:
            f.write(base64.b64decode(b64))
        print(f"[mcp_client] wrote {outfile} ({len(b64)} b64 chars)")

    if opts.raw:
        print(json.dumps(result, indent=1))
    elif opts.quiet:
        print(_text_payload(result))
    else:
        txt = _text_payload(result)
        if result.get("isError"):
            print("[mcp_client] TOOL RETURNED isError=true", file=sys.stderr)
        if txt:
            print(txt)
        elif not opts.save_b64:
            print(json.dumps(result, indent=1))

    return 1 if result.get("isError") else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
