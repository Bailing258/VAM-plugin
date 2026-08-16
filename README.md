# AllPackagesLinker for VaM

目标：把大量 `.var` 放在 `Allpackages`，VaM 启动时只保留少量真实/已链接包在 `AddonPackages`。需要用某个包时，在 VR/桌面面板里选择它，插件会把该包和依赖链接到 `AddonPackages\_AllPackagesLinkerLinks`，不改变 `Allpackages` 原目录层级。

> 完整功能由 **BepInEx DLL** 实现；`.cslist` 只保留为提示壳，避免 VaM 沙盒阻止 `System.IO / mklink / var 解析`。

## 当前版本变化

- 1.4.1：资源索引 `index.tsv` 改为两档清理都始终保留；索引容量很小，删除后却会触发全库重扫，没有实际收益。
- 1.4.0：设置新增缓存容量统计、“清除非必要”和“清除全部”按钮；统计与删除均在后台线程执行，删除前二次确认，场景加载期间拒绝清理，且不会跟随缓存目录中的符号链接或目录联接。
- 1.4.0：“非必要”只清理 APL 缩略图、Timeline 派生文件以及临时场景/脚本/预设；“全部”额外清理 `index.tsv` 和 VaM `Cache`，但始终保留 VAR、场景源文件、用户预设、配置、收藏和默认项。清理全部后建议重启 VaM。
- 1.3.9：设置新增“初始关闭的 CUA 按需加载”，APL 场景加载时保留原 `assetUrl` 但不立即创建关闭 Atom 的 AssetBundle；Atom 首次启用时自动回到 VaM 原生加载流程，关闭设置会立即补载尚未加载的 CUA。
- 1.3.9：396 Atom / 92 CUA 场景中延迟 70 个初始关闭 CUA，最大 pending hold 从 79 降到 9；正常帧率实测约 `41.9s -> 40.1s`，隔离测试上限约 `41.9s -> 36.3s`。尾部 `Romolas.Skyboxes` 改为启用时加载，URL 保留且约 1.07 秒完成。
- 1.3.7：设置新增纹理主线程收尾挡位 `原版 4 / 均衡 8 / 高速 12 / 极限 16`；只在 VaM 场景加载期间提高每帧 `Texture2D` 收尾上限，空闲时自动保持原版 4，Harmony 指令不匹配时自动禁用并回退。
- 1.3.7：场景脚本本地化会缓存本次已解析和已确认缺失的脚本包，同一缺失包只查找、报错一次，避免大型场景产生数百条重复错误。
- 1.3.7：396 Atom 场景实测 `4/8/12/16` 档总加载约为 `41.04/41.23/39.38/39.87s`；8 档曾把 Atom 注册从 `18.75s` 降到 `13.27s`，12/16 档收益不稳定，因此默认使用均衡 8。
- 1.3.8：场景加载采样新增 `holdLoadCompleteFlags` 状态差分，记录 CustomUnityAsset 的 Atom UID、资源 URL、等待时长、最大 pending 数和最慢项，用于定位 Atom 创建结束后仍保持 `isLoading` 的真实资源瓶颈；仅观测，不会提前结束加载。
- 1.3.8：设置新增 CUA AssetBundle 调度 `顺序 8 / 均衡 8 / 高速 12 / 极限 16`；加载期间允许已完成请求越过慢队首并按 `0/2/4/8` 每帧预算派发回调，高速/极限同时增加 Worker，非加载期保持顺序，补丁不匹配时自动回退。
- 1.3.8：396 Atom / 92 CUA 场景实测 `顺序8 / 均衡8 / 高速12 / 极限16` 总加载约 `40.96 / 40.24 / 36.84 / 36.80s`；16 Worker 与 12 Worker 基本持平且主线程回调尖峰更高，因此默认高速 12。
- 1.3.6：APL 发起场景加载后每 0.25 秒监视 VaM `isLoading` 与 Atom 注册变化，仅在状态变化时记录数量、增删项、类型分布和最终稳定耗时，180 秒后自动停止，便于定位 Timeline 之后的 Atom 创建瓶颈。
- 1.3.5：加载旧版 Timeline 大场景时，无损转换对象式关键帧为 Timeline 291 原生 Optimized 编码，并按 VAR 路径、大小、修改时间和场景条目持久缓存；原 VAR 不修改，转换异常自动回退原场景。
- 1.3.5：超大场景改用流式解压文本读取，不再受 128MB 预分析上限影响；包内脚本会在 `SuperController.Load` 前本地化，脚本磁盘缓存命中时不再重复触发整库刷新。
- 1.3.4：`人物优先`现在保留场景中的全部 Person（不区分男性/女性）及其挂载子 Atom；主角选择只决定优先预热谁的皮肤。`极简人物`仍只保留所选主角。
- 1.3.4：场景页先显示完整菜单和卡片外壳，再逐帧加载当前页缩略图；VR 每帧最多解码一张，并跳过超过 5MB 的异常大缩略图，避免打开菜单时同步卡住约 5 秒。
- 1.3.4：启动时直接使用 `index.tsv` 中已有的包内预设索引，不再重复打开无预设条目的 VAR；当前 12160 包环境可消除约 33 秒的重复回扫。
- 1.3.3：场景页新增 `完整`、`人物优先`、`极简人物` 三种加载模式和主角 Person 切换；精简模式只生成临时 JSON，不修改原 VAR/场景，并可随后点击“加载其余 Atom”通过 `LoadMerge` 补齐。
- 1.3.3：选中场景停留 0.75 秒后可预链接依赖并预热主人物皮肤纹理，最多 32 张；支持 `SELF:/` 与 `.latest` 路径，加载时最多等待正在进行的预热 8 秒。
- 1.3.3：场景 JSON 读取上限提高到 128MB；预热、场景准备或主场景调度失败时会取消旧任务并清除延迟 Atom 状态，避免把剩余 Atom 合并到错误场景。
- 1.3.2：大型场景的包内脚本和 `SELF:/` 引用改为单次线性扫描/改写，不再对整份 JSON 反复执行正则；39,467,546 字符实测由 12,679 ms 降至 50 ms，改写结果逐字符一致。
- 1.3.2：场景准备阶段只在链接或本地脚本发生变化时执行一次同步包刷新；移除加载前额外两轮刷新，并修复 `FileManager.Refresh()` 与实际仅包装同一方法的 `SuperController.RescanPackages()` 被连续调用的问题。
- 1.3.2：新建链接立即登记到插件内存索引，避免场景正文补充依赖时重复创建刚刚生成的链接；场景调度固定等待由 2.2 秒缩短为 0.10 秒。
- 1.3.2：场景日志新增 `rootDepsMs`、`sceneReadMs`、`sceneRefsMs`、`localizeMs`、`refreshMs` 分段计时，便于区分插件准备耗时和 VaM 自身场景加载耗时。
- 1.3.1：修正脚本加载后打开原子插件面板的 VaM UI 定位方式；不再依赖不存在的“Plugins”按钮，改为初始化并显式显示 `MVRPluginManagerUI` 的原生插件列表和脚本 UI 容器。
- 1.3.0：结果工具栏增加“作者”可搜索下拉菜单，按 `.var` 的 `作者.包名.版本` 标识筛选场景、包内预设、服装、头发和脚本；本地预设没有包作者信息，选择作者后会隐藏。
- 1.3.0：脚本加载到原子后会等待脚本控制器就绪，自动切换到该原子的 VaM 原生编辑/Plugins 界面并打开脚本 UI；设置中可单独关闭，避免与“BepInEx 加载本插件后打开本界面”混淆。
- 1.2.7：UI 精简（深色高对比主题、顶部仅设置/关闭、形态/预设快捷模式收敛、Hub 按钮合并）；设置新增 **插件加载后自动打开界面**（`autoOpenPanelOnPluginLoad`，写入 `config.tsv`）。
- 1.2.7：资源导航去掉独立「资产」入口（并入全部）；VR 下更大点击目标与更少工具条噪音。
- 1.2.6：刷新收藏/预设列表时不再自动选择第一页第一项，避免用户准备加载 bbw 时右侧总“应用”仍误加载 111 等其他预设。
- 1.2.6：精确版本缺失但本地存在较新版本时，会额外生成请求版本名的兼容别名链接，让 VaM 自身的 meta 依赖检查也能识别，例如 Jackaroo.ShivaPose_JaR.2 可由本地 .3 提供。
- 1.2.5：包内预设缩略图改为逐帧加载，并在一次 VAR 打开中完成定位与读取，避免切换服装/头发/形态页时同步读取大量图片造成卡顿。
- 1.2.5：默认关闭场景加载前的旧链接清理，避免反复删除、重建几十个链接，并减少 BrowserAssist 出现陈旧 .meta 缓存的概率。
- 1.2.5：本地临时场景会把 SELF:/ 引用改回原 VAR 包引用，修复包内 MP3 被错误当成本地 Custom/Sounds 文件。
- 1.2.5：精确依赖版本缺失时可回退到同包的本地较新版本；包内脚本增加跨重启磁盘缓存，源 VAR 未改变时不再重复解压。
- 1.2.4：服装页改为专门展示 VaM Clothing Preset，而非单件服装资源；当前索引中有 1743 个包内服装预设。每个预设使用自己的同名 JPG/PNG，点击“应用”一次加载整套服装。
- 1.2.0：修复 Hub 查询子进程输出堵塞导致的超时/exit=-1。
- 1.2.0：Hub 下载加入总进度、单文件字节数、速度、剩余时间和取消按钮。
- 1.2.0：下载增加重试、低速超时、文件大小/ZIP 签名、精确依赖版本校验，避免下载错误版本或损坏文件。
- 1.2.0：下载完成后只快速索引新增包，不再立即全量重扫整个包库，减少 VR 卡顿。
- 1.2.0：VR 呼出手势加入释放锁存，避免一次握持中菜单反复开关；VR 打开期间每页最多 64 项（关闭后恢复桌面设置），并增加缩放、远近、上下移动和重新居中按钮。
- 1.2.1：Hub 缺失依赖默认优先写入现有的 `Allpackages\E_Vam -> E:\Vam` 联接目录；如果联接不存在，才使用配置中的下载目录。
- 1.2.1：预设增加“只模型 / 模型+服装 / 模型+服装+头发”快捷模式，并通过 `SyncLockParams` 确保 VaM 预设管理器真正应用服装/头发锁定。
- 1.2.1：分页增加 20 页动态页码窗口，始终保留首页/末页；显示第 20 页时主窗口显示第 11～30 页。原来的 ±100 改为 ±5，并保留 ±10。
- 已加入启动前预索引脚本：`PreIndex_AllPackagesLinker.bat`。
- 已加入缓存：`Saves/PluginData/AllPackagesLinker/index.tsv`。
- 已加入缩略图缓存：`Saves/PluginData/AllPackagesLinker/thumbs`。
- VaM 启动后 DLL 会先读取缓存，再做增量扫描：只重新解析新增/修改过的 `.var`。
- UI 默认缩小，并加入 `UI -` / `UI +` 按钮；缩放配置写入 `config.tsv`。

## 启动前先索引一次

关闭 VaM 后双击：

```text
Custom/Scripts/AllPackagesLinker/PreIndex_AllPackagesLinker.bat
```

它会提前扫描 `Allpackages`，读取每个 `.var` 的：

- `meta.json`
- 分类信息
- 依赖信息
- 第一个场景路径
- 缩略图/截图

并写入：

```text
Saves/PluginData/AllPackagesLinker/index.tsv
Saves/PluginData/AllPackagesLinker/thumbs/
```

我已经在当前机器上跑过一次，结果：

```text
packages=809
errors=1
```

其中一个包损坏或 zip header 异常：

```text
Allpackages/E_Vam/MrDong/MRdong.DAMIMI.1.var
A local file header is corrupt.
```

这个包需要你重新下载/替换，否则插件也无法稳定读取它。

## 正常使用

1. 先关闭 VaM。
2. 如添加了大量新包，先运行 `PreIndex_AllPackagesLinker.bat`。
3. 启动 VaM。
4. BepInEx 自动加载：

```text
BepInEx/plugins/AllPackagesLinker/AllPackagesLinker.dll
```

5. 不需要把 `AllPackagesLinker.cslist` 加为 Session Plugin。
6. 按 `F8` 或 Quest/SteamVR 默认组合键 `左摇杆按下 + A` 打开面板。
7. 用分类筛选并选择包，点击：
   - `Link Selected + Dependencies`：只链接包和依赖。
   - `Link + Load First Scene`：链接后尝试加载包内第一个场景。
   - `Rescan`：读取缓存并增量检查新/改 `.var`。
   - `Clear Links`：清空插件生成的链接缓存。

## 增量扫描逻辑

插件按 `.var` 的完整路径、文件大小、修改时间判断缓存是否有效：

- 文件没变：直接复用 `index.tsv`，不重新打开 `.var`。
- 新增文件：解析该 `.var`，提取缩略图，追加进缓存。
- 修改文件：重新解析并更新缓存。
- 删除文件：下次扫描时从缓存剔除。

所以后续你往 `Allpackages` 加新包，不需要重新读取全部包。

## Allpackages/E_Vam 链接

你这台机器上已创建：

```text
Allpackages/E_Vam  ->  E:\Vam
```

插件会跟随 `Allpackages` 下的第一层目录链接/联接继续扫描，所以：

```text
E:\Vam\子文件夹\更多子文件夹\xxx.var
```

可以被索引。为避免死循环，进入该链接后不会继续跟随第二层目录链接/联接；普通文件夹不受影响。

## 链接权限限制

插件创建链接时顺序为：

1. 文件符号链接：`mklink link.var target.var`
2. 硬链接兜底：`mklink /H link.var target.var`

注意：

- 文件符号链接通常需要开启 Windows Developer Mode，或以管理员身份运行 VaM。
- 硬链接不需要管理员权限，但只能在同一个 NTFS 分区内使用。
- 如果源包在 `E:\Vam`，而 VaM 在 `D:\Game\...`，这是跨盘：硬链接一定失败；此时必须有符号链接权限。

## 一键卸载

关闭 VaM 后双击：

```text
Custom/Scripts/AllPackagesLinker/Uninstall_AllPackagesLinker.bat
```

卸载会删除：

```text
BepInEx/plugins/AllPackagesLinker
Custom/Scripts/AllPackagesLinker
AddonPackages/_AllPackagesLinkerLinks
Saves/PluginData/AllPackagesLinker
```

卸载不会删除：

```text
Allpackages
AddonPackages 中你原本真实存在的 .var
E:\Vam 或其它被 Allpackages 链接到的外部库
```

## 日志

```text
Saves/PluginData/AllPackagesLinker/bepinex.log
BepInEx/LogOutput.log
```

## Debug 日志与热键排查

当前 DLL 是带 debug 日志的合法版本：

```text
AllPackagesLinker 1.2.2
```

除了 `F8`，还加了兜底热键：

```text
F7
```

如果按 `F8` 没反应，先试 `F7`。插件会把加载、Update 心跳、F7/F8 检测、面板创建、异常堆栈写入：

```text
Saves/PluginData/AllPackagesLinker/debug.log
Saves/PluginData/AllPackagesLinker/bepinex.log
BepInEx/LogOutput.log
```

判断方式：

- `debug.log` 没有 `Awake begin`：DLL 没被 BepInEx 加载。
- 有 `Awake begin` 但没有 `Update loop alive`：插件加载了但 Unity Update 没跑。
- 有 `Update loop alive` 但按键后没有 `Keyboard hotkey detected`：F8/F7 被拦截或窗口没焦点。
- 有 `Keyboard hotkey detected` 但没有面板：看 `OpenPanel FAILED` 或 `BuildPanel` 后面的异常。

上一个版本的 `Scan failed: A type load exception has occurred.` 大概率来自旧 Unity/.NET 环境不支持 `Tuple<>`。debug 版已经去掉 `Tuple<>`。


## 列表缩略图模式

当前版本列表每一项会直接显示缩略图：

- 按 `F8`/`F7` 打开面板后，当前页的包会立即显示小缩略图。
- 右侧大预览会自动选中当前页第一个包，不需要先手动点击。
- 点击某个包后，右侧仍会显示大图、详情和依赖信息。
- 列表优先读取 `thumbs/` 缓存；如果缓存缺失，才临时从 `.var` 读取缩略图。

## 缓存清理

设置页会异步统计两档可清理容量：

- `清除非必要`：清除 APL 的 `thumbs/`、`timeline-cache/`、临时场景、临时脚本和临时预设。`index.tsv` 与 VaM 纹理缓存保留。
- `清除全部`：在上述范围之外，再清除 VaM 根目录的 `Cache/` 内容。`index.tsv` 始终保留，不会触发不必要的全库重扫。完成后建议重启 VaM，第一次场景加载会比平时慢。

两档都不会删除 `Allpackages`、`AddonPackages` 中的真实 VAR、保存的场景/预设、`config.tsv`、收藏或默认保留列表。资源库正在扫描或场景正在准备/加载时，插件会拒绝执行缓存清理；清理完成后也不会立即刷新列表并重新生成缩略图。

## 作为 VaM 场景加载器使用

现在插件不只是“链接 var”，而是可以直接当作 VaM 场景加载器：

1. 按 `F8`/`F7` 打开面板。
2. 切到 `Scenes` 分类。
3. 当前页会直接显示缩略图。
4. 点击场景行或点击行右侧 `LOAD`。
5. 插件会自动执行：
   - 链接该场景所在 `.var`
   - 递归链接依赖
   - 刷新 VaM 包索引
   - 调用 `SuperController.Load("Creator.Package.Version:/Saves/scene/xxx.json")`

在非 `Scenes` 分类里，带场景的包行右侧也会出现 `LOAD SCENE`，点击即可直接加载该包的第一个场景。

场景详情区提供三种加载模式：

- `完整`：按原场景加载全部 Atom，也是默认模式。
- `人物优先`：先加载全部 Person、系统 Atom、灯光、轻量 Atom，以及人物直接引用或挂在人物下的 Atom；无关的 CUA、SubScene、音视频和 Browser 等重资源延后。
- `极简人物`：先只加载主角、系统 Atom 和灯光。

场景包含多个人物时，可用主角按钮循环选择 Person。精简模式加载成功后，详情区会显示“加载其余 Atom”；点击后会把本次场景剩余的 Atom 合并进来。开始加载另一个场景或主场景加载失败时，这个待合并状态会自动清除。

设置中的“选中场景后预热主人物皮肤”默认开启。它只在面板中停留选中场景时工作；若启用了“场景加载前自动清理旧链接”，为避免预热链接立即被清除，预热会自动跳过。

如果加载失败，查看：

```text
Saves/PluginData/AllPackagesLinker/debug.log
```

重点看：

```text
LoadPackageScene begin
Calling SuperController.Load
Load scene failed
```

## UI / 收藏 / 默认保留 / 清理 / 符号链接权限

当前 UI 改为 8 列卡片式：

- 每个卡片直接显示缩略图。
- 顶部 `Per -8` / `Per +8` / `64` / `500` 可设置每页数量，范围 8~500。
- `Favorites` 是收藏栏，收藏只保存文本引用，不复制 var，不额外保存大图；仍复用 `thumbs/` 缩略图缓存。
- 卡片底部：
  - `LOAD`：链接并加载场景。
  - `☆/★`：收藏/取消收藏。
  - `D/D*`：Default Keep，标记为默认保留。
- 右侧也有 `★ Favorite` 和 `Default Keep`。
- 清理框输入 `DELETE` 后点清理按钮，会删除 `_AllPackagesLinkerLinks` 下生成的 `.var`，但跳过 `Default Keep` 标记的包。

符号链接权限：

插件现在优先调用 Windows API `CreateSymbolicLinkW`，并带 Developer Mode 免管理员标志；失败后再尝试 `mklink`、硬链接，最后才复制。

要避免每次复制，请二选一：

1. 开启 Windows Developer Mode：

```text
Custom/Scripts/AllPackagesLinker/Open_Windows_Developer_Mode_Settings.bat
```

打开后把 Developer Mode / 开发人员模式设为 On。

2. 或者右键以管理员身份运行 VaM。

测试当前用户是否能创建符号链接：

```text
Custom/Scripts/AllPackagesLinker/Test_Symlink_Permission.bat
```

### 注意：PowerShell New-Item 误报权限

如果旧脚本或手动命令：

```powershell
New-Item -ItemType SymbolicLink
```

仍提示“需要管理员权限”，不代表 Developer Mode 无效。很多 PowerShell 版本不会传入 Windows 的 `ALLOW_UNPRIVILEGED_CREATE` 标志。

本插件和新版 `Test_Symlink_Permission.ps1` 使用 `CreateSymbolicLinkW + ALLOW_UNPRIVILEGED_CREATE`，这才是与插件一致的测试方式。

## OpenVR / Quest SteamVR 串流热键

本地启动文件：

```text
VaM (OpenVR).bat = START "VaM" VaM.exe -vrmode OpenVR
```

因此 Quest/SteamVR 串流现在同时走 3 套输入兜底。当前热键：

- `F8`：打开/隐藏
- `F7`：兜底打开/隐藏
- Unity legacy：`Joy14`、`JoystickButton4 + JoystickButton0/1`、`JoystickButton8 + JoystickButton0/1`
- SteamVR Actions：左手 `HoldGrab/RemoteHoldGrab/Menu/GrabNavigate` + 右手 `A(Select/UIInteract)`
- OpenVR Raw：左手 `Grip/Menu/摇杆按下` + 右手 `A/Menu/B/常见按钮`

如果仍不触发，查看：

```text
Saves/PluginData/AllPackagesLinker/debug.log
```

按手柄按钮时会记录三类输入状态：

```text
Joystick button down: JoyN
SteamVR input state: LH[...] RH/A[...]
OpenVR raw state: L(mask=0x...) R(mask=0x...)
Heartbeat ... unity=... steamvr=... openvr=...
```

如果仍不能呼出，把最后 60 行 `Saves\PluginData\AllPackagesLinker\debug.log` 发回来即可重新映射。

### Virtual Desktop / Quest 实测映射

本机日志实测你按的侧边/Grip 组合只被 Unity legacy input 识别为：

```text
Joy14
```

因此当前版本直接支持 `Joy14` 单键打开/隐藏菜单，同时保留：

```text
Joy4 + Joy0/Joy1
Joy8 + Joy0/Joy1
F8 / F7
```


## UI 固定位置说明

从 1.1.4 开始，面板呼出时会按当前视点中心放置一次，然后取消挂在头显/CenterEye 下；所以打开后不会跟着头显微抖。关闭后再次呼出，会重新固定到当时视点中央。


## 1.1.4 UI 更新

- 改为浅色/白色主题，文字改为深色。
- 面板文字改为中文。
- 清除按钮不再要求输入 DELETE，改为确认框；确认后只删除 AddonPackages\\_AllPackagesLinkerLinks 中插件生成的链接项，不删除 Allpackages 真实包，也不碰其他 AddonPackages 包。
- 缩略图网格增加左侧和缩略图周围留白，避免最左列被遮挡。

## Hub 缺失依赖下载

- 右侧 Hub 操作区新增 `下载缺失依赖到 E:\VAM`。
- 点击后会：
  1. 递归检查当前选中包在本地库和 AddonPackages 中找不到的依赖；
  2. 调用 VaM Hub `findPackages` 接口获取可下载的 `.var`；
  3. 下载到 `E:\VAM`；
  4. 刷新 VaM 包索引，并尝试重新补链当前包及依赖。
- 如果有依赖不在 Hub 托管或 Hub 没有 `downloadUrl`，状态栏会显示 `Hub无地址/仍缺失`。
- 精确版本缺失时，插件会优先使用本地同包较新版本，并为请求的旧版本名建立兼容别名链接；`.latest` 会解析为本地最高可用版本。
- 本地完全没有的依赖只能从 Hub、场景作者或原资源来源补齐。插件不会创建空 `.var` 或伪造依赖，因为那只能消除表面提示，实际脚本、纹理和 AssetBundle 仍会加载失败。

