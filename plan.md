# AllPackagesLinker VR 镜头旋转实现计划

## 1. 目标

为 VaM 1.22.0.13 下的 AllPackagesLinker 增加 VR 镜头水平旋转：用户激活旋转模式后，推动左摇杆前后即可左右转向，获得类似桌面端按住右键拖动视角的效果。

本计划只描述实现方式，暂不修改 AllPackagesLinkerBepInEx.cs 或 DLL。

## 2. 推荐交互

交互采用简单 Toggle，不需要持续按住任何按钮。

### 最终方案：左移动摇杆按下一次进入，再按一次退出

1. 按下一次左手移动摇杆（Left Stick Click），进入镜头旋转模式。
2. 左摇杆前后推动时，镜头水平旋转；摇杆回中时停止旋转，但模式保持开启。
3. 再按一次左手移动摇杆，退出镜头旋转模式。
4. 每次实体按压只切换一次；必须松开后再次按下，不能在持续按住期间重复切换。
5. 不使用 X/Y/A/B、Trigger、Grip 或 Menu。
6. 场景加载、退出 VR、手柄断连或插件销毁时自动退出，避免状态残留。

### 不推荐

- 持续按住左摇杆、握把或 Trigger：容易疲劳，且会影响操作精度。
- 使用 X/Y/A/B 或 Menu：本功能明确排除这些按钮。
- 电容触摸门控：状态不明确且设备兼容性不稳定。
- 无门控直接读摇杆：摇杆漂移会造成镜头自转，并与 VaM 平移冲突。
- 直接转 Camera.main：VR 头显会每帧覆盖相机姿态，容易抖动或破坏双眼同步。

## 3. 已确认的项目结构

- 主源码为 AllPackagesLinkerBepInEx.cs，类为 AllPackagesLinkerBepInEx。
- Update 方法约在第 264 行，当前处理 F7/F8、Unity joystick、SteamVR action 与 OpenVR raw 热键。
- 项目已引用 Valve.VR、SteamVR.dll、SteamVR_Actions.dll、Assembly-CSharp.dll 以及 Unity VR/XR 模块。
- 已有 SteamVRCombo、SteamBool、OpenVRRawCombo、RawButton 和输入诊断方法，可复用其异常处理和日志节流方式。
- VaM 的 SuperController 中已确认存在 navigationRig、navigationRigParent、navigationPlayer、navigationCamera、centerCameraTarget、grabNavigateAction、freeMoveAction、navigationDisabled、grabNavigationRotationMultiplier 等成员；正式编码前仍需确认它们的访问级别和运行时状态。
- 现有 GetViewTransform 与 ApplyCanvasTransform 用于插件 VR 面板，不是玩家导航实现，不应把镜头旋转混入面板变换。
- 构建响应文件为 _build/refs.rsp 和 _build/refs_test.rsp。

## 4. 输入设计

### 4.1 统一输入结果

新增一个统一读取入口，例如：

    bool TryReadVrRotationInput(
        out bool togglePressed,
        out float stickY,
        out string sourceName)

输入优先级：

1. SteamVR Actions。
2. OpenVR raw controller state。
3. 经实机确认可靠后才启用 Unity Input 回退。

不要把三条输入路径的结果简单相加。每帧选择第一个有效来源，避免一次实体操作被处理两次。

### 4.2 激活键

只支持 Left Stick Click 作为 Toggle，明确排除 X/Y/A/B、Menu、Grip 和 Trigger。

SteamVR 路径优先读取 SteamVR_Actions.default_GrabNavigate 或实际绑定到左摇杆 Click 的 boolean action，并使用 GetStateDown(LeftHand)。正式实现前必须通过探针确认 GrabNavigate 在当前绑定中确实是左摇杆 Click。

OpenVR raw 路径读取左手 Axis0/Joystick 的 pressed bit，并通过当前 mask 与上一帧 mask 比较得到按下边沿。持续按住期间不得重复切换；释放后才能响应下一次按下。

### 4.3 左摇杆 Y 轴

优先从 VaM 的 freeMoveAction 或准确对应的 SteamVR_Action_Vector2 读取 LeftHand 的 GetAxis。若不可访问，则从 OpenVR 的 VRControllerState_t.rAxis 数组读取。

必须先做一次低频诊断，记录各 axis 的 x/y 值，确认实际摇杆 index。Axis0 很常见，但不能硬编码后假设所有设备一致。

只使用 Y 轴。X 轴完全交还给 VaM，避免破坏侧移。

### 4.4 激活状态机

保存 bool vrRotationModeActive 与 bool leftStickClickHeldLastFrame。检测到左摇杆 Click 的按下边沿时执行 vrRotationModeActive = !vrRotationModeActive。摇杆回中只停止角度变化，不退出模式。场景加载、失去 VR、控制器断连、传送、Possess 切换或插件销毁时强制设为 false。

## 5. 轴处理与旋转速度

建议配置默认值：

- deadzone：0.18。
- sensitivity：60 度/秒。
- autoExitDelay：1.5 秒。
- invert：false。
- smoothing：0.08 至 0.12 秒。

死区应重映射，而不只是截断：

    normalized = sign(y) * clamp01((abs(y) - deadzone) / (1 - deadzone))

每帧角度：

    deltaYaw = normalized * sensitivity * Time.unscaledDeltaTime

退出模式时立即将滤波轴值归零，不保留惯性。对异常大的 deltaTime 做上限保护，避免加载卡顿后一帧突然大角度旋转。

前推对应左转还是右转属于个人习惯，必须有 invert 配置；默认方向在第一次实机测试后确定。

第一版采用连续转向。后续可增加舒适转向模式：每次越过阈值只旋转 15、30 或 45 度，摇杆回中后才能再次触发。这对易晕用户更友好。

## 6. 正确旋转 VaM 导航 Rig

### 6.1 目标 Transform

首选 SuperController.singleton.navigationRig。如果为空，再调查 navigationRigParent 或 navigationPlayer，不能直接降级到 Camera.main。

### 6.2 旋转枢轴

镜头应围绕用户当前头显位置原地转向，而不是围绕世界原点旋转：

1. pivot 优先取 navigationCamera.position。
2. 次选 centerCameraTarget.transform.position。
3. q = Quaternion.AngleAxis(deltaYaw, Vector3.up)。
4. offset = rig.position - pivot。
5. rig.position = pivot + q * offset。
6. rig.rotation = q * rig.rotation。

在远离世界原点的位置测试。如果用户视点画圆或发生位移，说明 pivot 或 rig 层级选错。

### 6.3 Update 时机

Update 中采样输入并计算待应用的 deltaYaw。先测试同一 Update 中应用；如果 VaM 在随后更新时覆盖 navigationRig，则改在 LateUpdate 中应用已经计算好的角度。

不要同时让自定义逻辑与 VaM 原生 Grab Navigate 写同一 Transform。检测到 VaM 正在 grab navigation 或 teleport 时应暂停自定义旋转。

## 7. 配置

沿用现有 config.tsv 格式，在 LoadConfig 与 SaveConfig 中加入：

    vrRotationEnabled=1
    vrRotationActivationMode=Toggle
    vrRotationActivationButton=LeftStickClick
    vrRotationSensitivity=60
    vrRotationDeadzone=0.18
    vrRotationInvert=0
    vrRotationSnapAngle=0

缺少新键时使用默认值，保证旧 config.tsv 可直接使用。所有数值需要限制合法范围。

现有设置抽屉已经有 VR 设置区域。实机确认输入映射后可增加：

- 启用镜头旋转。
- 启用镜头旋转。
- 激活键固定为左移动摇杆按下。
- 灵敏度。
- 反转方向。
- 连续转向或舒适角度转向。

第一版可只支持配置文件，以减少 UI 改动。

## 8. 代码组织建议

- VR 旋转字段放在现有 VR 状态字段附近。
- Update 只调用 UpdateVrRotationInput，不塞入完整状态机。
- LateUpdate 可选，只负责 ApplyPendingVrYaw。
- SteamVR/OpenVR 输入读取方法放在现有输入辅助方法附近。
- GetNavigationRigTransform、GetVrRotationPivot、ApplyVrYawAroundHeadset 放在 GetViewTransform 附近，但与 VR canvas 方法明确分开。
- OnDestroy 中清除激活状态。
- LoadConfig 和 SaveConfig 添加新键。

若拆成新的 partial 源文件，必须同时更新 _build/refs.rsp 与 _build/refs_test.rsp；本功能不要求为了拆分而拆分。

## 9. 实施前的最小探针

其他模型开始编码前，先做一个只写日志、不旋转镜头的输入探针：

1. 记录左手摇杆 Click 的 Down/Held/Up 状态。
2. 记录左手摇杆各 axis 的 x/y。
3. 单击、长按、松开、再次单击，确认一次实体按压只产生一个 Down 边沿。
4. 推动左摇杆前后，确认 axis index、正负方向和回中噪声。
5. 确认 X/Y/A/B、Menu、Grip、Trigger 均不会激活本功能。
6. 探针日志必须限频，状态变化时记录即可。

完成映射验证后再实现旋转，避免出现代码正确但按键永远读不到的情况。

## 10. 冲突保护

以下情况必须暂停或退出：

- 非 VR 或控制器未连接。
- SuperController 或 navigationRig 不可用。
- navigationDisabled 或 disableAllNavigation 已启用。
- VaM 正在传送、场景加载或 Grab Navigate。
- VaM 正在使用同一个 Stick Click 执行 Grab Navigate；应避免同一按压同时启动互相冲突的导航写入。
- deltaTime、axis 或 Transform 数据异常。

反射读取私有状态时要缓存 FieldInfo/PropertyInfo。读取失败应安全禁用对应检查或整个功能，不能每帧抛异常。

## 11. 验证清单

### 构建与回归

- 用项目现有响应文件编译测试 DLL 和正式 DLL。
- 确认无新增引用缺失、C# 版本不兼容。
- F7/F8、现有 VR 菜单组合键、桌面模式和资源面板行为不变。
- 旧配置可启动，新设置重启后保留。

### VR 实机

1. 未按左移动摇杆进入模式时，左摇杆前后不触发自定义旋转。
2. 左移动摇杆按下一次进入模式，再按一次退出。
3. 长按摇杆 Click 只切换一次，不会每帧反复开关。
4. 模式开启时摇杆回中只停止旋转，不退出模式。
5. X/Y/A/B、Menu、Grip、Trigger 均不激活本功能。
5. 摇杆轻微漂移不自转，前后方向和 invert 一致。
6. 左右推杆不触发本功能。
7. 原点附近和远离原点时都只原地转向，不绕场景原点公转。
8. 不抖动、不覆盖头显实际朝向、不影响双眼同步。
9. 传送、自由移动、Possess、场景加载、打开 UI 时无跳转。
10. 非 VR、OpenVR 不可用或手柄断连时无异常。
11. 配置禁用后功能完全停止。

### 舒适性

- 默认 60 度/秒不应过快；测试 30、60、90 度/秒。
- 邀请易晕用户比较连续旋转和 30 度 Snap Turn。
- 实测长按、连按和推杆过程中误按 Click 的情况，确认 Toggle 状态稳定。

## 12. 完成标准

- 每次模式切换必须来自左移动摇杆 Click 的明确按下边沿，不要求持续按住。
- 默认操作为左移动摇杆按一次进入、前后推杆旋转、再按一次退出；不使用 X/Y/A/B、Menu、Grip 或 Trigger。
- 只改变水平 yaw，方向、速度、死区可配置。
- 围绕用户当前位置旋转，不直接控制 VR Camera。
- 至少 SteamVR 主路径可用；OpenVR 回退可用或能给出明确诊断并安全禁用。
- 不破坏 AllPackagesLinker 原有桌面与 VR 功能。
