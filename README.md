# Shrink Desktop

Windows 上的石英 Agent 被控端：.NET 10、Windows Forms 托盘、SQLite、主动 WSS 连接。独立 AstrBot 插件通过能力协议与石英核心通信。没有中间助手模型。

## 组成

- `src/Shrink.Desktop`：设备凭据、命令去重、事件日志、Agent 原生连接、指定窗口输入。
- `plugin/astrbot_plugin_shrink_desktop`：配对、设备鉴权、任务归属、事件接收、QQ 通知和持续监工。
- `scripts`：Windows 启动入口及可选的石英后台 SSH 转发。
- `docs/protocol.md`：命令、事件和恢复语义。

独立安装 AstrBot 接入插件请使用 [astrbot_plugin_shrink_desktop](https://github.com/cneicy/astrbot_plugin_shrink_desktop)。本仓库中的插件目录保留配套源码与协议测试；部署时只启用一份接入插件。

相关插件：[石英核心](https://github.com/cneicy/astrbot_plugin_shrink)、[项目工具](https://github.com/cneicy/astrbot_plugin_shirnk_tools)、[未来提醒](https://github.com/cneicy/astrbot_plugin_shirnk_ext_reminder)。

## 构建与安装

需要 Windows、.NET 10 SDK。Node 24 用于 npm CLI 和 DSH 压缩会话读取。

```powershell
dotnet publish src/Shrink.Desktop -c Release -r win-x64 --self-contained true -o publish
.\publish\Shrink.Desktop.exe --self-test
```

将发布目录复制到 `%LOCALAPPDATA%\ShrinkDesktop\app`。复制独立插件目录到 AstrBot 的 `data/plugins`，安装其 `requirements.txt` 并加载。插件监听 6190；反向代理必须支持 WebSocket，HTTPS 入口代理到该端口。不要直接向公网暴露无 TLS 的端口。

在石英后台的设备页或管理员会话调用 `computer.control/pair`，在托盘配对窗口填写 HTTPS 入口和一次性码。配对码有效期五分钟。设备凭据使用当前 Windows 用户的 DPAPI 保存。

本地 `config.json` 位于 `%LOCALAPPDATA%\ShrinkDesktop`。`Agents` 以 `codex`、`claude`、`dsh` 为键，每项配置 `Executable`、`Arguments`、`Environment`。沿用本机 Agent 已有登录与模型配置；不要将凭据写进仓库。DSH 使用已安装版本的 ACP 入口，不能把普通交互 CLI 当作 ACP。

`CraftTableCli` 指向独立安装的 CLI 入口；`GiteaCli`、`GiteaHost`、`GiteaUser`、`GiteaRepositories` 配置受允许的项目工具范围。项目接入插件调用 `computer.project`，不导入被控端实现。

可选后台启动器读取同一运行目录的 `launcher.json`，字段为 `Dashboard`（本机回环 HTTP 地址）、`SshTarget`（SSH 主机别名或用户与主机）、`SshPort`、`RemotePort`。预先配置 SSH 密钥和可信主机记录。复制两个 PowerShell 脚本到运行目录，bat 放到桌面。被控端 WSS 与后台网页 SSH 通道相互独立。

## 使用边界

新任务的原生控制与外部会话观察分开显示。`discover/read` 不表示接管；外部窗口需要核对、绑定、选择实际输入控件。用户刚操作电脑、锁屏、焦点变化、只读控件或权限隔离时，输入会拒绝。

重启后的历史恢复是显式 `resume`，只用于原托管进程已退出且记录了原生 ID 的会话。不会重发上次指令。原进程仍存在时拒绝恢复，防止重复执行。

命令已提交、Agent 完成、平台接受 QQ 发送是不同事实。发送结果不确定时记为 `unknown`，不自动重复发送。任务结果和通知记录保留在数据库中用于核对。

## 测试与发布

`tests/test_transport.py` 使用临时数据库与模拟设备；安装 pytest、pytest-asyncio、aiohttp 后运行 `pytest`。`--agent-test codex|claude|dsh` 会调用真实已配置 Agent，产生临时测试会话并使用其模型额度。`--terminal-test` 是独立 Claude 输入验收窗口。

运行配置、数据库、日志、个人会话、发布目录和部署备份均不属于源码。发布前检查实际暂存内容；不要发布整个运行目录。

## 许可

AGPL-3.0，见 [LICENSE](LICENSE)。


### 管理员间设备授权

设备所有者可用 `shrink_computer` 的 `grant`、`grants`、`ungrant` 操作授予、查看和撤销授权。`grant/ungrant` 参数为 `device_id`、`grantee`（已配置的另一管理员账号）。授权允许该管理员查看和控制指定设备及其已有 Agent 会话；不允许继续转授权或撤销设备。原任务和监工仍回传各自原会话，不随授权迁移。撤销后取消尚未完成的排队命令和监工；已经送到电脑执行的操作不会自动倒退。授权及撤销保留审计，重启后仍有效。

设备列表为空只表示当前管理员没有可见设备，不表示其他人的电脑离线，不能据此否定已确认的安装或测试结果。
