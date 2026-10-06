# dsh-screenshot

给 **DeepSeek Harness (DSH) Web 界面**加一套快速截屏：全局热键 + 聊天输入框旁边的按钮，
一键截图、自动贴进对话。纯 Windows / .NET Framework 实现，**不需要 .NET SDK、不需要 Visual Studio、不装任何第三方库**。

> A fast screenshot toolkit for the DSH web GUI: global hotkeys, in-page capture buttons,
> QQ-style "hide the window first" capture, and an interactive window picker.
> Windows only, built with the C# compiler that ships with the OS.

---

## 功能

| 能力 | 说明 |
|---|---|
| **三种模式** | 全屏（所有显示器）/ 选窗口（悬停高亮、点击确定）/ 框选（拖拽，`Esc` 右键取消） |
| **全局热键** | 默认 `Ctrl+Alt+F1` / `F2` / `F3`，被别的程序占用时**自动换用空闲组合** |
| **聊天界面按钮** | 输入框右上角两个相机按钮，点开菜单选模式 |
| **QQ 式隐藏** | 截图前先把挡在前面的窗口藏起来，露出后面的内容，截完自动恢复 |
| **选窗口** | 鼠标悬停时高亮窗口并显示标题与尺寸，点一下就截它。用 `PrintWindow` 取窗口**自身的内容**，所以被别的窗口压住也能拍得干干净净 |
| **外观可调** | 框线颜色（10 色预设）与粗细（细/中/粗），在浏览器菜单或托盘菜单里点选，也可写配置文件 |
| **托盘菜单** | 手动截图、切换外观、打开截图文件夹、退出服务 |
| **服务保活** | DSH 启动时插件自动把常驻服务带起来，之后每 30 秒探活一次，崩了自动拉回——热键和隐藏功能不会悄悄失效 |

截图同时会复制到剪贴板，并保存为 PNG，另有一份 `latest.png` 永远指向最新一张。

---

## 为什么是常驻进程（本项目最关键的一个设计）

最直觉的做法是"给快捷方式设个快捷键，按一次启动一次 `shot.exe`"。实测**每次按键要 3.2 秒**才出图。

拆开测才发现：

- `.exe` 自身只花约 150 ms（抓帧 80 ms + 存盘）
- 那 3 秒**全花在创建进程上**，跟程序本身无关
- 佐证：同一台机器上 `cmd /c exit` 也要 1050 ms

也就是说某些环境（安全软件 / 沙箱挂钩 `CreateProcess`）里，启动任何进程都要 1–3 秒。
所以热键交给**常驻服务自己注册**（`RegisterHotKey`），截图时不创建任何新进程：

```
按一次快捷键：3200 ms  →  120 ms
```

`shot.exe` 仍保留命令行形态，方便脚本调用；偶尔用一次的话那 3 秒无所谓。

---

## 目录结构

```
tools/                  截图引擎（独立可用，不依赖 DSH）
  ShotCore.cs           核心：抓帧、覆层绘制、框选与选窗口
  ShotMain.cs           shot.exe 的命令行入口
  ShotDaemon.cs         shotd.exe 的托盘服务 + 热键 + 本地 HTTP 接口
  build.ps1             编译（用系统自带的 csc.exe）
  install-daemon.ps1    安装/修复/卸载常驻服务与开机自启
  hotkeys.ini           热键与外观配置

gui-plugin/             DSH Web 界面插件
  package.json          bundle 声明
  cordis.patch.yml      挂载声明
  config.json           插件配置（按钮、端口、留白等）
  lib/index.js          服务端 cordis 插件：注入脚本 + 代理截图请求
  lib/client.js         注入浏览器的脚本：按钮、菜单、附件注入
  install.ps1           安装/卸载插件
  selftest.mjs          服务端自测（假 ctx 跑通全部路由）
  selftest-client.mjs   客户端自测（假 DOM 验证按钮装配）
```

---

## 安装

### 前置

- Windows 10 / 11
- .NET Framework 4.x（系统自带，`csc.exe` 在 `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\`）
- 想用 GUI 按钮的话还需要 DSH（`dsh` 命令可用）

### 1) 编译

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build.ps1
```

产出 `tools\shot.exe` 与 `tools\shotd.exe`。

### 2) 安装常驻服务（热键）

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install-daemon.ps1
```

它会停掉旧实例、在启动文件夹放一个快捷方式、启动服务，并报告三个热键是否都注册成功。

> 开机自启走的是**启动文件夹**。也可以改用计划任务，但创建计划任务通常需要管理员权限。

### 3) 安装 GUI 插件

```powershell
dsh plugin --profile web add link:<本仓库 gui-plugin 目录的绝对路径>
```

或者用附带的脚本（`dsh plugin` 失败时会自动回退到手工建立 junction）：

```powershell
powershell -ExecutionPolicy Bypass -File .\gui-plugin\install.ps1
```

**装完必须重启 `dsh web`，然后 `Ctrl+F5` 强刷浏览器。**
Node 会缓存 ES module，运行中的服务不会自己发现新插件。

---

## 配置

`tools\hotkeys.ini`（首次运行自动生成，格式可参考随附的 `hotkeys.example.ini`）：

```ini
Full=Ctrl+Alt+F1                    # 热键，支持 Ctrl/Alt/Shift/Win + A-Z/0-9/F1-F24/PrintScreen...
Window=Ctrl+Alt+F2
Region=Ctrl+Alt+F3

Port=38901                          # 供 GUI 按钮调用的本地 HTTP 端口（仅监听 127.0.0.1）
HideBeforeCapture=foreground        # foreground | none | title:关键字
OutlineWidth=2                      # 框线粗细 1-20
RegionColor=#ff4040                 # 框线颜色：#RRGGBB / #RGB / 颜色名
WindowColor=#ff4040
```

- 改完在**托盘菜单**点「重新加载热键配置」，或重启服务
- 颜色和粗细也可以在**浏览器里相机按钮的菜单底部**直接点选，会立刻生效并写回该文件
- 配置的键位被别的程序占用时，服务会自动退到空闲组合，实际生效的键写在 `shots\daemon.txt`

`gui-plugin\config.json`：

```json
{
  "port": 38901,
  "alwaysInsertReference": false,   // true = 总是插入 @路径 而不是图片附件
  "referencePrefix": "shots/",
  "composerPaddingRight": null,     // 输入框右侧留白；null = 按 4 个汉字与按钮宽度自动取大
  "buttonsGap": 6,
  "plainButton": { "enabled": true, "color": null,    "hidesWindow": false },
  "hideButton":  { "enabled": true, "color": "#2f7bf6", "hidesWindow": true }
}
```

两个按钮的分工由 `hidesWindow` 决定：`true` = 截图前先隐藏挡在前面的窗口。
改完 **刷新浏览器**即可（该文件每次请求都重新读取）。

---

## 命令行

```powershell
.\tools\shot.exe Full                  # 全屏
.\tools\shot.exe Window                # 选窗口（悬停高亮 → 点击）
.\tools\shot.exe Region                # 框选
.\tools\shot.exe Full --no-sound --no-clipboard
.\tools\shot.exe Full --out C:\tmp --delay 500 --keep 50
```

退出码：`0` 成功，`2` 用户取消，`1` 出错。

本地 HTTP 接口（仅 `127.0.0.1`，供 GUI 使用）：

```
GET /shot?mode=Full|Window|Region[&hide=0|1]   → image/png（取消则 204）
GET /config                                     → 当前外观设置 JSON
GET /set?outlineWidth=4&regionColor=%23ff0000   → 应用并写回配置
GET /beacon?event=...                           → 客户端探针日志
```

> 带 `Origin` 头且来源不是 loopback 的请求会被 **403** 拒绝——避免任意网页隔空触发截屏。

---

## 自测

```powershell
node .\gui-plugin\selftest.mjs          # 服务端：假 ctx 跑通全部路由（含一次真实抓图）
node .\gui-plugin\selftest-client.mjs   # 客户端：假 DOM 验证按钮/菜单装配
```

---

## 故障排查

**热键没反应**
看 `shots\daemon.txt` 里的 `registered=`：

- `False` → 该组合被别的程序占了，改 `hotkeys.ini` 换一个（服务也会自动避让）
- 文件不存在或时间很旧 → 服务没在跑，双击 `tools\shotd.exe`
- 都是 `True` 但仍无反应 → 是否有**以管理员权限运行**的窗口获得焦点；
  管理员窗口在前台时，普通权限注册的全局热键会被系统屏蔽

**截图是模糊的缩放尺寸（如 1707×1067 而不是 2560×1600）**
说明没拿到 DPI 感知。程序启动时会调 `SetProcessDpiAwareness(2)`；150% 缩放的屏幕上应当得到原生分辨率。

**GUI 里看不到按钮**

- 装完插件**重启过 `dsh web`** 吗？再 `Ctrl+F5` 一次
- 打开 `http://127.0.0.1:<port>/dsh-shot/status`，返回 200 说明插件已加载
- 看 `shots\beacon.log`：浏览器端脚本每次加载都会回报它找到了什么（输入框类型、按钮数量、文件输入框数量）

**框选/选窗口的框线偏淡**
框线画在 22% 不透明度的压暗层上会被冲淡，属于合成方式的必然结果，改色值救不回来。
想更醒目就加大 `OutlineWidth`。

---

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install-daemon.ps1 -Uninstall
powershell -ExecutionPolicy Bypass -File .\gui-plugin\install.ps1 -Uninstall
```

---

## 实现上踩过的坑（留给后来人）

1. **PowerShell 5.1 按 ANSI 读无 BOM 的 `.ps1`**，中文会变乱码。含中文的 `.ps1` 必须带 UTF-8 BOM；
   或者像本项目这样，把中文只放在参数和 `-Encoding UTF8` 的读取里。
2. **C# 源码是 UTF-8 无 BOM**，`csc` 默认按 ANSI 解码。`build.ps1` 里显式传 `/codepage:65001`。
3. **色彩键控的分层窗口一旦改变大小，新露出的区域会短暂变黑**。
   所以高亮层做成全屏固定、只重绘变化的条带。
4. **`Form.Opacity` 会把该窗口画的一切都按比例稀释**，包括你想画得很醒目的框线。
   要让线是纯色，就得让它单独占一个全不透明的窗口。
5. **每帧重绘一个全屏分层窗口是拖拽卡顿的元凶**。内容不变就不要 `Invalidate()`。
6. **抓"被遮挡的窗口"不能截屏幕上那块矩形**——那块区域里可能有别的东西。
   正解是 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`，让窗口自己把内容画出来。
7. **判断 `PrintWindow` 是不是"什么也没画"，不能只看"像素是否完全一致"**：
   纯色窗口同样完全一致，会被误判成失败从而错误地走回退路径。
   正确做法是只把**全黑或全透明**当作失败。
8. **常驻服务一死，依赖它的能力会静默降级**（热键失效、隐藏窗口失效），
   而截图本身还能用——于是很容易被误判成"某个功能坏了"。
   让运行中的宿主（这里是 DSH 插件）定期探活并自动拉起，能根治这一类问题。

---

## License

MIT
