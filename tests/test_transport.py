import asyncio
import importlib.util
import json
from pathlib import Path

import pytest
from aiohttp.test_utils import TestClient, TestServer

spec = importlib.util.spec_from_file_location("transport", Path(__file__).parents[1] / "plugin/astrbot_plugin_shrink_desktop/transport.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
Transport = module.Transport
SCOPE = {"admin": True, "sender": "owner", "session": "private:owner"}


@pytest.mark.asyncio
async def test_pair_once_authenticate_deduplicate_and_revoke(tmp_path):
    t = Transport(tmp_path)
    client = TestClient(TestServer(t.application))
    await client.start_server()
    try:
        code = t.pairing(SCOPE)["code"]
        response = await client.post("/pair", json={"code": code, "name": "test"})
        credentials = await response.json()
        assert (await client.post("/pair", json={"code": code})).status == 403
        device = credentials["device_id"]
        headers = {"Authorization": "Bearer " + credentials["token"], "X-Device-Id": device}
        ws = await client.ws_connect("/connect", headers=headers)
        assert (await ws.receive_json())["seq"] == 0
        job = asyncio.create_task(t.command(device, "sessions", {}, SCOPE, "read-1"))
        command = await ws.receive_json()
        await ws.send_json({"type": "result", "id": command["id"], "result": []})
        result = await job
        assert result["result"] == []
        assert await t.command(device, "sessions", {}, SCOPE, "read-1") == result
        with pytest.raises(ValueError):
            await t.command(device, "windows", {}, SCOPE, "read-1")
        with pytest.raises(PermissionError):
            await t.command(device, "sessions", {}, {**SCOPE, "sender": "other"}, "new")
        await ws.send_json({"type": "event", "seq": 1, "event": {"kind": "result", "session_id": "s"}})
        assert (await ws.receive_json())["seq"] == 1
        await ws.send_json({"type": "event", "seq": 1, "event": {"kind": "result", "session_id": "s"}})
        assert (await ws.receive_json())["seq"] == 1
        assert len(t.rows("SELECT * FROM events")) == 1
        await ws.close()
        ws = await client.ws_connect("/connect", headers=headers)
        assert (await ws.receive_json())["seq"] == 1
        await t.revoke(device, SCOPE)
        assert t.devices(SCOPE)[0]["revoked"] == 1
        with pytest.raises(PermissionError):
            await t.command(device, "sessions", {}, SCOPE, "after-revoke")
    finally:
        await client.close()
        await t.close()


@pytest.mark.asyncio
async def test_cross_session_control_and_results_are_denied(tmp_path):
    t = Transport(tmp_path)
    with t.db:
        t.db.execute("INSERT INTO devices(id,name,hash,owner) VALUES('d','test','hash','owner')")
        t.db.execute("INSERT INTO sessions VALUES('d','s',?)", (t.scope(SCOPE),))
        t.db.execute("INSERT INTO commands VALUES('c','d',?,'{}','{}',0)", (t.scope(SCOPE),))
    wrong = {**SCOPE, "session": "other"}
    with pytest.raises(PermissionError):
        await t.command("d", "send", {"session_id": "s", "prompt": "bad"}, wrong, "new")
    with pytest.raises(PermissionError):
        t.result("c", wrong)
    with pytest.raises(PermissionError):
        t.pairing({**SCOPE, "admin": False})
    await t.close()


@pytest.mark.asyncio
async def test_pending_command_survives_server_restart_and_file_is_device_bound(tmp_path):
    t = Transport(tmp_path)
    client = TestClient(TestServer(t.application))
    await client.start_server()
    code = t.pairing(SCOPE)["code"]
    response = await client.post("/pair", json={"code": code})
    credentials = await response.json()
    device = credentials["device_id"]
    headers = {"Authorization": "Bearer " + credentials["token"], "X-Device-Id": device}
    pending = await t.command(device, "sessions", {}, SCOPE, "offline-command", timeout=0.01)
    assert pending["state"] == "pending"
    await client.close()
    await t.close()

    t = Transport(tmp_path)
    client = TestClient(TestServer(t.application))
    await client.start_server()
    try:
        ws = await client.ws_connect("/connect", headers=headers)
        assert (await ws.receive_json())["seq"] == 0
        command = await ws.receive_json()
        assert command["id"] == pending["command_id"]
        await ws.send_json({"type": "result", "id": command["id"], "result": {"state": "executing"}})
        await ws.send_json({"type": "event", "seq": 1, "event": {"kind": "test"}})
        await ws.receive_json()
        assert t.result(command["id"], SCOPE)["result"] is None
        await ws.send_json({"type": "result", "id": command["id"], "result": []})
        await ws.send_json({"type": "event", "seq": 2, "event": {"kind": "test"}})
        await ws.receive_json()
        assert t.result(command["id"], SCOPE)["result"] == []

        import time
        file = tmp_path / "fixture.txt"
        file.write_text("fixture")
        with t.db:
            t.db.execute("INSERT INTO files VALUES(?,?,?,?,?,?)",
                         ("f", device, str(file), file.name, 7, time.time() + 60))
        assert (await client.get("/files/f")).status == 401
        assert (await client.get("/files/f", headers={**headers, "X-Device-Id": "another"})).status == 401
        assert await (await client.get("/files/f", headers=headers)).text() == "fixture"
        with t.db:
            t.db.execute("UPDATE files SET expires=0")
        assert (await client.get("/files/f", headers=headers)).status == 404
        await ws.close()
    finally:
        await client.close()
        await t.close()


@pytest.mark.asyncio
async def test_device_cannot_complete_another_device_command_or_rebind_replayed_result(tmp_path):
    t = Transport(tmp_path)
    client = TestClient(TestServer(t.application))
    await client.start_server()
    try:
        credentials = []
        for name in ("first", "second"):
            r = await client.post("/pair", json={"code": t.pairing(SCOPE)["code"], "name": name})
            credentials.append(await r.json())
        first, second = credentials
        headers = {"Authorization": "Bearer " + second["token"], "X-Device-Id": second["device_id"]}
        original = await t.command(first["device_id"], "sessions", {}, SCOPE, "first-command", timeout=0.01)
        ws = await client.ws_connect("/connect", headers=headers)
        await ws.receive_json()
        await ws.send_json({"type": "result", "id": original["command_id"], "result": {"ok": True}})
        await ws.send_json({"type": "event", "seq": 1, "event": {"kind": "barrier"}})
        await ws.receive_json()
        assert t.result(original["command_id"], SCOPE)["result"] is None
        own = asyncio.create_task(t.command(second["device_id"], "start", {}, SCOPE, "second-command"))
        command = await ws.receive_json()
        await ws.send_json({"type": "result", "id": command["id"],
                           "result": {"id": "real", "mode": "managed"}})
        await own
        await ws.send_json({"type": "result", "id": command["id"],
                           "result": {"id": "forged", "mode": "managed", "binding": "forged"}})
        await ws.send_json({"type": "event", "seq": 2, "event": {"kind": "barrier"}})
        await ws.receive_json()
        assert [r["id"] for r in t.rows("SELECT id FROM sessions")] == ["real"]
        assert t.rows("SELECT * FROM bindings") == []
        assert t.result(command["id"], SCOPE)["result"]["id"] == "real"
        assert command["id"] not in t.waiters
        await ws.close()
    finally:
        await client.close()
        await t.close()
