# Claude 账号管家

Claude 桌面端的 Windows 小工具：一键切换多个账号，多个账号之间同步 Claude Code 会话，并显示每个账号的额度。

- 单个 exe，约 60 KB。用 .NET Framework 4.8 自带的 C# 编译器生成，不需要安装运行库，也没有第三方依赖。
- WPF 界面：无边框圆角窗口、卡片式布局、滑块开关。

## 功能

- **切换账号**：保存每个账号的登录状态（`config.json` 里的 `oauth:*` 字段和 `Network\Cookies`）。切换时先关闭 Claude，换上目标账号的登录状态，再重新打开 Claude。会等 Cookie 写到磁盘后再保存，刚登录的账号也能存完整。
- **添加账号**：先保存当前账号，再把 Claude 打开到登录页。不要用 Claude 自带的"退出登录"，那可能会让已保存的登录失效。
- **会话同步**：每个账号的会话索引在 `%APPDATA%\Claude\claude-code-sessions\<账号>\<组织>` 下各存一份，本工具让它们保持一致：同名文件以最新的为准，删除标记对所有账号生效。支持手动同步和实时同步。
- **额度显示**：
  - 用量百分比读取桌面端在本地的记录 `plan-usage-history.json`。
  - 5 小时窗口从窗口内第一条消息开始计时，时间取自 `~/.claude/projects` 里的对话记录。
  - 每周重置为每个账号单独设定的固定星期和时间，点击即可修改。
  - 可选的状态栏模式：读取 Claude Code 传给状态栏的官方 `rate_limits` 字段，得到精确值。
- **托盘**：双击打开主界面，右键可以快速切换账号、立即同步。支持开机自启和关闭时最小化到托盘。

## 编译

```powershell
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

`Claude账号管家.exe` 会生成在 `src` 的上一级目录。运行时的数据（`profiles/`、`usage/`、`current.txt`、`settings.txt`）也写在这个目录里。

## 状态栏（可选）

在 `~/.claude/settings.json` 里加入下面这段，终端里运行的 `claude` 就会把精确额度记下来：

```json
"statusLine": { "type": "command", "command": "\"<路径>/Claude账号管家.exe\" --statusline" }
```

## 注意

- `profiles/` 里存着各账号的登录凭据（由 Windows DPAPI 加密），只能自己保管，不要提交或分享。
- 只用于在自己的电脑上切换自己的账号，请遵守 Anthropic 的使用条款。
