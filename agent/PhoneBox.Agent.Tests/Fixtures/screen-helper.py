#!/usr/bin/env python3
"""Fake screen helper used by ScreenCastTests. No portal, no encoder."""
import argparse
import os
import socket
import struct
import sys
import time


def frame(kind, payload):
    return struct.pack(">BI", kind, len(payload))[:1] + struct.pack(">I", len(payload)) + payload
    # placeholder; replaced below


def send_frame(sock, kind, payload):
    sock.sendall(bytes([kind]) + len(payload).to_bytes(4, "big") + payload)


def json_frame(obj):
    import json
    return 1, json.dumps(obj).encode()


def video_frame(epoch, seq, data):
    payload = epoch.to_bytes(4, "big") + seq.to_bytes(4, "big") + data
    return 2, payload


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--socket", required=True)
    args = ap.parse_args()

    mode_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mode.txt")
    mode = os.environ.get("SCREEN_HELPER_MODE", "success")
    try:
        with open(mode_file) as f:
            mode = f.read().strip() or mode
    except OSError:
        pass

    # connect to socket
    for _ in range(50):
        try:
            s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            s.connect(args.socket)
            break
        except (ConnectionRefusedError, FileNotFoundError):
            time.sleep(0.1)
    else:
        sys.exit(1)

    try:
        if mode == "crash":
            sys.exit(1)
        if mode == "truncated":
            s.sendall(b"\x01\x00\x00")  # partial header
            s.close()
            return
        # success / stall
        import json
        send_frame(s, 1, json.dumps({"type": "ready", "width": 64, "height": 48,
                                     "codec": "avc1.42C028", "inputAvailable": False}).encode())
        if mode == "stall":
            time.sleep(60)
            return
        send_frame(s, 1, json.dumps({"type": "media", "epoch": 1}).encode())
        send_frame(s, 2, (1).to_bytes(4, "big") + (1).to_bytes(4, "big") + b"\x00\x01\x02")
        # keep alive until socket closes
        while True:
            try:
                data = s.recv(1024)
                if not data:
                    break
            except OSError:
                break
    finally:
        try:
            s.close()
        except OSError:
            pass


if __name__ == "__main__":
    main()
