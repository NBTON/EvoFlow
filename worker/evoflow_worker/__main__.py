from __future__ import annotations

import argparse
import asyncio
import json
import os
import socket
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["serve", "demo", "benchmark", "probe"])
    parser.add_argument("--home", type=Path)
    parser.add_argument("--token-stdin", action="store_true")
    parser.add_argument("--repeats", type=int, default=1)
    args = parser.parse_args()
    root = args.home or Path(os.environ.get("EVOFLOW_HOME", Path(os.environ["LOCALAPPDATA"]) / "EvoFlow"))
    if args.command == "serve":
        import uvicorn

        from .api import create_app

        if not args.token_stdin:
            parser.error("serve requires a per-launch token on standard input")
        token = sys.stdin.readline().strip()
        if len(token) < 32:
            parser.error("Per-launch authentication token is too short")
        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        sock.listen(16)
        app = create_app(root, token)
        server = uvicorn.Server(uvicorn.Config(app, host="127.0.0.1", log_level="error", access_log=False))
        app.state.shutdown_callback = lambda: setattr(server, "should_exit", True)
        print(json.dumps({"port": sock.getsockname()[1], "ready": True}), flush=True)
        # Native supervisor owns this pipe. EOF requests a graceful stop after UI/process death.
        import threading

        def watch_parent():
            sys.stdin.read()
            server.should_exit = True

        threading.Thread(target=watch_parent, daemon=True).start()
        asyncio.run(server.serve(sockets=[sock]))
    else:
        from .benchmark import main as benchmark

        asyncio.run(benchmark(args.command, root, args.repeats))


if __name__ == "__main__":
    main()
