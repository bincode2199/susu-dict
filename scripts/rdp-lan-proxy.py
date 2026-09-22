#!/usr/bin/env python3
"""Restrict a VM's loopback RDP port to clients from one LAN subnet."""

import ipaddress
import json
import os
import select
import socket
import socketserver
import sys
import threading
import time
from pathlib import Path


base = Path(sys.argv[1]).resolve()
config = json.loads((base / 'config/deployment.json').read_text())
listen_ip = config['rdp_lan_ip']
port = config['rdp_host_port']
allowed = ipaddress.ip_network(config['rdp_allowed_subnet'], strict=True)
pid_file = base / 'run/rdp-proxy.pid'


def qemu_alive():
    try:
        pid = int((base / 'run/qemu.pid').read_text())
        args = Path(f'/proc/{pid}/cmdline').read_bytes()
        return b'qemu-system' in args and str(base).encode() in args
    except (OSError, ValueError):
        return False


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        if ipaddress.ip_address(self.client_address[0]) not in allowed:
            return
        try:
            upstream = socket.create_connection(('127.0.0.1', port), timeout=5)
        except OSError:
            return
        with upstream:
            self.request.settimeout(None)
            upstream.settimeout(None)
            sockets = (self.request, upstream)
            while True:
                try:
                    readable, _, _ = select.select(sockets, [], [], 60)
                    if not readable:
                        if not qemu_alive():
                            return
                        continue
                    for source in readable:
                        data = source.recv(65536)
                        if not data:
                            return
                        target = upstream if source is self.request else self.request
                        target.sendall(data)
                except OSError:
                    return


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


with Server((listen_ip, port), Handler) as server:
    if not qemu_alive():
        raise RuntimeError('QEMU is not running')
    pid_file.write_text(str(os.getpid()) + '\n')
    try:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        while qemu_alive():
            time.sleep(2)
    finally:
        server.shutdown()
        pid_file.unlink(missing_ok=True)
