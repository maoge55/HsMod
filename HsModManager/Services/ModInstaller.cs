using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using HsModManager.Models;

namespace HsModManager.Services;

public sealed class ModInstaller
{
    private readonly HttpClient _http = new();

    public ModInstaller()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HsModManager/1.0");
    }

    public async Task<InstallResult> InstallAsync(string hearthstoneRoot, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (!HearthstoneLocator.LooksLikeHearthstoneRoot(hearthstoneRoot))
        {
            return new InstallResult
            {
                Success = false,
                HearthstonePath = hearthstoneRoot,
                Message = "目标目录不像炉石客户端根目录，请确认目录中存在 Hearthstone.exe 或炉石数据目录。"
            };
        }

        hearthstoneRoot = Path.GetFullPath(hearthstoneRoot);
        progress.Report($"炉石目录：{hearthstoneRoot}");

        string bepInExRoot = Path.Combine(hearthstoneRoot, "BepInEx");
        if (!Directory.Exists(bepInExRoot))
        {
            string? bundledBepInEx = FindBundledBepInExZip();
            if (bundledBepInEx != null)
            {
                progress.Report("未检测到 BepInEx，使用包内 BepInEx 5 x64。");
                ExtractBepInExZip(bundledBepInEx, hearthstoneRoot, progress, cancellationToken);
            }
            else
            {
                progress.Report("未检测到 BepInEx，开始下载 BepInEx 5 x64。");
                await DownloadAndExtractBepInExAsync(hearthstoneRoot, progress, cancellationToken);
            }
        }
        else
        {
            progress.Report("检测到已有 BepInEx，进入追加安装模式，不覆盖已有目录。");
        }

        Directory.CreateDirectory(Path.Combine(bepInExRoot, "plugins"));
        Directory.CreateDirectory(Path.Combine(bepInExRoot, "unstripped_corlib"));

        PatchDoorstopConfig(hearthstoneRoot, progress);
        CopyUnstrippedCorlib(bepInExRoot, progress);
        CopyPluginDll(bepInExRoot, progress);
        EnsurePluginConfigFiles(bepInExRoot, progress);

        bool gameRunning = Process.GetProcessesByName("Hearthstone").Length > 0;
        string suffix = gameRunning ? " 当前炉石正在运行，请重启炉石后加载或更新插件。" : " 下次启动炉石时会加载插件。";

        return new InstallResult
        {
            Success = true,
            HearthstonePath = hearthstoneRoot,
            Message = "HsMod 安装完成。" + suffix
        };
    }

    public async Task<InstallResult> UninstallAsync(string hearthstoneRoot, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (!HearthstoneLocator.LooksLikeHearthstoneRoot(hearthstoneRoot))
        {
            return new InstallResult
            {
                Success = false,
                HearthstonePath = hearthstoneRoot,
                Message = "目标目录不像炉石客户端根目录，无法安全卸载。"
            };
        }

        hearthstoneRoot = Path.GetFullPath(hearthstoneRoot);
        progress.Report($"炉石目录：{hearthstoneRoot}");
        await CloseHearthstoneAsync(progress, cancellationToken);

        try
        {
            await Task.Run(() =>
            {
                string bepInExRoot = Path.Combine(hearthstoneRoot, "BepInEx");
                string pluginsDir = Path.Combine(bepInExRoot, "plugins");
                string configDir = Path.Combine(bepInExRoot, "config");
                string workDir = Path.Combine(bepInExRoot, "HsMod");

                DeleteMatchingFiles(pluginsDir, "HsMod.dll*", hearthstoneRoot, progress, cancellationToken);
                DeleteMatchingFiles(configDir, "HsMod.cfg*", hearthstoneRoot, progress, cancellationToken);

                if (Directory.Exists(configDir))
                {
                    foreach (string skinConfig in Directory.EnumerateFiles(configDir, "HsSkins.cfg", SearchOption.AllDirectories).ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DeleteFile(skinConfig, hearthstoneRoot, progress);
                    }
                }

                DeleteDirectory(workDir, hearthstoneRoot, progress);

                string cachePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HsModManager",
                    "skin-catalog.json");
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                    progress.Report("已删除管理器皮肤缓存。");
                }
            }, cancellationToken);

            progress.Report("已保留共享的 BepInEx、unstripped_corlib 和其他插件。");
            return new InstallResult
            {
                Success = true,
                HearthstonePath = hearthstoneRoot,
                Message = "HsMod 已卸载。共享的 BepInEx 和其他插件未被删除。"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InstallResult
            {
                Success = false,
                HearthstonePath = hearthstoneRoot,
                Message = "卸载 HsMod 失败：" + ex.Message
            };
        }
    }

    private static async Task CloseHearthstoneAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        Process[] processes = Process.GetProcessesByName("Hearthstone");
        if (processes.Length == 0)
        {
            progress.Report("炉石客户端未运行。");
            return;
        }

        progress.Report("正在关闭炉石客户端...");
        foreach (Process process in processes)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
            }
        }

        await Task.Delay(1500, cancellationToken);
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                process.Dispose();
            }
        }

        progress.Report("炉石客户端已关闭。");
    }

    private static void DeleteMatchingFiles(
        string directory,
        string pattern,
        string hearthstoneRoot,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFile(file, hearthstoneRoot, progress);
        }
    }

    private static void DeleteFile(string path, string hearthstoneRoot, IProgress<string> progress)
    {
        string fullPath = EnsureInsideHearthstone(path, hearthstoneRoot);
        File.Delete(fullPath);
        progress.Report("已删除：" + Path.GetRelativePath(hearthstoneRoot, fullPath));
    }

    private static void DeleteDirectory(string path, string hearthstoneRoot, IProgress<string> progress)
    {
        string fullPath = EnsureInsideHearthstone(path, hearthstoneRoot);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        Directory.Delete(fullPath, recursive: true);
        progress.Report("已删除：" + Path.GetRelativePath(hearthstoneRoot, fullPath));
    }

    private static string EnsureInsideHearthstone(string path, string hearthstoneRoot)
    {
        string root = Path.GetFullPath(hearthstoneRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝删除炉石目录之外的文件：" + fullPath);
        }

        return fullPath;
    }

    private async Task DownloadAndExtractBepInExAsync(string hearthstoneRoot, IProgress<string> progress, CancellationToken cancellationToken)
    {
        string assetUrl = await FindBepInExAssetUrlAsync(cancellationToken);
        progress.Report("下载：" + assetUrl);

        string zipPath = Path.Combine(Path.GetTempPath(), $"BepInEx_win_x64_{Guid.NewGuid():N}.zip");
        await using (Stream remote = await _http.GetStreamAsync(assetUrl, cancellationToken))
        await using (FileStream local = File.Create(zipPath))
        {
            await remote.CopyToAsync(local, cancellationToken);
        }

        progress.Report("解压 BepInEx，已存在文件会跳过。");
        ExtractBepInExZip(zipPath, hearthstoneRoot, progress, cancellationToken);

        try
        {
            File.Delete(zipPath);
        }
        catch { }
    }

    private static void ExtractBepInExZip(string zipPath, string hearthstoneRoot, IProgress<string> progress, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string root = Path.GetFullPath(hearthstoneRoot);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("BepInEx 压缩包包含非法路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                progress.Report($"跳过已有文件：{Path.GetRelativePath(root, target)}");
                continue;
            }

            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private async Task<string> FindBepInExAssetUrlAsync(CancellationToken cancellationToken)
    {
        string json = await _http.GetStringAsync("https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=20", cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);

        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            string tag = release.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? "" : "";
            if (!tag.StartsWith("v5.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out JsonElement assets))
            {
                continue;
            }

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
                if (!name.Contains("BepInEx_win_x64", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement) ? urlElement.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        throw new InvalidOperationException("未能在 GitHub Release 中找到 BepInEx 5 x64 下载包。请手动安装 BepInEx_x64 后重试。");
    }

    private static void PatchDoorstopConfig(string hearthstoneRoot, IProgress<string> progress)
    {
        string doorstop = Path.Combine(hearthstoneRoot, "doorstop_config.ini");
        if (!File.Exists(doorstop))
        {
            progress.Report("未找到 doorstop_config.ini；如果 BepInEx 已完整安装，可忽略。");
            return;
        }

        string[] lines = File.ReadAllLines(doorstop);
        bool changed = false;
        bool found = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("dll_search_path_override", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                if (!trimmed.Contains("BepInEx\\unstripped_corlib", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "dll_search_path_override = BepInEx\\unstripped_corlib";
                    changed = true;
                }
            }
            else if (trimmed.StartsWith("dllSearchPathOverride", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                if (!trimmed.Contains("BepInEx\\unstripped_corlib", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "dllSearchPathOverride=BepInEx\\unstripped_corlib";
                    changed = true;
                }
            }
        }

        if (!found)
        {
            var list = lines.ToList();
            list.Add("dll_search_path_override = BepInEx\\unstripped_corlib");
            lines = list.ToArray();
            changed = true;
        }

        if (!changed)
        {
            progress.Report("doorstop_config.ini 已配置 unstripped_corlib。");
            return;
        }

        string backup = doorstop + ".HsModManager.bak";
        if (!File.Exists(backup))
        {
            File.Copy(doorstop, backup, overwrite: false);
        }

        File.WriteAllLines(doorstop, lines);
        progress.Report("已更新 doorstop_config.ini，并保留备份。");
    }

    private static void CopyUnstrippedCorlib(string bepInExRoot, IProgress<string> progress)
    {
        string? source = FindDirectoryNearApp("HsMod", "UnstrippedCorlib");
        if (source == null)
        {
            source = Path.Combine(AppContext.BaseDirectory, "Payload", "UnstrippedCorlib");
        }

        if (!Directory.Exists(source))
        {
            progress.Report("未找到 UnstrippedCorlib，跳过运行库复制。");
            return;
        }

        string target = Path.Combine(bepInExRoot, "unstripped_corlib");
        Directory.CreateDirectory(target);

        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*.dll", SearchOption.TopDirectoryOnly))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
            {
                continue;
            }

            File.Copy(file, destination, overwrite: false);
            copied++;
        }

        progress.Report($"UnstrippedCorlib 追加完成，新复制 {copied} 个 DLL。");
    }

    private static void CopyPluginDll(string bepInExRoot, IProgress<string> progress)
    {
        string? pluginDll = FindPluginDll();
        if (pluginDll == null)
        {
            throw new FileNotFoundException("未找到 HsMod.dll。请先构建 HsMod Release，或把 HsMod.dll 放到管理器 Payload 目录。");
        }

        string pluginsDir = Path.Combine(bepInExRoot, "plugins");
        Directory.CreateDirectory(pluginsDir);
        string destination = Path.Combine(pluginsDir, "HsMod.dll");

        if (File.Exists(destination))
        {
            string backup = Path.Combine(pluginsDir, $"HsMod.dll.{DateTime.Now:yyyyMMddHHmmss}.bak");
            File.Copy(destination, backup, overwrite: false);
            progress.Report("已备份旧 HsMod.dll：" + Path.GetFileName(backup));
        }

        File.Copy(pluginDll, destination, overwrite: true);
        progress.Report("已复制 HsMod.dll 到 BepInEx\\plugins。");
    }

    private static void EnsurePluginConfigFiles(string bepInExRoot, IProgress<string> progress)
    {
        string configDir = Path.Combine(bepInExRoot, "config");
        string workDir = Path.Combine(bepInExRoot, "HsMod");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(workDir);

        string hsSkinsPath = Path.Combine(configDir, "HsSkins.cfg");
        if (!File.Exists(hsSkinsPath))
        {
            File.WriteAllText(hsSkinsPath,
                "# 皮肤映射表\r\n" +
                "# 格式：原始皮肤:替换皮肤，支持 原始皮肤:替换1,替换2,替换3 做随机皮肤。\r\n" +
                "# 按 F4 或在管理器中点击“热加载 HsSkins.cfg”后重新读取。\r\n" +
                "# 示例：玛法里奥·怒风替换成大导师玛法里奥\r\n" +
                "274:57761\r\n",
                System.Text.Encoding.UTF8);
            progress.Report("已创建默认 BepInEx\\config\\HsSkins.cfg。");
        }
        else
        {
            progress.Report("检测到已有 HsSkins.cfg，保持不覆盖。");
        }

        string hsModCfgPath = Path.Combine(configDir, "HsMod.cfg");
        if (!File.Exists(hsModCfgPath))
        {
            File.WriteAllText(hsModCfgPath,
                "## HsMod 基础配置。完整配置会在首次启动炉石并加载插件后自动生成。\r\n" +
                "[HsMod]\r\n" +
                "HsMod.Init.Language = zhCN\r\n" +
                "HsMod.Init.Eula = false\r\n",
                System.Text.Encoding.UTF8);
            progress.Report("已创建基础 BepInEx\\config\\HsMod.cfg，完整配置会在插件首次启动后生成。");
        }
        else
        {
            progress.Report("检测到已有 HsMod.cfg，保持不覆盖。");
        }

        progress.Report("已确保 BepInEx\\HsMod 工作目录存在。");
    }

    private static string? FindPluginDll()
    {
        foreach (string candidate in EnumeratePotentialPluginDlls())
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? FindBundledBepInExZip()
    {
        foreach (string ancestor in EnumerateAncestors(AppContext.BaseDirectory))
        {
            string payloadDir = Path.Combine(ancestor, "Payload");
            if (!Directory.Exists(payloadDir))
            {
                continue;
            }

            string? zip = Directory
                .EnumerateFiles(payloadDir, "BepInEx_win_x64_*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (zip != null)
            {
                return zip;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePotentialPluginDlls()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Payload", "HsMod.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "HsMod.dll");

        foreach (string ancestor in EnumerateAncestors(AppContext.BaseDirectory))
        {
            yield return Path.Combine(ancestor, "HsMod", "Release", "HsMod.dll");
            yield return Path.Combine(ancestor, "HsMod", "bin", "Release", "HsMod.dll");
            yield return Path.Combine(ancestor, "HsMod", "bin", "Debug", "HsMod.dll");
        }
    }

    private static string? FindDirectoryNearApp(params string[] parts)
    {
        foreach (string ancestor in EnumerateAncestors(AppContext.BaseDirectory))
        {
            string path = Path.Combine(new[] { ancestor }.Concat(parts).ToArray());
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(start));
        while (directory != null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
