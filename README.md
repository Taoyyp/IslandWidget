# IslandWidget · Windows 双屏低占用灵动岛 (v1.0)

基于 `.NET 10 + 原生 C#/WPF` 构建的 Windows 10/11 桌面灵动岛小工具，参考并融合了 **[BrimDeck](https://github.com/MoFeng2223/BrimDeck)** 与 **[Wisland](https://github.com/AsamiAkito/wisland)** 的交互设计、无线外设协议与毛玻璃视觉，**无 Node.js 子进程、无 Python 运行时**，专注低内存与低 CPU 占用。

---

## ✨ 核心特性

1. **双屏精准停靠与跨屏液态拉丝拖拽**
   - 原生支持 `PerMonitorV2` 高分屏多显示器环境，默认停靠在 **副屏顶部正中央**（不遮挡主屏浏览器标签栏与 IDE 标题栏；单屏自动回退主屏）。
   - 按住胶囊向屏幕内侧拉动呈现 **液态表面张力拉丝与断开回弹水滴动效**；拉断后可自由跨屏拖到任意一块屏幕的 **顶 / 底 / 左 / 右** 四条边缘自动吸附并记忆比例。
   - 支持全屏游戏/视频自动隐藏避让、双击胶囊切换「长关模式」防误触、以及鼠标穿透模式。

2. **小窗口（收起态）直接点击交互 + 自动排版**
   - **按需自动排版**：可在设置中自由勾选收起态显示的模块（AI 额度环 / CPU·GPU·网速 / 无线电量 / 快捷控制按钮 / 时钟），胶囊宽度与分隔线自动排版自适应，未选中的模块不占空间。
   - **不展开大窗即可直接操作**：
     - 收起态内置快捷按钮：一键 **系统静音/取消静音**、一键 **开关 Clash 系系统代理**（右键打开代理客户端）、一键 **主副屏秒切**。
     - 音乐收起态支持直接点击 **上一曲 / 播放暂停 / 下一曲** 并同步显示滚动双语歌词。
     - 点击收起态的 AI 额度环、硬件网速或无线电量徽章，可直达对应功能页并刷新。

3. **AI 编程工具额度直连**
   - **Antigravity**：优先直连本机语言服务（自动绕过系统代理）读取 **5 小时** 与 **本周** 剩余额度及重置倒计时；未运行时可按需回退远程接口。
   - **Codex**：读取本机 `~/.codex/auth.json`，展示 **5 小时** 与 **7 天** 窗口剩余比例与重置时间。
   - **DSH (DeepSeek)**：读取本机 `~/.dsh/.credentials.yaml`，展示可用余额、赠送余额与累计消费。
   - 收起态使用紧凑进度环展示，低额度自动变色预警（≤25% 琥珀黄，≤12% 珊瑚红）。

4. **实时硬件与网速监控（零 WMI 查询）**
   - **CPU / 内存**：通过 `GetSystemTimes` 与 `GlobalMemoryStatusEx` 内核调用零分配采集。
   - **GPU**：直连 `nvml.dll` / `nvapi64.dll` 读取独立显卡占用率、显存占用与核心温度。
   - **实时网速**：通过 `GetIfTable2` 过滤物理活动网卡（自动规避虚拟 TUN 重复计数），显示实时上传 `↑` 与下载 `↓` 速率。

5. **蓝牙与 2.4G 无线外设电量**
   - **2.4G 原生 HID 协议**：支持 VXE / VGN / ATK / 迈从 / Pulsar / Scyrox 等 Compx 17 字节协议鼠标、狼蛛/VGN/ATK 无线键盘、罗技 HID++ 2.0、标准 HID 电池特征报告及通用 2.4G 接收器状态识别（外设休眠唤醒后自动重读电量）。
   - **蓝牙经典与 BLE**：读取 Windows PnP `DEVPKEY_Bluetooth_Battery` 缓存与 BLE GATT `0x180F` 电池服务。
   - **Xbox / XInput 手柄**：读取无线游戏手柄电池档位。

6. **音乐歌词 · 剪贴板中转 · 天气小形象 · Clash 系快捷控制**
   - **音乐与滚动歌词**：同时支持 Windows SMTC 与网易云音乐本地日志无插件解码，默认仅识别音乐播放器（网易云 / QQ 音乐 / 酷狗 / 酷我 / Spotify / Apple Music / 洛雪 / foobar2000 等，自动过滤浏览器和视频播放器，可在设置中按需开启），自动匹配双语 LRC 歌词。
   - **剪贴板与拖拽文件中转站**：基于 `WM_CLIPBOARDUPDATE` 事件驱动（零轮询）记录最近 15 条文字/图片，点击即复制；任意文件拖到灵动岛自动展开暂存，支持随时拖出或一键复制全部文件。
   - **免 Key 天气与发光小形象**：内置 Open-Meteo 实时天气与 5 日预报；附带视线追随鼠标、靠近随机变换表情、点击捏脸的发光双眼小形象。
   - **快捷系统控制**：CoreAudio 系统音量/静音、DDC/CI 显示器亮度、蓝牙开关、以及 **Clash 系代理客户端（FlClash / Clash Verge / Clash Nyanpasu / Mihomo Party / Clash for Windows / v2rayN 等）自动识别与一键系统代理开关**。

---

## 🖱 快捷操作

- **悬停灵动岛**：平滑展开到上次停留的功能页；鼠标移开自动收起。
- **在收起胶囊上滚动鼠标滚轮**：循环切换 4 种收起视图（`综合灵动` / `时间天气` / `音乐歌词` / `发光小形象`）。
- **直接点击收起态快捷图标**：小窗口下直接静音、开关 Clash 代理、切屏或切歌。
- **双击收起态胶囊**：切换「长关模式」（暂停悬停自动展开，再次双击恢复）。
- **按住左键拖动**：液态拉丝跨屏拖拽，松手吸附至最近屏幕边缘。
- **拖拽文件到灵动岛**：自动展开并切入「剪贴·中转」页暂存文件。
- **右键胶囊或系统托盘图标**：一键切屏、切换四套主题（`深色` / `浅色` / `胭脂撞色` / `晴空渐变`）、鼠标穿透、设置或退出。

---

## 📦 下载与运行

### 方式一：直接下载 Release 便携版（推荐）
1. 前往本仓库右侧 **[Releases](https://github.com/Taoyyp/IslandWidget/releases)** 下载最新 `IslandWidget-v1.0.0-win-x64.zip` 并解压。
2. 确保系统已安装 [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)。
3. 双击 `IslandWidget.exe`（或 `run.cmd`）即可运行。

### 方式二：从源码编译

```powershell
git clone https://github.com/Taoyyp/IslandWidget.git
cd IslandWidget

# 编译 Release
dotnet build .\IslandWidget.csproj -c Release

# 发布便携版
dotnet publish .\IslandWidget.csproj -c Release -r win-x64 --self-contained false -o .\dist\IslandWidget
```

---

## 📄 开源许可与致谢

本项目采用 [MIT License](LICENSE) 开源。部分交互灵感、液态表面张力几何与 2.4G/蓝牙外设电量协议参考了 [BrimDeck](https://github.com/MoFeng2223/BrimDeck)（Apache-2.0）与 [Wisland](https://github.com/AsamiAkito/wisland)（MIT），特此致谢。
