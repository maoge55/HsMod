# AGENTS.md

本文是本仓库的会话接手指南，适用于根目录及其全部子目录。开始修改前先阅读本文；更详细的功能说明见 `docs/中文说明与桌面管理工具计划.md`。

## 1. 项目概览

HsMod 是基于 64 位 BepInEx 5 的炉石传说插件，仓库同时包含一个 Windows WPF 桌面管理器。

- 插件：`.NET Framework 4.8` 类库，使用 BepInEx、Harmony 和炉石托管程序集。
- 管理器：`.NET 8` WPF 自包含应用，负责安装、卸载、桥接、配置和皮肤管理。
- 本地桥接：插件 Web 服务默认监听 `http://127.0.0.1:58744/`。
- 最终交付：`dist/HsModManager-win-x64.zip`，用户解压后直接运行 `HsModManager.exe`。

## 2. 重要目录

| 路径 | 作用 |
| --- | --- |
| `HsMod/` | 插件源码。入口在 `Main.cs`，配置在 `PluginConfig.cs`，补丁集中在 `Patcher.cs`。 |
| `HsMod/WebServer.cs` | HTTP 路由和请求处理。 |
| `HsMod/WebApi.cs` | 状态、配置、皮肤目录和动作接口。 |
| `HsMod/WebPage.cs`、`HsMod/WebResources/` | 插件自带 Web 管理页面。 |
| `HsModManager/` | Windows WPF 管理器。主要界面在 `MainWindow.xaml` 和 `MainWindow.xaml.cs`。 |
| `HsModManager/Services/` | 炉石定位、安装卸载、桥接客户端、皮肤缓存。 |
| `HsModManager/Controls/` | 自定义 WPF 控件，包括可搜索皮肤下拉框。 |
| `HsModManager/Payload/` | 安装器必需载荷，目前包含 BepInEx x64 ZIP。 |
| `HsMod/LibHearthstone/` | 编译插件所需的炉石托管程序集。 |
| `HsMod/BepInExCore/` | 编译插件所需的 BepInEx/Harmony 程序集。 |
| `HsMod/UnstrippedCorlib/` | Windows 安装时复制到 `BepInEx/unstripped_corlib` 的运行库。 |
| `tools/package-hsmod-manager.ps1` | 插件编译、管理器自包含发布和 ZIP 打包的统一入口。 |
| `docs/` | 中文项目说明、移植说明和参考代码。 |
| `dist/` | 本地发布产物，已被 `.gitignore` 忽略，不得重新纳入 Git。 |

`LibHearthstone`、`BepInExCore`、`UnstrippedCorlib` 是源码构建依赖，不要因为它们是 DLL 就直接删除或统一取消跟踪。`HsModManager/Payload/BepInEx_win_x64_*.zip` 也是安装器必需输入，`.gitignore` 已为它保留例外。

## 3. 开始工作前

1. 运行 `git status --short`，保留用户已有改动，不要重置或还原不属于当前任务的文件。
2. 用 `rg` 搜索代码和文件；不要凭旧版炉石 API 名称猜测补丁入口。
3. 涉及炉石类型或方法签名时，对比当前客户端的 `Hearthstone_Data/Managed/Assembly-CSharp.dll` 与 `HsMod/LibHearthstone/Assembly-CSharp.dll`。炉石更新后签名可能变化。
4. 涉及实时行为时，先确认 `GET /api/status` 可访问，再决定是否需要用户重启或进入对局。
5. 不要默认关闭炉石。只有安装卸载流程或用户明确同意时才结束游戏进程。

## 4. 构建与打包

环境要求：

- Windows
- .NET SDK 8.x
- .NET Framework 4.8 Developer Pack
- 仓库中已有的炉石、BepInEx 和 UnstrippedCorlib 引用

单独编译插件：

```powershell
dotnet build .\HsMod\HsMod.csproj -c Release /p:PostBuildEvent=
```

单独编译管理器：

```powershell
dotnet build .\HsModManager\HsModManager.csproj -c Release
```

生成最终自包含包：

```powershell
.\tools\package-hsmod-manager.ps1
```

该脚本会：

1. 编译 `HsMod/Release/HsMod.dll`。
2. 以 `win-x64` 自包含、单文件模式发布管理器。
3. 复制插件 DLL、BepInEx ZIP、UnstrippedCorlib 和中文说明。
4. 生成 `dist/HsModManager-win-x64.zip`。

用户要求：凡修改 `HsMod/`、`HsModManager/`、安装器或打包逻辑，完成后必须主动运行打包脚本，不要只交付源码或普通 `dotnet build` 结果。纯文档修改可以不重新打包；修改 `PackageReadme.zh-CN.txt` 时必须重新打包。

当前插件构建可能出现已有的 `QRCoderUnity` 引用缺失警告。它不阻止现有发布，但不要把新增编译错误误判为该已知警告。

## 5. 最终包验收

至少检查以下内容：

- `HsModManager.exe` 存在并能启动，进程保持响应。
- `Payload/HsMod.dll` 与最新 `HsMod/Release/HsMod.dll` 的 SHA256 一致。
- `Payload/BepInEx_win_x64_5.4.23.3.zip` 存在。
- `Payload/UnstrippedCorlib/` 中包含 18 个 DLL。
- `README.zh-CN.txt` 存在。
- 记录最终 ZIP 的大小和 SHA256。

隐藏启动测试后必须结束测试用 `HsModManager` 进程，不要留下锁定 `dist` 的实例。

## 6. 安装与卸载边界

安装器实现在 `HsModManager/Services/ModInstaller.cs`。

- 自动寻找炉石目录时使用注册表、Battle.net 线索和常见安装路径。
- 若目标已有 `BepInEx`，必须采用追加模式；不得覆盖整个 BepInEx 目录。
- 若没有 BepInEx，优先使用包内 BepInEx 5 x64，缺失时才下载。
- 安装必须同时处理 BepInEx、`unstripped_corlib`、`doorstop_config.ini`、`HsMod.dll`、`HsMod.cfg` 和 `HsSkins.cfg`，不能只复制插件 DLL。
- 旧 `HsMod.dll` 先备份；已有配置默认保留。
- 卸载会关闭炉石，仅删除 HsMod DLL/备份、HsMod 配置、HsMod 工作目录和管理器皮肤缓存。
- 卸载必须保留共享 BepInEx、UnstrippedCorlib 和其他插件。
- 不要为了自动测试而对用户当前炉石目录执行真实卸载。

运行中的炉石通常会锁定已加载 DLL。更新实机插件时优先让用户完全退出炉石；任何替换都先备份，并核对源/目标 SHA256。

## 7. 桥接 API

管理器固定连接 `127.0.0.1:58744`，用户界面不暴露主机和端口输入框。主要接口：

- `GET /api/status`：插件、游戏、路径、Patch 和宠物诊断状态。
- `GET /api/config?lang=zhCN`：按分组返回配置元数据。
- `POST /config`：保存单个插件配置。
- `GET /api/skins`：从当前炉石 GameDbf 读取皮肤目录。
- `GET /api/skin-settings`：读取当前皮肤配置和 `HsSkins.cfg`。
- `POST /update`：更新 `HsSkins.cfg`。
- `POST /api/action`：执行受支持动作。

管理器启动后自动连接。首次没有皮肤缓存时会从客户端读取；成功后保存到 `%LocalAppData%/HsModManager/skin-catalog.json`。后续启动优先加载缓存，用户点击“刷新皮肤列表”才重新读取当前客户端数据库并覆盖缓存。

不要把 WebShell 暴露到管理器默认流程，也不要把 Web 端口开放到公网。

## 8. 管理器现状

主界面当前保留三个页面：

- 一键安装：单一路径输入、自动寻找、安装、卸载、可复制日志。
- 实时配置：按模块折叠显示配置；首次进入默认全部收起。
- 皮肤切换：11 个职业映射、随机皮肤、其他外观和可搜索下拉框。

“日志与桥接”页面已删除。桥接由顶部连接状态和自动重连承担，不要无理由恢复主机/端口输入框或旧日志页。

WPF 长任务统一使用现有异步按钮/线程管理模式，避免阻塞 UI 线程。延续现有配色、圆角、分组标题和响应式布局，不要另起一套视觉体系。

## 9. 皮肤与宠物

- 英雄皮肤按 11 个职业维护，`HsSkins.cfg` 支持固定映射和每局随机。
- 游戏新增且仍使用现有 GameDbf 表的皮肤，可以通过“刷新皮肤列表”读取，无需更新管理器。
- 若暴雪更改 DBF 结构、增加新类别或新增职业，需要同步更新插件和管理器。
- 管理器中的自己/对手宠物下拉框已开放，并保存 `skinPet`、`skinOpposingPet`。
- 选择自己的宠物时，管理器会自动开启上游实现依赖的 `isBgsUnlockCollectionEnable`；酒馆内没有服务器宠物实体时，由上游 `BgPetSpoofer` 的本地模型控制器补全显示。
- 宠物实现以上游当前修复为主；后续同步上游时保留管理器的宠物保存和刷新调用，不要恢复为只读下拉框。

## 10. 实机验证建议

桥接检查：

```powershell
Invoke-RestMethod http://127.0.0.1:58744/api/status | ConvertTo-Json -Depth 8
Invoke-RestMethod http://127.0.0.1:58744/api/skin-settings | ConvertTo-Json -Depth 8
```

对局相关补丁应监听完整状态变化：`HUB/TOURNAMENT -> GAMEPLAY -> CREATE_GAME`，并同时检查：

- `C:/Game/Hearthstone/BepInEx/LogOutput.log` 或状态接口返回的实际 BepInEx 路径。
- 状态接口中的 Patch 数量和目标功能字段。
- 当前炉石会话目录下的 `Power.log`。
- 用户画面确认；非空对象或正确标签不等于视觉效果已生效。

测试期间启动的监听器、管理器或辅助进程必须在结束前清理。不要停止用户的炉石进程，除非测试步骤已经明确得到同意。

## 11. Git 与文件规则

- `dist/`、`bin/`、`obj/`、Release 输出、普通 ZIP/EXE/安装包、日志、转储和备份不得提交。
- 必须保留 `HsModManager/Payload/BepInEx_win_x64_*.zip` 的 Git 例外。
- 不要使用 `git reset --hard`、`git checkout --` 等破坏性命令处理用户改动。
- 不要顺手格式化大型旧文件或重写无关代码。
- 修改应集中在任务涉及的模块，并沿用现有 BepInEx、Harmony、Web API 和 WPF 模式。
- 手工编辑使用补丁方式；生成物由构建或打包脚本产生。

## 12. 接手完成检查表

在结束一次功能开发前确认：

1. 已阅读相关源码和现有文档，不仅依据界面截图猜测。
2. 插件和管理器按改动范围编译通过。
3. 需要时完成真实炉石桥接验证，并区分“配置写入成功”和“游戏视觉生效”。
4. 已运行 `tools/package-hsmod-manager.ps1`。
5. 已核验最终 ZIP 内容、插件哈希和管理器启动响应。
6. 没有遗留测试进程，也没有覆盖其他 BepInEx 插件或用户配置。
7. 最终答复说明改动、验证结果、已知限制和可直接运行包的绝对路径。
