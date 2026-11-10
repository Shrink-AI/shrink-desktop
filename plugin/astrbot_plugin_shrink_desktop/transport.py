"""Authenticated outbound device connections with durable command/event delivery."""
import asyncio
import hashlib
import json
import secrets
import sqlite3
import time
import uuid
from pathlib import Path

from aiohttp import web


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


class Transport:
    def __init__(self, root):
        Path(root).mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(Path(root) / "desktop.db")
        self.db.row_factory = sqlite3.Row
        self.db.executescript("""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS pairs(hash TEXT PRIMARY KEY,owner TEXT,expires REAL);
        CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,name TEXT,hash TEXT,owner TEXT,revoked INTEGER DEFAULT 0,last_seen REAL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS commands(id TEXT PRIMARY KEY,device TEXT,scope TEXT,request TEXT,result TEXT,created REAL);
        CREATE TABLE IF NOT EXISTS events(device TEXT,seq INTEGER,body TEXT,PRIMARY KEY(device,seq));
        CREATE TABLE IF NOT EXISTS sessions(device TEXT,id TEXT,scope TEXT,PRIMARY KEY(device,id));
        CREATE TABLE IF NOT EXISTS bindings(device TEXT,id TEXT,scope TEXT,PRIMARY KEY(device,id));
        CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY,device TEXT,path TEXT,name TEXT,size INTEGER,expires REAL);
        CREATE TABLE IF NOT EXISTS deliveries(device TEXT,seq INTEGER,scope TEXT,state TEXT,PRIMARY KEY(device,seq,scope));
        CREATE TABLE IF NOT EXISTS watches(id TEXT PRIMARY KEY,device TEXT,session TEXT,scope TEXT,active INTEGER DEFAULT 1,last TEXT DEFAULT '',checked REAL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS watch_deliveries(id TEXT PRIMARY KEY,watch TEXT,scope TEXT,facts TEXT,state TEXT,created REAL);
        UPDATE deliveries SET state='unknown' WHERE state='sending';
        UPDATE watch_deliveries SET state='unknown' WHERE state='sending';
        """)
        self.sockets = {}
        self.waiters = {}
        self.application = web.Application(client_max_size=2_000_000)
        self.application.add_routes([web.post("/pair", self.pair), web.get("/connect", self.connect),
                                     web.get("/health", self.health), web.get("/files/{id}", self.download)])
        self.runner = None

    async def health(self, request):
        return web.json_response({"ok": True})

    async def download(self, request):
        device = request.headers.get("X-Device-Id", "")
        token = request.headers.get("Authorization", "").removeprefix("Bearer ")
        row = self.db.execute("SELECT hash FROM devices WHERE id=? AND revoked=0", (device,)).fetchone()
        if not row or not secrets.compare_digest(row["hash"], digest(token)):
            raise web.HTTPUnauthorized()
        file = self.db.execute("SELECT * FROM files WHERE id=? AND device=? AND expires>?",
                               (request.match_info["id"], device, time.time())).fetchone()
        if not file or not Path(file["path"]).is_file():
            raise web.HTTPNotFound()
        return web.FileResponse(file["path"])

    def rows(self, sql, args=()):
        return [dict(r) for r in self.db.execute(sql, args)]

    @staticmethod
    def scope(scope):
        if not scope.get("admin") or not scope.get("sender") or not scope.get("session"):
            raise PermissionError("Administrator and originating session required")
        return json.dumps({k: str(scope[k]) for k in ("sender", "session")}, sort_keys=True)

    def pairing(self, scope):
        self.scope(scope)
        code = secrets.token_urlsafe(18)
        with self.db:
            self.db.execute("DELETE FROM pairs WHERE expires<?", (time.time(),))
            self.db.execute("INSERT INTO pairs VALUES(?,?,?)", (digest(code), str(scope["sender"]), time.time() + 300))
        return {"code": code, "expires_in": 300}

    async def pair(self, request):
        body = await request.json()
        code = body.get("code", "")
        if not isinstance(code, str) or len(code) > 200:
            raise web.HTTPBadRequest()
        with self.db:
            row = self.db.execute("SELECT * FROM pairs WHERE hash=? AND expires>?", (digest(code), time.time())).fetchone()
            if not row:
                raise web.HTTPForbidden(text="Invalid or expired pairing code")
            self.db.execute("DELETE FROM pairs WHERE hash=?", (row["hash"],))
            token, device = secrets.token_urlsafe(32), uuid.uuid4().hex
            self.db.execute("INSERT INTO devices(id,name,hash,owner) VALUES(?,?,?,?)",
                            (device, str(body.get("name", "Windows"))[:120], digest(token), row["owner"]))
        return web.json_response({"device_id": device, "token": token})

    def device(self, device, scope):
        self.scope(scope)
        row = self.db.execute("SELECT * FROM devices WHERE id=? AND owner=? AND revoked=0",
                              (device, str(scope["sender"]))).fetchone()
        if not row:
            raise PermissionError("Device unavailable or owned by another administrator")
        return row

    def devices(self, scope):
        self.scope(scope)
        return [{**r, "online": r["id"] in self.sockets and time.time() - r["last_seen"] < 20}
                for r in self.rows("SELECT id,name,last_seen,revoked FROM devices WHERE owner=?", (str(scope["sender"]),))]

    async def revoke(self, device, scope):
        self.device(device, scope)
        with self.db:
            self.db.execute("UPDATE devices SET revoked=1 WHERE id=?", (device,))
        if ws := self.sockets.get(device):
            await ws.close(code=1008, message=b"Device revoked")
        return {"ok": True}

    async def connect(self, request):
        device = request.headers.get("X-Device-Id", "")
        token = request.headers.get("Authorization", "").removeprefix("Bearer ")
        row = self.db.execute("SELECT * FROM devices WHERE id=? AND revoked=0", (device,)).fetchone()
        if not row or not secrets.compare_digest(row["hash"], digest(token)):
            raise web.HTTPUnauthorized()
        ws = web.WebSocketResponse(heartbeat=15, max_msg_size=8_000_000)
        await ws.prepare(request)
        if old := self.sockets.get(device):
            await old.close(code=1012, message=b"Device reconnected")
        self.sockets[device] = ws
        seq = self.db.execute("SELECT COALESCE(MAX(seq),0) FROM events WHERE device=?", (device,)).fetchone()[0]
        await ws.send_json({"type": "ack", "seq": seq})
        for cmd in self.rows("SELECT request FROM commands WHERE device=? AND result IS NULL ORDER BY created", (device,)):
            await ws.send_json(json.loads(cmd["request"]))
        try:
            resent_at = 0
            async for message in ws:
                if message.type != web.WSMsgType.TEXT:
                    continue
                body = json.loads(message.data)
                with self.db:
                    self.db.execute("UPDATE devices SET last_seen=? WHERE id=?", (time.time(), device))
                if body.get("type") == "heartbeat" and time.time() - resent_at > 10:
                    resent_at = time.time()
                    for pending in self.rows("SELECT request FROM commands WHERE device=? AND result IS NULL ORDER BY created LIMIT 20", (device,)):
                        await ws.send_json(json.loads(pending["request"]))
                if body.get("type") == "result":
                    command = self.db.execute("SELECT * FROM commands WHERE id=? AND device=?", (body.get("id"), device)).fetchone()
                    if not command:
                        continue
                    result = body.get("result")
                    if isinstance(result, dict) and result.get("state") == "executing":
                        continue
                    with self.db:
                        changed = self.db.execute("UPDATE commands SET result=? WHERE id=? AND result IS NULL", (json.dumps(result), command["id"])).rowcount
                        action = json.loads(command["request"]).get("action")
                        if changed and action in {"start", "resume"} and isinstance(result, dict) and result.get("id") and result.get("mode") == "managed":
                            self.db.execute("INSERT OR IGNORE INTO sessions VALUES(?,?,?)", (device, result["id"], command["scope"]))
                        if changed and action == "bind_window" and isinstance(result, dict) and result.get("binding"):
                            self.db.execute("INSERT OR IGNORE INTO bindings VALUES(?,?,?)", (device, result["binding"], command["scope"]))
                    if waiter := self.waiters.pop(command["id"], None):
                        if not waiter.done():
                            waiter.set_result(result if changed else json.loads(command["result"]))
                elif body.get("type") == "event":
                    incoming = body.get("seq")
                    if not isinstance(incoming, int) or incoming < 1:
                        continue
                    if incoming <= seq:
                        await ws.send_json({"type": "ack", "seq": seq})
                        continue
                    if incoming != seq + 1:
                        await ws.send_json({"type": "ack", "seq": seq})
                        continue
                    with self.db:
                        self.db.execute("INSERT OR IGNORE INTO events VALUES(?,?,?)", (device, incoming, json.dumps(body["event"])))
                    seq = incoming
                    await ws.send_json({"type": "ack", "seq": seq})
        finally:
            if self.sockets.get(device) is ws:
                self.sockets.pop(device, None)
        return ws

    async def command(self, device, action, arguments, scope, request_id, timeout=15):
        self.device(device, scope)
        owner = self.scope(scope)
        allowed = {"sessions", "discover", "start", "resume", "read", "send", "interrupt", "approve",
                   "windows", "bind_window", "capture", "inspect_window", "window_action", "input", "project", "save_file"}
        if action not in allowed:
            raise ValueError("Unsupported device action")
        if not request_id or len(request_id) > 200:
            raise ValueError("A stable request_id is required")
        sid = arguments.get("session_id", "")
        if action in {"capture", "inspect_window", "input", "window_action"}:
            binding = self.db.execute("SELECT scope FROM bindings WHERE device=? AND id=?", (device, arguments.get("binding", ""))).fetchone()
            if not binding or binding["scope"] != owner:
                raise PermissionError("Window binding belongs to another conversation")
        if sid and not sid.startswith("external:"):
            session = self.db.execute("SELECT scope FROM sessions WHERE device=? AND id=?", (device, sid)).fetchone()
            if not session or session["scope"] != owner:
                raise PermissionError("Session belongs to another conversation")
        command_id = digest(owner + "|" + device + "|" + request_id)
        request = {"type": "command", "id": command_id, "action": action, "arguments": arguments}
        existing = self.db.execute("SELECT * FROM commands WHERE id=?", (command_id,)).fetchone()
        if existing:
            if json.loads(existing["request"]) != request:
                raise ValueError("request_id reused with different command")
            if existing["result"] is not None:
                return {"command_id": command_id, "result": json.loads(existing["result"])}
        else:
            with self.db:
                self.db.execute("INSERT INTO commands VALUES(?,?,?,?,NULL,?)", (command_id, device, owner, json.dumps(request), time.time()))
        waiter = self.waiters.setdefault(command_id, asyncio.get_running_loop().create_future())
        if ws := self.sockets.get(device):
            await ws.send_json(request)
        try:
            result = await asyncio.wait_for(asyncio.shield(waiter), timeout)
            return {"command_id": command_id, "result": result}
        except TimeoutError:
            return {"command_id": command_id, "state": "pending", "completed": False}
        finally:
            if waiter.done():
                self.waiters.pop(command_id, None)

    def result(self, command_id, scope):
        row = self.db.execute("SELECT result FROM commands WHERE id=? AND scope=?", (command_id, self.scope(scope))).fetchone()
        if not row:
            raise PermissionError("Command not found in this conversation")
        return {"command_id": command_id, "result": json.loads(row["result"]) if row["result"] else None}

    def timeline(self, device, session, scope):
        self.device(device, scope)
        owned = self.db.execute("SELECT scope FROM sessions WHERE device=? AND id=?", (device, session)).fetchone()
        if not owned or owned["scope"] != self.scope(scope):
            raise PermissionError("Session belongs to another conversation")
        events = self.rows("SELECT seq,body FROM events WHERE device=? AND json_extract(body,'$.session_id')=? ORDER BY seq DESC LIMIT 100", (device, session))
        deliveries = {r["seq"]: r["state"] for r in self.rows("SELECT seq,state FROM deliveries WHERE device=? AND scope=?", (device, self.scope(scope)))}
        return [{"seq": e["seq"], "delivery_state": deliveries.get(e["seq"]), **json.loads(e["body"])} for e in reversed(events)
                if json.loads(e["body"]).get("session_id") == session][-100:]

    async def start(self, host="0.0.0.0", port=6190):
        self.runner = web.AppRunner(self.application)
        await self.runner.setup()
        await web.TCPSite(self.runner, host, port).start()

    async def close(self):
        for ws in list(self.sockets.values()):
            await ws.close()
        if self.runner:
            await self.runner.cleanup()
        self.db.close()
