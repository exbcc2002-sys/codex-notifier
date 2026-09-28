# Codex Notifier

Windows 11 上的 Codex 桌面通知助手。绿色悬浮球在工作时渐变为白色，显示旋转尾流与随机字符；一轮完成后显示对勾、扩散绿色光圈，并播放提示音。

仅管理当前 Windows 用户及其选定的 WSL 环境。程序不会替代 Codex，也不需要 OpenAI API Key。

## 下载与运行

适用于 Windows 11 x64。发布包见 [Releases](https://github.com/exbcc2002-sys/codex-notifier/releases)。
当前版本为 **0.1.6**，提供使用本机 .NET 的小体积构建。

1. 安装 Windows **.NET 10 Desktop Runtime（x64）**，已有对应运行时则无需重复安装。
2. 解压发布包，双击 `CodexNotifier.Desktop.exe`。
3. 保留整个文件夹，特别是 `Audio/over.wav` 和 `Bridge/`。

升级前通过托盘菜单退出旧程序，否则启动新版只会唤起已经运行的旧实例。
同一用户的设置继续保存在 `%LOCALAPPDATA%\CodexNotifier`。
换电脑后重新检测配置；自行添加的音频请连同 `Audio` 文件夹一起复制。

界面使用奶白背景、黑色文字和滑动开关，托盘菜单为圆角样式。
WSL 桥接需要目标发行版中的 Python 3 和 Windows 互操作。

## 从源码构建

需要 Windows .NET 10 SDK；WSL 脚本调用 Windows SDK，无需在 WSL 安装 .NET。
先克隆仓库并进入项目目录。

Windows PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
.\artifacts\win-x64\CodexNotifier.Desktop.exe
```

WSL：

```bash
bash scripts/build.sh
bash scripts/run.sh
```

产物位于 `artifacts/win-x64/`。构建脚本默认执行核心测试；WSL 脚本还验证跨系统转发。
在 PowerShell 中可加 `-WslDistro <发行版名>` 验证 WSL 桥接。

如需将 .NET 运行时一并打包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package.ps1
```

或在 WSL 运行 `bash scripts/package.sh`。
产物为 `artifacts/CodexNotifier-<版本>-win-x64-portable/`，另含 ZIP 和 SHA-256 校验文件。
此构建体积较大，目标电脑无需安装 .NET；仍须保留 `Audio` 文件夹。
可运行 `scripts/SmokePortable.ps1` 验证隔离启动、转发器和内置运行时加载。

`ExecutionPolicy Bypass` 仅用于本次构建进程，不修改系统执行策略。

## 第一次使用

1. 打开程序，点 **演示一轮通知** 查看工作／完成动画；点 **试听** 检查音频设备。
2. 点 **检测配置**，或输入配置目录并点 **添加 / 更新目录**。检测不会修改 Codex 配置。
3. Windows 执行的 Codex：右侧 WSL 发行版留空。WSL 执行的 Codex：填写准确发行版名，例如 `Ubuntu24.04`。
4. 当前版本支持只读会话监测，**不部署回调也可以接收新任务的开始和完成事件**。
5. 如需安装 notify 回调，点 **部署接入…**，查看实际目标及旧回调处理方式，再确认。重启 Codex 扩展使回调生效。
6. 点 **测试桥接** 检查 Windows／WSL 到当前桌面会话的转发链路。它不播放声音，也不修改 Codex 配置。

### WSL 使用 Windows 上的共享配置目录

如果 WSL 的 `CODEX_HOME` 指向 `/mnt/c/Users/<用户名>/.codex`，请在面板选择对应 Windows 目录，执行环境填写实际 WSL 发行版名。
双击 EXE 不会继承 WSL 的环境变量，需在主面板检查这一设置。
通过 `scripts/run.sh` 启动时，脚本会显式传入 `CODEX_HOME` 和当前 WSL 发行版。

保留旧回调可能产生两个不同程序的提示音。本程序的暂停开关只能控制自己的通知。若要替代旧提示音工具，请在部署前取消 **部署后继续执行原通知回调**；旧回调仍保存在恢复记录中，清除接入时会恢复。

## 操作

屏幕边缘闪光依据悬浮球所在显示器的完整屏幕尺寸，从四周向内扩散后淡出，中央区域保持透明。隐藏悬浮球时仍可使用；关闭完成光圈不会关闭屏幕边缘闪光，反之亦然。

- 按住球体拖动；位置自动保存。
- 双击球体：暂停／恢复本程序的全部通知；暂停时球体变灰。
- 右键球体：打开主面板。
- 隐藏悬浮球仅控制显示，不关闭声音。
- 主面板可选择 WAV／MP3、音量、球体大小、完成光圈和可选的屏幕边缘闪光（两者独立控制）。
- 点击主面板右上角关闭时，选择最小化到系统托盘或退出；默认选择最小化，按回车确认，按 Esc 或取消保留面板。最小化后隐藏任务栏窗口，通知和悬浮球继续运行；点击托盘图标或右键悬浮球恢复面板。
- 任务栏右键通过系统 Jump List 提供打开、开关音频、开关悬浮球、退出。
- 托盘右键提供同样的操作和总通知开关。
- 退出必须使用面板或菜单中的 **退出程序**；回调不会重新拉起已退出的桌面程序。

## 接入、移除与数据位置

程序设置、有限诊断日志和 Windows 转发器：

```text
%LOCALAPPDATA%\CodexNotifier\
  settings.json
  diagnostics.log           # 64 KiB 轮换；不记录对话正文
  relay\                    # 独立转发器；主程序搬家后仍可被调用
```

用户在面板确认部署后，所选 Codex 配置目录新增：

```text
.codex/codex-notifier/
  integration.json          # 原回调、安装回调、来源编号及恢复状态
  config.before-install.toml
  bridge.py                 # 仅 WSL 接入
```

仅修改 `config.toml` 的根级 `notify`，不改模型、权限、认证、hooks 或项目配置。现有回调可按原参数继续执行，WSL 回调仍在原 Linux 上下文中执行。

**清除接入配置…** 精确恢复原 notify，保留安装后用户新增的其他配置，同时停用这个目录的监测。若 notify 已被其他程序改动，会拒绝覆盖并提示复核。备份及恢复记录保留以便检查，未部署状态下清除则仅停用监测。

清除后也应重启 Codex 扩展：已启动的 Codex 进程可能还缓存旧 notify。

程序目录下的 `Audio/over.wav` 为默认提示音（用户提供），另附可选音频 `Audio/over_2x.wav`。选择音频默认打开 `Audio` 文件夹；选择外部 WAV／MP3 时自动复制到该文件夹，同名且内容不同的文件会另存，原文件不变。设置保存相对路径，搬动整个程序文件夹后仍可播放。恢复默认音频会重新使用 `Audio/over.wav`。旧版的 `D:\Downloads\over.wav` 设置自动切换到本地默认文件，其他旧绝对路径会尝试导入。

## 当前兼容范围与边界

- 在本机 Codex VS Code 扩展 `26.917.62051` / Codex `0.153.0` 的会话元数据上核对过 `task_started`、`task_complete` 和 `turn_aborted`。
- 会话 JSONL 不是官方保证稳定的外部接口。本版采用独立解析模块，并同时提供 notify 接入；升级 Codex 后若事件格式变化，可能需要更新解析器。
- 监测会读取 JSONL 字节流，只解析会话身份和事件字段，不持久化或上传聊天正文。子任务由会话元数据过滤，完成事件按来源／线程／轮次去重。
- 同时运行多个主对话时，某一轮结束会提示，但其他对话仍工作时球体继续回到白色状态。
- 开机／启动之前的通知不会补播，也不会将历史上未结束的会话直接判为正在工作。启动前已经开始的任务，在收到新完成事件时仍可提示。
- 不以文件停止写入或固定超时推测成功。如果 Codex 崩溃而未写结束事件，监测状态可能等待结束；重新检测可刷新监测状态。
- 扫描所有日期目录的文件元数据，读取最近三天有更新的会话，并持续跟踪已发现文件。因此早先创建、今天继续使用的对话也能被识别；不会把新发现的旧历史补播成通知。
- 旧 notify 缺少轮次编号时，交给明确的会话完成事件处理，不凭空生成编号并重复提醒。回调尚未识别为主线程时也不单独播报。
- 目标是当前桌面会话，使用 CurrentUserOnly 命名管道；没有网络监听端口。
- WSL 桥接需要 `python3`、默认 `/mnt/<drive>` 挂载与 Windows 互操作。自定义 automount 路径尚未适配。
- 本版未配置开机自启，尚未制作安装器和签名。独占全屏应用、安全桌面及复杂多屏缩放需要实机进一步确认。

## 开发与验证

```text
src/CodexNotifier.Core       配置编辑、部署恢复、事件解析、状态机、监测和 IPC
src/CodexNotifier.Desktop    WPF 面板、悬浮球、动画、音频、任务栏、托盘
src/CodexNotifier.Relay      无控制台 Windows 事件转发器
tests/CodexNotifier.Tests   可执行测试程序（临时目录与独立管道）
scripts/                   Windows／WSL 构建与启动脚本
```

实际依赖：C# / .NET 10、WPF、Windows 内置 MediaPlayer、Tomlyn 0.19.0。首版没有额外引入音频或 MVVM 框架；WAV／MP3 解码失败会在面板中提示。

`Build.ps1` 默认执行配置恢复、事件去重、增量读取、命名管道与真实 Windows 转发器检查。`build.sh` 还传入当前 WSL 发行版，执行跨系统检查；在 PowerShell 中可用 `-WslDistro Ubuntu24.04` 启用这项检查。测试程序也支持 `--wsl` 参数。测试只操作随机临时目录，不改真实 Codex 配置。

隔离的 Windows 界面渲染测试（不播放声音，不检测真实配置）：

```bash
mkdir -p artifacts/smoke
artifacts/win-x64/CodexNotifier.Desktop.exe --smoke-test \
  --data-dir "$(wslpath -w "$PWD/artifacts/smoke")"
cat artifacts/smoke/smoke-result.txt
```

截图位于 `artifacts/smoke/`。人工验收还应检查实际音箱播放、鼠标拖动／双击、多屏 DPI 和任务栏 Jump List。

## 参考

- [参考产品 codex-sound-manager](https://github.com/miao8818/codex-sound-manager)：借鉴功能和 notify 共存思路，未复制其实现或音频资产。
- [Codex 配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)
- [Codex Hooks](https://learn.chatgpt.com/docs/hooks)：Stop 可能继续执行，因此本版不把它当作最终完成信号。
- [Codex App Server](https://learn.chatgpt.com/docs/app-server)
- 第三方依赖见 `docs/THIRD-PARTY-NOTICES.md`。

## 许可证

项目代码使用 [MIT License](LICENSE)。第三方组件保留各自许可证，见 [第三方声明](docs/THIRD-PARTY-NOTICES.md)。
`Audio/over.wav` 为维护者提供的音频，单独说明见 [Audio/README.md](Audio/README.md)，不自动归入代码的 MIT 授权。
