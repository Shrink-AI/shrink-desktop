# 设备协议 v1

设备通过 `POST /pair` 交换短时单次码，得到设备 ID 和令牌。服务端仅保存令牌摘要。通过 `GET /connect` 建立 WSS，认证头为 `Authorization: Bearer …`、`X-Device-Id`。撤销设备会关闭连接并拒绝后续控制。

服务端命令：

```json
{"type":"command","id":"opaque-command-id","action":"read","arguments":{"session_id":"opaque-session-id"}}
```

客户端先将命令写入 SQLite，再执行。相同 ID 和内容返回原结果；同 ID 不同内容拒绝。执行中重启记为未知，不重新执行副作用。

```json
{"type":"result","id":"opaque-command-id","result":{"state":"completed"}}
{"type":"event","seq":1,"event":{"kind":"result","session_id":"opaque-session-id","state":"completed","detail":{"text":"result"}}}
{"type":"ack","seq":1}
```

事件序号属于设备，持久化后发送。服务端仅确认已连续落库的序号；断线重放不会重复入库。重连只重发未取得结果的命令，客户端负责去重。`executing` 是中间状态，服务端继续等待最终结果。连接中断不等于 Agent 失败。

管理员身份由接入端确定。命令归属包含设备、管理员和原始会话；托管会话、窗口绑定及结果查询均核对该归属。确认操作额外绑定会话内唯一 approval ID 及十五分钟有效期。

能力：

- `computer.control`：设备、会话、窗口、监工、历史恢复、撤销。
- `computer.tasks`：兼容 `submit(task, device_id, agent, cwd, request_id)` 和 `result(task_id)`。
- `computer.project`：独立 CraftTable/Gitea 插件的受限 CLI 传输。

原生模式使用实际 native ID；本地 ID 为控制记录身份。`resume` 返回新的控制记录并标记 `history_resume`，native ID 沿用原历史。外部会话保持 `observe`，不假称连接了原生进程。

窗口操作顺序：`windows` → `bind_window(hwnd,pid,title)` → `inspect_window(binding)` → `input(binding,control_id,text,enter)`。绑定核对进程启动时间，输入前复核窗口、控件与焦点；绑定在客户端重启后失效。截图仅按需获取。

`window_action` 支持对核对过的控件执行 `invoke`，以及文本输入控件的 `escape`、`enter`、`interrupt`（Ctrl+C）。返回 `input_submitted` 只表示输入已提交，需要继续读取实际 Agent 状态验证效果。

任务事件及监工通知先保存，再调用石英表达和平台发送。表达失败使用事实文本；发送结果不明记为 unknown，避免不确定的重复通知。监工读取记录变化，不将日志变化推断为完成。
