import asyncio
import importlib.util
import json
import logging
import sys
import types
from pathlib import Path
from unittest.mock import AsyncMock

import pytest


@pytest.fixture
def plugin_module(monkeypatch):
    def module(name, **members):
        obj = types.ModuleType(name)
        obj.__dict__.update(members)
        monkeypatch.setitem(sys.modules, name, obj)
        return obj

    decorators = types.SimpleNamespace(llm_tool=lambda **_: lambda f: f,
                                       on_llm_request=lambda: lambda f: f)
    module("astrbot")
    module("astrbot.api", logger=logging.getLogger("test"))
    module("astrbot.api.event", AstrMessageEvent=object, filter=decorators)
    module("astrbot.api.star", Context=object, Star=object, register=lambda *a: lambda c: c)
    module("astrbot.api.message_components", Plain=lambda text: text)
    module("astrbot.core")
    module("astrbot.core.message")
    module("astrbot.core.message.message_event_result", MessageChain=list)
    root = Path(__file__).parents[1] / "plugin/astrbot_plugin_shrink_desktop"
    package = module("delivery_test_plugin")
    package.__path__ = [str(root)]
    spec = importlib.util.spec_from_file_location("delivery_test_plugin.main", root / "main.py")
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


@pytest.mark.asyncio
async def test_composition_failure_preserves_facts_and_checks_platform_result(plugin_module, monkeypatch):
    m = plugin_module
    client = types.SimpleNamespace(call=AsyncMock(side_effect=RuntimeError("model unavailable")))
    monkeypatch.setattr(m, "CapabilityClient", lambda: client)
    plugin = object.__new__(m.ShrinkDesktop)
    plugin.context = types.SimpleNamespace(send_message=AsyncMock(return_value=True))
    await plugin.notify({"sender": "owner", "session": "private:owner"}, "recorded result")
    plugin.context.send_message.assert_awaited_once_with("private:owner", ["recorded result"])
    plugin.context.send_message.return_value = False
    with pytest.raises(RuntimeError, match="did not accept"):
        await plugin.notify({"sender": "owner", "session": "private:owner"}, "recorded result")


@pytest.mark.asyncio
async def test_uncertain_delivery_survives_restart_without_duplicate_send(plugin_module, tmp_path, monkeypatch):
    m = plugin_module
    plugin = object.__new__(m.ShrinkDesktop)
    plugin.transport = m.Transport(tmp_path)
    scope = plugin.transport.scope({"admin": True, "sender": "owner", "session": "private:owner"})
    with plugin.transport.db:
        plugin.transport.db.execute("INSERT INTO sessions VALUES('device','session',?)", (scope,))
        plugin.transport.db.execute("INSERT INTO events VALUES('device',1,?)",
            (json.dumps({"kind": "result", "session_id": "session", "detail": {"text": "persisted"}}),))
    plugin.notify = AsyncMock(side_effect=RuntimeError("send outcome uncertain"))

    async def end_cycle(_):
        raise asyncio.CancelledError()

    monkeypatch.setattr(m.asyncio, "sleep", end_cycle)
    with pytest.raises(asyncio.CancelledError):
        await plugin.deliver_loop()
    assert plugin.notify.await_count == 1
    assert plugin.transport.rows("SELECT state FROM deliveries")[0]["state"] == "unknown"
    await plugin.transport.close()
    plugin.transport = m.Transport(tmp_path)
    with pytest.raises(asyncio.CancelledError):
        await plugin.deliver_loop()
    assert plugin.notify.await_count == 1
    assert len(plugin.transport.rows("SELECT * FROM events")) == 1
    await plugin.transport.close()


@pytest.mark.asyncio
async def test_repeated_watch_reuses_original_subscription(plugin_module, tmp_path):
    plugin = object.__new__(plugin_module.ShrinkDesktop)
    plugin.transport = plugin_module.Transport(tmp_path)
    scope = {"admin": True, "sender": "owner", "session": "private:owner"}
    with plugin.transport.db:
        plugin.transport.db.execute("INSERT INTO devices(id,name,hash,owner) VALUES('d','fixture','hash','owner')")
    plugin.transport.command = AsyncMock(return_value={"result": {"id": "s", "state": "running"}})
    first = await plugin.call("watch", {"device_id": "d", "session_id": "s"}, scope)
    second = await plugin.call("watch", {"device_id": "d", "session_id": "s"}, scope)
    assert second["watch_id"] == first["watch_id"]
    assert second["existing"] is True
    assert len(plugin.transport.rows("SELECT * FROM watches")) == 1
    await plugin.transport.close()
