
## 结论

项目中没有直接处理 `D:\GameData\Hearthstone\Hearthstone_Data\Plugins\x86_64\libacsdk_x64.dll` 文件本身。

已确认源码里没有出现 `libacsdk_x64.dll`、`libacsdk` 或 `acsdk` 字符串，也没有针对该 DLL 的复制、删除、替换、重命名、二进制改写、`DllImport`、`LoadLibrary` 或 native detour 逻辑。

当前项目的处理方式是在 BepInEx 加载插件后，通过 Harmony 拦截炉石托管代码中的 `AntiCheatSDK.AntiCheatManager` 方法，让这些托管入口提前返回，从而阻止它们继续执行到后面的 SDK 调用链。也就是说，移植点是托管层 Harmony Patch，不是 DLL 文件级处理。

## 源码位置

核心代码在：

```text
HsMod/Patcher.cs
```

为了方便移植，本文档同目录额外放了一份最小复用版源码：

```text
docs/AntiCheatPatch.reuse.cs
```

这份源码只保留反作弊托管入口短路 Patch，不依赖 `HsMod.Utils`、`PluginConfig` 或项目内其他功能模块。复制到另一个 BepInEx/Harmony 项目后，按目标项目实际命名空间、日志接口和加载时机调整即可。

相关入口在：

```text
HsMod/Main.cs
HsMod/Patcher.cs
```

说明文案还出现在：

```text
ReadMe.md
HsMod/WebResources/about.zhCN.html
HsMod/WebResources/about.enUS.html
docs/中文说明与桌面管理工具计划.md
```

## 运行时机

插件入口类是 `HsMod.Plugin`，继承自 BepInEx 的 `BaseUnityPlugin`。

启动流程如下：

1. BepInEx 加载 `HsMod.dll`。
2. `Plugin.Awake()` 绑定配置。
3. 如果 `isPluginEnable.Value == true`：
   - 调用 `PatchManager.PatchSettingDelegate()`。
   - 调用 `PatchManager.PatchAll()`。
4. `PatchAll()` 中无条件加载 `Patcher.PatchAntiCheat`。

对应源码点：

```text
HsMod/Main.cs:108-113
HsMod/Patcher.cs:204-208
```

因此该功能没有单独配置项。只要插件总开关 `isPluginEnable` 为 true，反作弊相关 Patch 就会随基础 Patch 一起加载。

## Patch 管理方式

`PatchManager.LoadPatch(Type loadType)` 使用：

```csharp
Harmony.CreateAndPatchAll(loadType)
```

对指定 Patch 类执行 Harmony 扫描和注入，并把 Harmony 实例保存到：

```text
PatchManager.AllHarmony
PatchManager.AllHarmonyName
```

这样后续可以通过 `UnpatchSelf()` 卸载。

`PatchAntiCheat` 有特殊异常处理：

- macOS / Unix 下如果加载失败，会记录 `Skip Mac.` 并跳过。
- 其他平台加载失败会记录错误、短暂等待，然后调用 `Utils.Quit(114514)` 退出。

对应源码点：

```text
HsMod/Patcher.cs:31-58
HsMod/Patcher.cs:250-263
```

## 被拦截的托管方法

Patch 类：

```text
Patcher.PatchAntiCheat
```

目标类型：

```text
AntiCheatSDK.AntiCheatManager
```

被拦截的方法如下：

| 目标方法 | Patch 方法 | 行为 |
| --- | --- | --- |
| `OnLoginComplete()` | `PatchAntiCheatManagerOnLoginComplete()` | Harmony Prefix 返回 `false`，跳过原方法 |
| `Shutdown()` | `PatchAntiCheatManagerShutdown()` | Harmony Prefix 返回 `false`，跳过原方法 |
| `TryCallSDK(string scriptId)` | `PatchAntiCheatManagerTryCallSDK(ref string scriptId)` | Harmony Prefix 返回 `false`，跳过原方法 |
| `CallInterfaceCallSDK(string scriptId)` | `PatchAntiCheatManagerTryCallSDK(ref string scriptId)` | 共用同一个 Prefix，返回 `false` |
| `InnerSDKMethodCall(Action<string> handler, string args)` | `PatchAntiCheatManagerInnerSDKMethodCall(ref Action<string> handler, ref string args)` | Harmony Prefix 返回 `false`，跳过原方法 |

对应源码点：

```text
HsMod/Patcher.cs:271-306
```

Harmony Prefix 返回值含义：

- 返回 `true`：继续执行原方法。
- 返回 `false`：不执行原方法。

本项目这些 Prefix 都返回 `false`，所以它们不是修改参数或替换返回值，而是直接短路目标方法。

## 依赖条件

移植到其他项目时，需要满足这些前提：

1. 目标项目运行在可注入托管插件的 Unity/Mono 环境中，例如 BepInEx 5。
2. 可以引用或解析 Harmony。
3. 可以引用包含 `AntiCheatSDK.AntiCheatManager` 的炉石客户端托管程序集。当前项目通过 `HsMod/LibHearthstone/Assembly-CSharp.dll` 等客户端程序集编译引用类型。
4. 目标客户端版本中仍存在同名类型和方法，且方法签名兼容。
5. Patch 加载时机要早于这些方法实际被调用，否则已经发生的调用无法补救。

当前项目的 `.csproj` 中没有单独引用名为 `AntiCheatSDK.dll` 的程序集；`AntiCheatSDK.AntiCheatManager` 很可能来自炉石自身托管程序集引用。

## 移植使用方式

如果另一个项目已经有 BepInEx + Harmony 的 Patch 管理结构，移植时只需要迁移这几个概念：

1. 一个启动入口，在插件初始化阶段调用 Patch 加载。
2. 一个 Patch 管理方法，等价于本项目的 `PatchManager.LoadPatch(typeof(Patcher.PatchAntiCheat))`。
3. 一个 Patch 类，目标类型为 `AntiCheatSDK.AntiCheatManager`。
4. 对目标方法使用 Harmony Prefix，并让 Prefix 返回 `false`。
5. 记录 Patch 成功和短路日志，方便确认功能是否生效。

推荐保留的日志语义：

```text
PatchAntiCheat => Patched <n> methods
AntiCheat OnLoginComplete feature is disabled.
AntiCheat Shutdown feature is disabled.
AntiCheat TryCallSDK feature is disabled.
AntiCheat InnerSDKMethodCall feature is disabled.
```

如果另一个项目没有统一 Patch 管理器，也可以只保留最小流程：

```text
插件初始化 -> 创建 Harmony 实例 -> Patch PatchAntiCheat 类型 -> 保存 Harmony 实例以便卸载
```

配套源码 `AntiCheatPatch.reuse.cs` 的最短接入示例：

```csharp
using HsMod.Reuse;

private void Awake()
{
    AntiCheatPatchInstaller.LogDebug = message => Logger.LogDebug(message);
    AntiCheatPatchInstaller.LogWarning = message => Logger.LogWarning(message);
    AntiCheatPatchInstaller.Patch();
}

private void OnDestroy()
{
    AntiCheatPatchInstaller.Unpatch();
}
```

不要把该功能设计成处理 `libacsdk_x64.dll` 路径的文件工具。当前项目没有这样的实现，按文件路径迁移会偏离原逻辑。

## 验证方式

运行时可以从 BepInEx 日志确认：

1. 出现类似 `PatchAntiCheat => Patched <n> methods` 的日志，表示 Harmony 已成功应用。
2. 登录完成、SDK 调用或关闭流程触发时，出现 `AntiCheat ... feature is disabled.` 调试日志，表示 Prefix 被执行。
3. 如果目标版本变更导致类型或方法找不到，`PatchAntiCheat` 加载会失败；Windows 下当前项目会把这类失败视为致命错误并退出。

## 常见移植问题

### 找不到 `AntiCheatSDK.AntiCheatManager`

通常是客户端程序集引用不完整或目标版本类型变化。先确认目标项目引用的 `Assembly-CSharp.dll` 是否来自同一客户端版本。

### Patch 数量为 0

说明 Harmony 没有成功识别到目标方法。检查：

- Patch 类是否被加载。
- `[HarmonyPatch]` 的目标类型、方法名、参数签名是否匹配。
- 是否引用了正确版本的 Harmony。

### 运行后仍触发 SDK 调用

可能原因：

- Patch 加载晚于目标方法调用。
- 客户端版本新增了其他调用入口。
- 目标逻辑已从托管层迁移到 native 层。

### 非 Windows 平台行为不同

当前项目对 `PatchAntiCheat` 的失败处理在 macOS / Unix 上是跳过，在 Windows 上是退出。移植时应根据目标平台决定是否保留这种策略。

## 风险与边界

- 该功能会改变游戏客户端运行逻辑，项目 README 已明确提示无法保证账号安全。
- 客户端更新后，方法名、签名或调用链变化都可能导致 Patch 失败或行为异常。
- 当前实现只处理托管入口，不处理 `libacsdk_x64.dll` 的 native 导出函数。
- 如果目标项目需要处理 native DLL 层，不属于当前项目已有功能，不能直接从这里迁移。
