# 验收方式

必须区分编译通过、协议测试和真实 Agent 执行。任何未执行的场景都不能用相邻测试替代。

## 自动测试

```powershell
dotnet build tests/DesktopFixture -c Release
dotnet publish src/Shrink.Desktop -c Release -r win-x64 --self-contained true -o publish
.\publish\Shrink.Desktop.exe --self-test
python -m pytest -q
```

Python 测试覆盖单次配对、撤销、非管理员与跨会话拒绝、双设备命令隔离、结果重复回传、断线重放、服务重启、附件鉴权和过期、表达失败、未知发送状态及监工去重。测试使用临时数据库，不连接生产服务。

## 窗口动作

运行 `tests/DesktopFixture/bin/Release/net10.0-windows/DesktopFixture.exe`。它没有业务文件或模型调用，只将输入和操作计数写入临时目录中的 `shrink-desktop-window-fixture.json`。

通过正常设备通道列出并绑定该窗口，检查控件后：

1. 对 `Invoke fixture` 执行 `window_action/invoke`。
2. 对 `Fixture input` 依次执行 `enter`、`interrupt`、`escape`。
3. 核对 fixture 中四个计数均为 1。
4. Escape 会改变窗口标题。使用旧绑定发送文本必须被拒绝，输入字段应保持空。

用户正在操作电脑时应返回暂停原因。操作结果不确定时先读 fixture，不可自动重复调用来凑计数。

## 需要人在场的 Windows 边界

锁屏验收：先绑定测试窗口，由本机使用者手动锁屏，再经设备通道调用窗口检查。窗口检查、截图和输入共用会话锁定校验；锁屏期间检查必须返回明确拒绝，WSS 会话查询应继续响应。由使用者正常解锁，核对窗口访问恢复且测试窗口内容未变。此项验证共用访问边界，不冒充锁屏期间的实际键盘输入测试。测试程序不自动解锁。

提权隔离验收：由使用者确认启动仅用于测试的提权窗口。普通权限客户端应明确拒绝无法操作的目标，不提升自身权限、不向其他前台窗口发送输入。不得把普通窗口的成功操作当作提权隔离已通过。

## Agent 与完整链路

三个 Agent 分别验证新任务、同 native ID 追加、中断和显式历史恢复。使用一次性测试目录，禁止用生产项目制造中断测试。外部原会话需要单独发现与读取，并在独立测试会话上验证输入，不能新开会话冒充接管。

分别从 QQ 和 WebUI 发起任务，核对原始会话、设备事件、真实 Agent 输出及平台发送结果。监工必须在前台请求结束及服务重载后继续。确认请求须返回原会话，批准只作用于具体会话和操作。

停止旧服务后再次检查 Agent、项目 CLI、文件传输和后台入口。开机启动项也必须检查，避免登录后旧服务重新运行。
