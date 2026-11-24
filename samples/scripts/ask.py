#!/usr/bin/env python3
"""Talks to Racka agents straight over their Unix sockets, with the standard library only.

    python3 samples/scripts/ask.py                       # list the running agents
    python3 samples/scripts/ask.py shaun "Mi a státusz?"  # ask and wait for the answer
    python3 samples/scripts/ask.py lamb "Fordítsd le: Hello flock" --tell

Every agent serves HTTP/JSON on $RACKA_RUNTIME_DIR/<name>.sock (default $XDG_RUNTIME_DIR/racka):
GET /status, POST /messages, POST /cancel, GET /tools, POST /tools/invoke.
"""
import argparse
import getpass
import http.client
import json
import os
import socket
import sys
import uuid


def runtime_dir():
    if os.environ.get("RACKA_RUNTIME_DIR"):
        return os.environ["RACKA_RUNTIME_DIR"]
    if os.environ.get("XDG_RUNTIME_DIR"):
        return os.path.join(os.environ["XDG_RUNTIME_DIR"], "racka")
    return f"/tmp/racka-{getpass.getuser()}"


class UnixConnection(http.client.HTTPConnection):
    """HTTP over the agent's Unix socket; the host name is the agent name."""

    def __init__(self, agent, timeout):
        super().__init__(agent, timeout=timeout)
        self.socket_path = os.path.join(runtime_dir(), agent + ".sock")

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self.socket_path)


def call(agent, method, path, body=None, timeout=10):
    conn = UnixConnection(agent, timeout)
    try:
        payload = json.dumps(body).encode() if body is not None else None
        conn.request(method, path, payload, {"Content-Type": "application/json"} if payload else {})
        response = conn.getresponse()
        text = response.read().decode()
        if response.status >= 400:
            raise RuntimeError(f"{agent}: HTTP {response.status} {text}")
        return json.loads(text) if text else None
    finally:
        conn.close()


def list_agents():
    directory = runtime_dir()
    names = sorted(f[:-5] for f in os.listdir(directory) if f.endswith(".sock")) if os.path.isdir(directory) else []
    for name in names:
        try:
            s = call(name, "GET", "/status", timeout=3)
            print(f"{s['name']:<10} {s['engine']:<12} {s['state']:<8} queue={s['queueLength']} done={s['completedTasks']}")
        except OSError:
            pass  # a stale socket of an agent that did not shut down cleanly


def ask(agent, text, tell, timeout):
    message = {
        "id": uuid.uuid4().hex[:12],
        "from": f"python:{getpass.getuser()}",
        "to": agent,
        "kind": "tell" if tell else "ask",
        "text": text,
        "conversationKey": "python:ask",
        "timeoutSeconds": timeout,
        "source": "cli",
    }
    reply = call(agent, "POST", "/messages", message, timeout=timeout + 10)
    if reply.get("queued"):
        print(f"queued {reply['id']}")
        return 0
    if reply["success"]:
        print(reply["output"])
        return 0
    print(f"error: {reply.get('error')}", file=sys.stderr)
    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("agent", nargs="?")
    parser.add_argument("text", nargs="*")
    parser.add_argument("--tell", action="store_true", help="queue the task and do not wait")
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()

    if not args.agent:
        list_agents()
        return 0
    if not args.text:
        print(json.dumps(call(args.agent, "GET", "/status"), indent=2))
        return 0
    return ask(args.agent, " ".join(args.text), args.tell, args.timeout)


if __name__ == "__main__":
    sys.exit(main())
