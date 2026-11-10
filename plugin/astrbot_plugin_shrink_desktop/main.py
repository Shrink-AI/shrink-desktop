import asyncio
import json
import time
import uuid
import hashlib
from pathlib import Path

from astrbot.api import logger
from astrbot.api.event import AstrMessageEvent, filter
from astrbot.api.star import Context, Star, register
from astrbot.api.message_components import Plain
from astrbot.core.message.message_event_result import MessageChain

from .transport import Transport
from .monitoring import notification
from .plugin_protocol import CapabilityClient, success, validate


ACTIONS = {
    "devices": "查看设备及在线状态",
    "pair": "创建五分钟有效的一次性设备配对码",
    "revoke": "撤销指定设备",
    "sessions": "查看设备托管会话",
    "discover": "按 agent 和 query 查找已有本机会话，仅观察不接管",
    "start": "指定 agent、cwd、prompt 创建托管任务",
    "resume": "原托管进程已退出时按 session_id 显式恢复历史；不接管外部活跃进程，也不重发旧指令",
    "read": "读取指定 session_id 的真实状态或原会话记录",
    "send": "向指定 session_id 追加 prompt",
    "interrupt": "中断指定 session_id",
    "approve": "回复指定 session_id 的 approval_id，allow 为真假",
    "windows": "列出本机可见窗口",
    "bind_window": "核对 hwnd、pid、title 后绑定窗口",
    "capture": "按 binding 获取当前前台目标窗口截图",
    "inspect_window": "读取 binding 窗口的可聚焦控件，输入必须指定返回的 control_id",
    "window_action": "对 binding/control_id 执行 operation：invoke 按钮、escape、interrupt(Ctrl+C)、enter；先核对目标与具体操作",
    "input": "向核对过的 binding 输入 text；enter 明确是否提交",
    "watch": "持续监工指定 session_id",
    "watches": "查看当前会话的持久监工及最近检查",
    "unwatch": "取消 watch_id 对应监工",
    "timeline": "查看托管 session_id 的任务事件",
    "result": "查询 command_id 的执行结果",
}


@register("astrbot_plugin_shrink_desktop", "Shrink", "石英 Windows 设备与 Agent 控制", "0.1.0")
class ShrinkDesktop(Star):
    def __init__(self, context: Context):
        super().__init__(context)
        self.context = context
        self.root = Path("data/plugin_data/astrbot_plugin_shrink_desktop")
        self.transport = Transport(self.root)
        self.worker = None

    async def initialize(self):
        await self.transport.start()
        self.worker = asyncio.create_task(self.deliver_loop())

    async def terminate(self):
        if self.worker:
            self.worker.cancel()
            await asyncio.gather(self.worker, return_exceptions=True)
        await self.transport.close()

    def plugin_capabilities(self):
        parameters = {"type": "object", "properties": {
            "device_id": {"type": "string"}, "request_id": {"type": "string"},
            "agent": {"type": "string", "enum": ["codex", "claude", "dsh"]},
            "cwd": {"type": "string"}, "prompt": {"type": "string"},
            "session_id": {"type": "string"}, "query": {"type": "string"},
            "approval_id": {"type": "string"}, "allow": {"type": "boolean"},
            "command_id": {"type": "string"}, "watch_id": {"type": "string"},
            "hwnd": {"type": "string"}, "pid": {"type": "integer"}, "title": {"type": "string"},
            "binding": {"type": "string"}, "text": {"type": "string"}, "enter": {"type": "boolean"},
            "control_id": {"type": "string"},
            "operation": {"type": "string", "enum": ["invoke", "escape", "interrupt", "enter"]},
        }}
        return {"protocol": 1, "capabilities": [
            {"name": "computer.control", "version": 1, "description": "直接控制配对电脑上的 Agent，无中间模型",
             "actions": {name: {"description": desc, "readonly": name in {"devices", "sessions", "discover", "read", "windows", "capture", "inspect_window", "timeline", "result", "watches"},
                                "parameters": parameters} for name, desc in ACTIONS.items()}},
            {"name": "computer.tasks", "version": 1, "description": "电脑任务提交和查询",
             "actions": {
                 "submit": {"readonly": False, "description": "提交到明确设备和 Agent，task 为指令",
                            "parameters": {"type": "object", "required": ["device_id", "agent", "cwd", "task", "request_id"],
                                           "properties": {**parameters["properties"], "task": {"type": "string"}}}},
                 "result": {"readonly": True, "description": "按 task_id 查询命令结果",
                            "parameters": {"type": "object", "required": ["task_id"], "properties": {"task_id": {"type": "string"}}}},
             }},
            {"name": "computer.project", "version": 1, "description": "独立项目工具的本机 CLI 传输",
             "actions": {"call": {"readonly": False, "description": "执行已配置 CraftTable/Gitea CLI",
                         "parameters": {"type": "object", "required": ["device_id", "service", "body", "request_id"],
                                        "properties": {"device_id": {"type": "string"}, "service": {"type": "string"},
                                                       "body": {"type": "object"}, "request_id": {"type": "string"}}}}}},
        ]}

    async def invoke_capability(self, request):
        capability = request.get("capability")
        validate(request, capability, set(ACTIONS) if capability == "computer.control" else {"call"} if capability == "computer.project" else {"submit", "result"})
        if capability not in {"computer.control", "computer.tasks", "computer.project"}:
            raise ValueError("Unknown capability")
        args = dict(request["arguments"])
        action = request["action"]
        if capability == "computer.project":
            action = "project"
        if capability == "computer.tasks":
            if action == "submit":
                action = "start"
                args["prompt"] = args.pop("task")
            else:
                args["command_id"] = args.pop("task_id")
        return success(await self.call(action, args, request.get("scope") or {}))

    async def call(self, action, args, scope):
        t = self.transport
        owner = t.scope(scope)
        if action == "devices":
            return t.devices(scope)
        if action == "pair":
            return t.pairing(scope)
        if action == "result":
            return t.result(args["command_id"], scope)
        device = args.get("device_id", "")
        t.device(device, scope)
        if action == "revoke":
            return await t.revoke(device, scope)
        if action == "timeline":
            return t.timeline(device, args["session_id"], scope)
        if action == "watches":
            return t.rows("SELECT id,session,active,checked FROM watches WHERE device=? AND scope=?", (device, owner))
        if action == "watch":
            sid = args["session_id"]
            existing = t.rows("SELECT id FROM watches WHERE device=? AND session=? AND scope=? AND active=1",
                              (device, sid, owner))
            if existing:
                return {"watch_id": existing[0]["id"], "state": "watching", "existing": True}
            # Read first so an invalid target is never reported as actively monitored.
            check = await t.command(device, "read", {"session_id": sid}, scope, "watch-check-" + uuid.uuid4().hex)
            if not check.get("result") or check["result"].get("ok") is False:
                return {"ok": False, "error": "Target could not be verified", "check": check}
            watch = uuid.uuid4().hex
            with t.db:
                t.db.execute("INSERT INTO watches(id,device,session,scope,last) VALUES(?,?,?,?,?)",
                             (watch, device, sid, owner, json.dumps(check["result"], ensure_ascii=False, sort_keys=True)))
            return {"watch_id": watch, "state": "watching"}
        if action == "unwatch":
            with t.db:
                count = t.db.execute("UPDATE watches SET active=0 WHERE id=? AND device=? AND scope=?",
                                     (args["watch_id"], device, owner)).rowcount
            return {"ok": count == 1}
        body = {k: v for k, v in args.items() if k not in {"device_id", "request_id"}}
        readonly = action in {"sessions", "discover", "read", "windows", "capture", "inspect_window"}
        rid = args.get("request_id") or (uuid.uuid4().hex if readonly else scope.get("message_id"))
        response = await t.command(device, action, body, scope, str(rid or ""))
        response["task_id"] = response["command_id"]
        return response

    @filter.llm_tool(name="shrink_computer")
    async def computer(self, event: AstrMessageEvent, action: str = "devices", arguments: str = "{}"):
        """直接管理配对电脑的 Codex、Claude、DSH 及已有窗口。

        Args:
            action(string): devices/pair/sessions/discover/start/resume/read/send/interrupt/approve/windows/bind_window/inspect_window/capture/input/window_action/watch/unwatch/timeline/result/revoke
            arguments(string): JSON 对象；设备操作带 device_id；start 带 agent、cwd、prompt、request_id；会话操作带 session_id；写操作必须使用稳定且每个操作不同的 request_id。
        """
        if not event.is_admin():
            return json.dumps({"ok": False, "error": "仅管理员可控制电脑"}, ensure_ascii=False)
        scope = {"admin": True, "sender": str(event.get_sender_id()), "session": event.unified_msg_origin,
                 "message_id": str(event.message_obj.message_id)}
        try:
            return json.dumps(await self.call(action, json.loads(arguments), scope), ensure_ascii=False)
        except Exception as exc:
            return json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False)

    async def notify(self, scope, facts):
        text = facts
        try:
            response = await asyncio.wait_for(CapabilityClient().call("message.compose", "compose", {"facts": facts}, scope={**scope, "admin": True}), 30)
            text = response.get("text") or facts
        except Exception:
            logger.warning("Desktop result composition unavailable; sending recorded facts")
        sent = await asyncio.wait_for(self.context.send_message(scope["session"], MessageChain([Plain(text)])), 20)
        if not sent:
            raise RuntimeError("Platform did not accept desktop result")

    @filter.llm_tool(name="shrink_save_file_to_desktop")
    async def save_file(self, event: AstrMessageEvent, device_id: str, filename: str = ""):
        """将当前或引用消息中的指定 QQ 文件保存到配对电脑桌面，不覆盖已有文件。

        Args:
            device_id(string): devices 返回的目标设备标识。
            filename(string): 多个附件时选择文件名，单文件可留空。
        """
        scope = {"admin": event.is_admin(), "sender": str(event.get_sender_id()), "session": event.unified_msg_origin}
        self.transport.device(device_id, scope)
        files = []
        def visit(items, depth=0):
            if depth > 4:
                return
            for item in items:
                kind = getattr(item, "type", "")
                kind = str(getattr(kind, "value", kind)).lower()
                if kind == "file":
                    files.append(item)
                elif kind == "reply":
                    visit(getattr(item, "chain", []) or [], depth + 1)
        visit(event.get_messages())
        if filename:
            files = [f for f in files if getattr(f, "name", "") == filename]
        if len(files) != 1:
            return "请引用一个文件，或明确选择附件文件名。"
        component = files[0]
        source = Path(await asyncio.wait_for(component.get_file(), 40))
        size = source.stat().st_size
        if size > 50 * 1024 * 1024:
            return "附件超过 50 MiB。"
        name = getattr(component, "name", "") or source.name
        file_id = hashlib.sha256(f"{scope}|{event.message_obj.message_id}|{name}".encode()).hexdigest()
        with self.transport.db:
            self.transport.db.execute("INSERT OR IGNORE INTO files VALUES(?,?,?,?,?,?)",
                                     (file_id, device_id, str(source), name, size, time.time() + 3600))
        sha = await asyncio.to_thread(lambda: hashlib.sha256(source.read_bytes()).hexdigest())
        result = await self.transport.command(device_id, "save_file",
            {"file_id": file_id, "name": name, "size": size, "sha256": sha}, scope, "file-" + file_id)
        return json.dumps(result, ensure_ascii=False)

    @filter.on_llm_request()
    async def hint(self, event, req):
        if event.is_admin():
            req.system_prompt = (req.system_prompt or "") + (
                "\n本机 Agent 使用 shrink_computer 直接控制，不经过 bothouse。先 devices 选在线设备；"
                "用户说已有任务时用 discover(agent,query) 查原会话，再 read，不能用 start 冒充接管。"
                "仅观察会话可 watch 持续监工；发送原窗口需 windows、bind_window 核对目标，再 inspect_window 选择输入控件 control_id，按需 capture/input/window_action。"
                "客户端重启后原托管进程已退出才可显式 resume(session_id) 恢复历史；这不是接管活跃进程，不自动重发旧指令。"
                "新任务 start 必须给 agent、cwd、prompt；不明确的工作目录先发现或询问，不猜。"
                "每个写操作使用独立稳定 request_id。等待结果用 result 查询 command_id，pending 不代表失败，不重做。"
                "confirmation 必须核对具体操作与用户授权，approve 绑定 session_id、approval_id。"
                "QQ 附件保存桌面用 shrink_save_file_to_desktop，不把服务器路径说成本机路径。")

    async def deliver_loop(self):
        while True:
            try:
                t = self.transport
                # Durable events can arrive before start's result has registered the session owner.
                rows = t.rows("""SELECT e.device,e.seq,e.body,s.scope FROM events e JOIN sessions s
                    ON e.device=s.device AND json_extract(e.body,'$.session_id')=s.id
                    LEFT JOIN deliveries d ON d.device=e.device AND d.seq=e.seq AND d.scope=s.scope
                    WHERE d.state IS NULL AND json_extract(e.body,'$.kind') IN ('result','confirmation','error','disconnected')
                    ORDER BY e.seq LIMIT 20""")
                for row in rows:
                    with t.db:
                        t.db.execute("INSERT OR IGNORE INTO deliveries VALUES(?,?,?,'sending')", (row["device"], row["seq"], row["scope"]))
                    try:
                        await self.notify(json.loads(row["scope"]), "电脑任务事件：" + row["body"])
                    except Exception:
                        state = "unknown"
                        logger.exception("Desktop result delivery uncertain")
                    else:
                        state = "platform_accepted"
                    with t.db:
                        t.db.execute("UPDATE deliveries SET state=? WHERE device=? AND seq=? AND scope=?",
                                     (state, row["device"], row["seq"], row["scope"]))
                for watch in t.rows("SELECT * FROM watches WHERE active=1 AND checked<?", (time.time() - 60,)):
                    scope = {**json.loads(watch["scope"]), "admin": True}
                    with t.db:
                        t.db.execute("UPDATE watches SET checked=? WHERE id=?", (time.time(), watch["id"]))
                    check = await t.command(watch["device"], "read", {"session_id": watch["session"]}, scope, "watch-" + uuid.uuid4().hex)
                    result = check.get("result")
                    if not result or result.get("ok") is False:
                        continue
                    serialized = json.dumps(result, ensure_ascii=False, sort_keys=True)
                    previous = json.loads(watch["last"]) if watch["last"] else None
                    latest = t.rows("SELECT MAX(created) AS sent FROM watch_deliveries WHERE watch=?", (watch["id"],))[0]["sent"] or 0
                    facts = notification(previous, result, latest, time.time())
                    with t.db:
                        if facts:
                            t.db.execute("INSERT INTO watch_deliveries VALUES(?,?,?,?,'pending',?)",
                                         (uuid.uuid4().hex, watch["id"], watch["scope"],
                                          facts, time.time()))
                        t.db.execute("UPDATE watches SET last=?,checked=? WHERE id=?", (serialized, time.time(), watch["id"]))
                for delivery in t.rows("""SELECT d.* FROM watch_deliveries d JOIN watches w ON w.id=d.watch
                                          WHERE d.state='pending' AND w.active=1 ORDER BY d.created LIMIT 20"""):
                    with t.db:
                        t.db.execute("UPDATE watch_deliveries SET state='sending' WHERE id=?", (delivery["id"],))
                    try:
                        await self.notify(json.loads(delivery["scope"]), delivery["facts"])
                        state = "platform_accepted"
                    except Exception:
                        state = "unknown"
                        logger.exception("Watch delivery uncertain; saved for reconciliation")
                    with t.db:
                        t.db.execute("UPDATE watch_deliveries SET state=? WHERE id=?", (state, delivery["id"]))
            except asyncio.CancelledError:
                raise
            except Exception:
                logger.exception("Desktop delivery/monitor cycle failed")
            await asyncio.sleep(5)
