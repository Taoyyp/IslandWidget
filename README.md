# IslandWidget · 双屏低占用融合灵动岛 (v2.0)

基于 `.NET 10 + 原生 C#/WPF` 深度重构的 Windows 桌面灵动岛小工具，融合了 **QuotaWidget**、**[BrimDeck](https://github.com/MoFeng2223/BrimDeck)** 与 **[Wisland](https://github.com/AsamiAkito/wisland)** 的核心交互与实用模块，**彻底移除了 Node.js 子进程、Python 运行时及翻译/股票等高开销模块**。

---

## ✨ 核心功能

1. **双屏精准停靠与跨屏液态拉丝拖拽**
   - 默认常驻在 **右副屏顶部正中央**（不遮挡左主屏浏览器标签栏或 IDE 标题栏，余光一眼看全）。
   - 按住胶囊向屏幕内部拉动会呈现 **Wisland 液态表面张力拉丝与回弹小水滴动效**；拉断后可自由跨屏拖到 **任意一块屏幕的顶 / 底 / 左 / 右边缘** 自动吸附并保存比例。
   - 展开面板右上角或右键菜单支持 **一键在左主屏 / 右副屏之间秒切**。

2. **AI 编程工具额度直连（零 Node 子进程）**
   - **Antigravity**：优先直连本机语言服务（`127.0.0.1` 动态端口，自动绕过系统代理）同时读取 **5 小时窗口** 与 **本周窗口** 剩余比例与重置倒计时；客户端未运行时自动使用 Google OAuth 刷新回退到远程接口。
   - **Codex**：读取 `%USERPROFILE%\.codex\auth.json`，展示 **5 小时** 与 **7 天** 窗口剩余额度。
   - **DSH (DeepSeek)**：读取 `%USERPROFILE%\.dsh\.credentials.yaml`，展示人民币可用总余额、赠送余额与累计消费。
   - 收起态采用 **BrimDeck 同款同心圆环**，低额度自动变色预警（≤25% 琥珀黄，≤12% 珊瑚红）。

3. **实时硬件与网速监控（零 WMI 查询）**
   - **CPU 占用**：`kernel32!GetSystemTimes` 内核时间差（微秒级零分配）。
   - **内存占用**：`kernel32!GlobalMemoryStatusEx` 实时百分比与已用/总容量 (GB)。
   - **GPU 监控**：直连 `nvml.dll` / `nvapi64.dll` 读取独立显卡（如 **RTX 3060**）核心占用率、显存占用与温度。
   - **实时网速**：`iphlpapi!GetIfTable2` 过滤物理活动网卡（自动排除虚拟 TUN 双重计数），实时显示上传 `↑` / 下载 `↓` 速率。

4. **蓝牙与 2.4G 无线外设电量**
   - **2.4G 接收器原生 HID 协议**：支持 **VXE / VGN / ATK / 迈从 / Pulsar / Scyrox 等 Compx 17 字节协议（如 `VXE Mouse 1K Dongle`）**、狼蛛/VGN/ATK 无线键盘、罗技 HID++ 2.0、标准 HID 电池特征报告，以及通用 2.4G 接收器在线状态识别（鼠标休眠时自动保留上次电量并标注“休眠”）。
   - **蓝牙经典与低功耗 (BLE)**：读取 Windows 缓存 `DEVPKEY_Bluetooth_Battery` 与 BLE GATT `0x180F` 电池服务。
   - **Xbox / XInput 无线手柄**：原生读取无线手柄电池档位。

5. **音乐滚动歌词 · 天气小形象 · 剪贴板中转 · 快捷控制**
   - **音乐与双语歌词**：同时支持 **Windows SMTC**（QQ 音乐、Spotify、Edge/Chrome/B 站视频）与 **网易云音乐本地日志无插件解码**，自动匹配滚动双语歌词；播歌时收起态可自动切换为歌词条。
   - **免 Key 天气与发光小形象**：内置 Open-Meteo 实时天气与 5 日预报；移植 Wisland **发光双眼互动小形象**（视线追踪鼠标、靠近随机变表情、点击捏脸）。
   - **剪贴板与拖拽文件中转站**：基于 `WM_CLIPBOARDUPDATE` 事件监听（零轮询）记录最近 15 条文字/图片，点击即复制；任意文件拖到灵动岛自动展开暂存，可随时拖出或一键复制全部文件。
   - **快捷系统控制**：CoreAudio 系统音量/静音、DDC/CI 显示器亮度、蓝牙开关、**桌面 FlClash 自动识别与一键开关系统代理 (`127.0.0.1:7890`)**、全屏游戏/视频自动隐藏、鼠标穿透。

---

## 🖱 快捷操作指南

- **悬停灵动岛**：平滑展开到上次停留的功能页；鼠标移开自动收起。
- **在收起态滚动鼠标滚轮**：循环切换 4 种收起视图（`综合灵动` / `时间天气` / `音乐歌词` / `发光小形象`）。
- **双击收起态胶囊**：切换 **长关模式**（悬停不再自动展开，防止误触；再次双击恢复）。
- **按住左键拖动**：液态拉丝跨屏拖拽，松手吸附最近边缘。
- **拖拽文件到胶囊上**：自动展开并切入「剪贴·中转」页暂存文件。
- **右键胶囊或托盘图标**：一键切屏、切换四套毛玻璃主题（`深色` / `浅色` / `胭脂撞色` / `晴空渐变`）、开启鼠标穿透、打开设置或退出。

---

## 🛠 编译与发布

工程路径：`D:\WORK\IslandWidget`

```powershell
# 编译调试
dotnet build .\IslandWidget.csproj -c Release

# 发布到桌面绿色目录
dotnet publish .\IslandWidget.csproj -c Release -r win-x64 --self-contained false -o "$env:USERPROFILE\Desktop\IslandWidget"
```
