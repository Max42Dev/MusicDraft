"""Submit a long job, watch progress over the websocket, interrupt it, and report the final history status."""
import json
import sys
import time
import uuid
import urllib.request

sys.path.insert(0, __file__.rsplit("\\", 1)[0])
from spike import music_graph, post, get  # noqa: E402

try:
    import websocket  # websocket-client, if present in the embedded runtime
except ImportError:
    websocket = None

port = 8189
client = uuid.uuid4().hex
g = music_graph("epic orchestral trailer", "", "", int(time.time()), 300, "full", "spike/cancel", True)
pid = post(port, "/prompt", {"prompt": g, "client_id": client})["prompt_id"]
print("submitted", pid, "websocket-client available:", websocket is not None)
events = []
t0 = time.time()
if websocket:
    ws = websocket.create_connection(f"ws://127.0.0.1:{port}/ws?clientId={client}", timeout=5)
    while time.time() - t0 < 25:
        try:
            m = ws.recv()
        except Exception:
            continue
        if isinstance(m, str):
            d = json.loads(m)
            events.append(d["type"])
            if d["type"] in ("progress", "executing", "progress_state"):
                print(round(time.time() - t0, 1), json.dumps(d)[:200])
else:
    time.sleep(25)
urllib.request.urlopen(urllib.request.Request(f"http://127.0.0.1:{port}/interrupt", data=b"{}", method="POST"))
print("interrupt sent at", round(time.time() - t0, 1))
while pid not in get(port, f"/history/{pid}"):
    time.sleep(1)
h = get(port, f"/history/{pid}")[pid]
print("final:", h["status"]["status_str"], [m[0] for m in h["status"]["messages"]], "outputs:", list(h["outputs"].keys()))
print("event types seen:", sorted(set(events)))
