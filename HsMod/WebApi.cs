using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static HsMod.PluginConfig;

namespace HsMod
{
    public class WebApi
    {

        public static async Task<string> RunShellCommandAsync(string command)
        {
            if (!isWebshellEnable.Value)
            {
                return string.Empty;
            }

            var processInfo = new ProcessStartInfo
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Platform-specific settings for command execution
            if ((Environment.OSVersion.Platform == PlatformID.MacOSX) || (Environment.OSVersion.Platform == PlatformID.Unix))
            {
                processInfo.FileName = "/bin/sh";
                processInfo.Arguments = $"-c \"{command}\"";
            }
            else
            {
                processInfo.FileName = "cmd.exe";
                processInfo.Arguments = "/C chcp 65001 & " + command;
            }

            using (var process = new Process { StartInfo = processInfo })
            {
                var outputBuilder = new StringBuilder();
                var tcs = new TaskCompletionSource<bool>();

                // Set up asynchronous reading of output and error streams
                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                        tcs.TrySetResult(true); // Mark as complete when output ends
                    else
                        outputBuilder.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                        tcs.TrySetResult(true); // Mark as complete when error output ends
                    else
                        outputBuilder.AppendLine(e.Data);
                };

                // Start the process and begin reading output and error
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Create a task to wait for the process to exit
                var processTask = Task.Run(() =>
                {
                    process.WaitForExit();
                    tcs.TrySetResult(true);
                });

                // Wait for the process to complete or timeout after 5 seconds
                var completedTask = await Task.WhenAny(processTask, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completedTask == processTask)
                {
                    // Process completed within timeout
                    return outputBuilder.ToString();
                }
                else
                {
                    // Timeout occurred
                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill(); // Ensure the process is terminated on timeout
                        }
                        catch
                        {
                            // Ignore any exceptions if the process is already terminated
                        }
                    }
                    return string.Empty; // Return empty string on timeout
                }
            }
        }

        public static int UpdateHsSkinsCfg(string content, out string res)
        {
            res = string.Empty;

            try
            {
                File.WriteAllText(Path.Combine(BepInEx.Paths.ConfigPath, "HsSkins.cfg"), content);
                LoadSkinsConfigFromFile();
                res = WebPage.HsModCfgPage("HsSkins.cfg").ToString();
                return 200;
            }
            catch (Exception ex)
            {
                res = ex.Message;
                return 500;
            }
        }

        public static int RunPluginConfigAsync(string key, string value, out string res)
        {
            res = string.Empty;

            if (!string.IsNullOrEmpty(key) && (key.Length > 5))
            {
                key = key.Substring(0, key.Length - 5); // remove .name
                if (key.Equals("isWebshellEnable"))
                {
                    res = "not allow.";
                    return 403;
                }
                var configKeyProp = typeof(PluginConfig).GetField(key, BindingFlags.Public | BindingFlags.Static);
                if (configKeyProp == null)
                {
                    res = "key not found.";
                    return 501;

                }
                var configEntry = (ConfigEntryBase)configKeyProp.GetValue(null);
                var converter = TomlTypeConverter.GetConverter(configEntry.SettingType);
                if (converter != null)
                {
                    configEntry.SetSerializedValue(value);
                    res = configEntry.GetSerializedValue();
                    return 200;
                }
            }
            return 500;
        }

        public static string GetAllConfigMetadata(string lang = null)
        {
            // Use specified language or fall back to plugin default
            string targetLang = string.IsNullOrEmpty(lang) ? pluginInitLanague.Value : lang;

            // Load language file for the target language
            Dictionary<string, string> langDict = null;
            Dictionary<string, string> fallbackDict = null;

            try
            {
                string langJson = FileManager.ReadEmbeddedFile($"./Languages/{targetLang}.json");
                if (!string.IsNullOrEmpty(langJson))
                {
                    langDict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(langJson);
                }
            }
            catch { }

            // Always load enUS as fallback
            try
            {
                string enUSJson = FileManager.ReadEmbeddedFile("./Languages/enUS.json");
                if (!string.IsNullOrEmpty(enUSJson))
                {
                    fallbackDict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(enUSJson);
                }
            }
            catch { }

            // Helper function to get localized value
            Func<string, string, string> getLangValue = (key, defaultValue) =>
            {
                if (langDict != null && langDict.TryGetValue(key, out var val))
                    return val;
                if (fallbackDict != null && fallbackDict.TryGetValue(key, out var fallbackVal))
                    return fallbackVal;
                return defaultValue;
            };

            var configList = new List<Dictionary<string, object>>();
            var fields = typeof(PluginConfig).GetFields(BindingFlags.Public | BindingFlags.Static);

            foreach (var field in fields)
            {
                if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(ConfigEntry<>))
                    continue;

                var configEntry = field.GetValue(null) as ConfigEntryBase;
                if (configEntry == null)
                    continue;

                // Mark internal/advanced configs
                string fieldName = field.Name;
                bool isAdvanced = (fieldName == "pluginInitLanague" || fieldName == "isEulaRead" || fieldName == "isDynamicFpsEnable");

                // Get localized name, label, description
                string localizedName = getLangValue($"{fieldName}.name", configEntry.Definition.Key);
                string localizedLabel = getLangValue($"{fieldName}.label", configEntry.Definition.Section);
                string localizedDesc = getLangValue($"{fieldName}.description", configEntry.Description?.Description ?? "");

                var configItem = new Dictionary<string, object>
                {
                    ["key"] = fieldName,
                    ["name"] = localizedName,
                    ["label"] = localizedLabel,
                    ["description"] = localizedDesc,
                    ["type"] = GetConfigType(configEntry.SettingType),
                    ["value"] = configEntry.GetSerializedValue(),
                    ["isAdvanced"] = isAdvanced
                };

                // Handle enum types
                if (configEntry.SettingType.IsEnum)
                {
                    configItem["enumValues"] = Enum.GetNames(configEntry.SettingType);
                }

                // Handle AcceptableValueRange
                if (configEntry.Description?.AcceptableValues != null)
                {
                    var acceptableValues = configEntry.Description.AcceptableValues;
                    var acceptableType = acceptableValues.GetType();

                    if (acceptableType.IsGenericType)
                    {
                        var minProp = acceptableType.GetProperty("MinValue");
                        var maxProp = acceptableType.GetProperty("MaxValue");

                        if (minProp != null && maxProp != null)
                        {
                            configItem["min"] = minProp.GetValue(acceptableValues);
                            configItem["max"] = maxProp.GetValue(acceptableValues);
                        }
                    }
                }

                // Handle KeyboardShortcut type
                if (configEntry.SettingType == typeof(KeyboardShortcut))
                {
                    configItem["keyCodes"] = Enum.GetNames(typeof(KeyCode));
                }

                configList.Add(configItem);
            }

            // Group by label
            var grouped = configList
                .GroupBy(c => c["label"].ToString())
                .ToDictionary(g => g.Key, g => g.ToList());

            var result = new Dictionary<string, object>
            {
                ["language"] = pluginInitLanague.Value,
                ["groups"] = grouped
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }

        public static string GetStatusJson()
        {
            var patches = new List<Dictionary<string, object>>();
            for (int i = 0; i < PatchManager.AllHarmonyName.Count; i++)
            {
                int methodCount = 0;
                try
                {
                    methodCount = PatchManager.AllHarmony[i].GetPatchedMethods().Count();
                }
                catch { }

                patches.Add(new Dictionary<string, object>
                {
                    ["name"] = PatchManager.AllHarmonyName[i],
                    ["methodCount"] = methodCount
                });
            }

            var result = new Dictionary<string, object>
            {
                ["plugin"] = new Dictionary<string, object>
                {
                    ["guid"] = PluginInfo.PLUGIN_GUID,
                    ["name"] = PluginInfo.PLUGIN_NAME,
                    ["author"] = PluginInfo.PLUGIN_AUTHOR,
                    ["version"] = PluginInfo.PLUGIN_VERSION,
                    ["enabled"] = isPluginEnable?.Value ?? false,
                    ["language"] = pluginInitLanague?.Value ?? "UNKNOWN",
                    ["runningTime"] = ConfigValue.Get().RunningTime
                },
                ["game"] = new Dictionary<string, object>
                {
                    ["pid"] = Process.GetCurrentProcess()?.Id ?? -1,
                    ["login"] = Utils.CacheLoginStatus,
                    ["hsunitid"] = CommandConfig.GlobalHSUnitID,
                    ["mode"] = SafeGetString(() => SceneMgr.Get()?.GetMode().ToString())
                },
                ["pets"] = GetPetDiagnostics(),
                ["web"] = new Dictionary<string, object>
                {
                    ["port"] = CommandConfig.webServerPort,
                    ["root"] = HsModWebSite
                },
                ["paths"] = new Dictionary<string, object>
                {
                    ["gameRoot"] = BepInEx.Paths.GameRootPath,
                    ["bepInExRoot"] = BepInEx.Paths.BepInExRootPath,
                    ["config"] = BepInEx.Paths.ConfigPath,
                    ["hsMatchLog"] = CommandConfig.hsMatchLogPath
                },
                ["patches"] = patches
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }

        private static Dictionary<string, object> GetPetDiagnostics()
        {
            int friendlyTag = -1;
            int opposingTag = -1;
            int friendlyContextCorner = 0;
            int opposingContextCorner = 0;
            bool boardReady = false;
            bool boardCompatible = false;
            bool friendlySpellLoaded = false;
            bool opposingSpellLoaded = false;
            bool friendlySpellActive = false;
            bool opposingSpellActive = false;
            bool friendlyControllerFound = false;
            bool opposingControllerFound = false;
            int friendlyControllerVariant = 0;
            int opposingControllerVariant = 0;
            bool friendlyModelLoaded = false;
            bool opposingModelLoaded = false;
            bool friendlyModelVisible = false;
            bool opposingModelVisible = false;

            try
            {
                GameState gameState = GameState.Get();
                friendlyTag = gameState?.GetPlayerBySide(Player.Side.FRIENDLY)?.GetTag(GAME_TAG.PET_VARIANT_ID) ?? -1;
                opposingTag = gameState?.GetPlayerBySide(Player.Side.OPPOSING)?.GetTag(GAME_TAG.PET_VARIANT_ID) ?? -1;

                Board board = Board.Get();
                boardReady = board != null;
                boardCompatible = board?.IsCornerReplacementCompatible() ?? false;

                CornerSpellReplacementManager manager = gameState?.GetCornerReplacementManager();
                if (manager != null)
                {
                    friendlyContextCorner = (int)manager.GetCornerReplacementContext(Player.Side.FRIENDLY).cornerReplacementPetType;
                    opposingContextCorner = (int)manager.GetCornerReplacementContext(Player.Side.OPPOSING).cornerReplacementPetType;
                    Spell friendlySpell = manager.GetCornerSpell(CornerReplacementPosition.BOTTOM_LEFT);
                    Spell opposingSpell = manager.GetCornerSpell(CornerReplacementPosition.TOP_RIGHT);
                    friendlySpellLoaded = friendlySpell != null;
                    opposingSpellLoaded = opposingSpell != null;
                    friendlySpellActive = friendlySpell?.IsActive() ?? false;
                    opposingSpellActive = opposingSpell?.IsActive() ?? false;

                    PetControllerGame friendlyController = friendlySpell?.GetComponentInChildren<PetControllerGame>(true);
                    PetControllerGame opposingController = opposingSpell?.GetComponentInChildren<PetControllerGame>(true);
                    friendlyControllerFound = friendlyController != null;
                    opposingControllerFound = opposingController != null;
                    friendlyControllerVariant = friendlyController?.PetVariantId ?? 0;
                    opposingControllerVariant = opposingController?.PetVariantId ?? 0;
                    friendlyModelLoaded = friendlyController?.PetObject != null;
                    opposingModelLoaded = opposingController?.PetObject != null;
                    friendlyModelVisible = friendlyController?.PetObject?.activeInHierarchy ?? false;
                    opposingModelVisible = opposingController?.PetObject?.activeInHierarchy ?? false;
                }
            }
            catch
            {
            }

            PetVariantDbfRecord friendlyPet = friendlyTag > 0 ? GameDbf.PetVariant.GetRecord(friendlyTag) : null;
            PetVariantDbfRecord opposingPet = opposingTag > 0 ? GameDbf.PetVariant.GetRecord(opposingTag) : null;

            return new Dictionary<string, object>
            {
                ["configuredFriendly"] = skinPet?.Value ?? -1,
                ["configuredOpposing"] = skinOpposingPet?.Value ?? -1,
                ["actualFriendly"] = friendlyTag,
                ["actualOpposing"] = opposingTag,
                ["friendlyCorner"] = friendlyPet?.CornerId ?? 0,
                ["opposingCorner"] = opposingPet?.CornerId ?? 0,
                ["friendlyName"] = SafeGetString(() => friendlyPet?.Name.GetString()),
                ["opposingName"] = SafeGetString(() => opposingPet?.Name.GetString()),
                ["friendlyContextCorner"] = friendlyContextCorner,
                ["opposingContextCorner"] = opposingContextCorner,
                ["boardReady"] = boardReady,
                ["boardCompatible"] = boardCompatible,
                ["friendlySpellLoaded"] = friendlySpellLoaded,
                ["opposingSpellLoaded"] = opposingSpellLoaded,
                ["friendlySpellActive"] = friendlySpellActive,
                ["opposingSpellActive"] = opposingSpellActive,
                ["friendlyControllerFound"] = friendlyControllerFound,
                ["opposingControllerFound"] = opposingControllerFound,
                ["friendlyControllerVariant"] = friendlyControllerVariant,
                ["opposingControllerVariant"] = opposingControllerVariant,
                ["friendlyModelLoaded"] = friendlyModelLoaded,
                ["opposingModelLoaded"] = opposingModelLoaded,
                ["friendlyModelVisible"] = friendlyModelVisible,
                ["opposingModelVisible"] = opposingModelVisible
            };
        }

        public static string GetSkinCatalogJson()
        {
            var result = new Dictionary<string, object>
            {
                ["current"] = GetCurrentSkinSettings(),
                ["catalog"] = new Dictionary<string, object>
                {
                    ["coins"] = GetCoins(),
                    ["cardBacks"] = GetCardBacks(),
                    ["boards"] = GetBoards(),
                    ["battlegroundBoards"] = GetBattlegroundBoards(),
                    ["battlegroundFinishers"] = GetBattlegroundFinishers(),
                    ["heroes"] = GetHeroes("HERO"),
                    ["battlegroundHeroes"] = GetHeroes("BATTLEGROUNDS_HERO"),
                    ["bobs"] = GetHeroes("BATTLEGROUNDS_GUIDE"),
                    ["pets"] = GetPets()
                },
                ["hsskins"] = ReadHsSkinsCfg()
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }

        public static string GetSkinSettingsJson()
        {
            var result = new Dictionary<string, object>
            {
                ["current"] = GetCurrentSkinSettings(),
                ["hsskins"] = ReadHsSkinsCfg()
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(result);
        }

        private static Dictionary<string, object> GetCurrentSkinSettings()
        {
            return new Dictionary<string, object>
            {
                ["skinCoin"] = skinCoin?.Value ?? -1,
                ["skinCardBack"] = skinCardBack?.Value ?? -1,
                ["skinBoard"] = skinBoard?.Value ?? -1,
                ["skinBgsBoard"] = skinBgsBoard?.Value ?? -1,
                ["skinBgsFinisher"] = skinBgsFinisher?.Value ?? -1,
                ["skinBob"] = skinBob?.Value ?? -1,
                ["skinHero"] = skinHero?.Value ?? -1,
                ["skinOpposingHero"] = skinOpposingHero?.Value ?? -1,
                ["skinPet"] = skinPet?.Value ?? -1,
                ["skinOpposingPet"] = skinOpposingPet?.Value ?? -1
            };
        }

        public static int RunAction(string action, out string res)
        {
            res = string.Empty;
            if (string.IsNullOrEmpty(action))
            {
                res = "action is required.";
                return 400;
            }

            try
            {
                switch (action)
                {
                    case "reloadSkins":
                        LoadSkinsConfigFromFile();
                        res = "skins reloaded.";
                        return 200;
                    case "refreshPetCorners":
                        Patcher.PatchFavorite.RefreshPetCorners();
                        res = "pet corners refreshed.";
                        return 200;
                    case "restartWeb":
                        WebServer.Restart();
                        res = "web server restarted.";
                        return 200;
                    case "simulateDisconnect":
                        Network.Get()?.SimulateUncleanDisconnectFromGameServer();
                        res = "disconnect simulated.";
                        return 200;
                    case "readNewCards":
                        Utils.TryReadNewCards();
                        res = "new cards marked as read.";
                        return 200;
                    case "toggleFps":
                        isShowFPSEnable.Value = !isShowFPSEnable.Value;
                        res = isShowFPSEnable.Value.ToString();
                        return 200;
                    default:
                        res = "action not supported.";
                        return 404;
                }
            }
            catch (Exception ex)
            {
                res = ex.Message;
                return 500;
            }
        }

        private static string SafeGetString(Func<string> func, string fallback = "")
        {
            try
            {
                return func() ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static Dictionary<string, object> SkinItem(int id, string name, string category, string extra = "", string heroClass = "")
        {
            if (string.IsNullOrEmpty(name))
            {
                name = $"{category} {id}";
            }

            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["name"] = name,
                ["category"] = category,
                ["extra"] = extra,
                ["heroClass"] = heroClass
            };
        }

        private static List<Dictionary<string, object>> GetCoins()
        {
            var list = new List<Dictionary<string, object>> { SkinItem(-1, "不修改", "coin") };
            try
            {
                foreach (var record in GameDbf.CosmeticCoin.GetRecords().OrderBy(x => x.ID).ToList())
                {
                    if (record != null)
                    {
                        list.Add(SkinItem(record.CardId, SafeGetString(() => record.Name.GetString(), $"幸运币 {record.CardId}"), "coin"));
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"幸运币读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static List<Dictionary<string, object>> GetCardBacks()
        {
            var list = new List<Dictionary<string, object>> { SkinItem(-1, "不修改", "cardBack") };
            try
            {
                foreach (var record in GameDbf.CardBack.GetRecords().OrderBy(x => x.ID).ToList())
                {
                    if (record != null)
                    {
                        list.Add(SkinItem(record.ID, SafeGetString(() => record.Name.GetString(), $"卡背 {record.ID}"), "cardBack"));
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"卡背读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static List<Dictionary<string, object>> GetBoards()
        {
            var list = new List<Dictionary<string, object>> { SkinItem(-1, "不修改", "board") };
            try
            {
                foreach (var record in GameDbf.Board.GetRecords().OrderBy(x => x.ID).ToList())
                {
                    if (record != null)
                    {
                        string name = SafeGetString(() => record.NoteDesc.ToString(), $"对战面板 {record.ID}");
                        list.Add(SkinItem(record.ID, name, "board"));
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"对战面板读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static List<Dictionary<string, object>> GetBattlegroundBoards()
        {
            var list = new List<Dictionary<string, object>> { SkinItem(-1, "不修改", "battlegroundBoard") };
            try
            {
                foreach (var record in GameDbf.BattlegroundsBoardSkin.GetRecords().OrderBy(x => x.ID).ToList())
                {
                    if (record != null)
                    {
                        list.Add(SkinItem(record.ID, SafeGetString(() => record.CollectionName.GetString(), $"酒馆战斗面板 {record.ID}"), "battlegroundBoard"));
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"酒馆战斗面板读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static List<Dictionary<string, object>> GetBattlegroundFinishers()
        {
            var list = new List<Dictionary<string, object>> { SkinItem(-1, "不修改", "battlegroundFinisher") };
            try
            {
                foreach (var record in GameDbf.BattlegroundsFinisher.GetRecords().OrderBy(x => x.ID).ToList())
                {
                    if (record != null)
                    {
                        list.Add(SkinItem(record.ID, SafeGetString(() => record.CollectionName.GetString(), $"酒馆击杀特效 {record.ID}"), "battlegroundFinisher"));
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"酒馆击杀特效读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static List<Dictionary<string, object>> GetHeroes(string heroType)
        {
            var list = new List<Dictionary<string, object>> { SkinItem(-1, "不修改", heroType) };
            try
            {
                foreach (var record in GameDbf.CardHero.GetRecords().OrderBy(x => x.HeroType).ThenBy(x => x.CardId).ToList())
                {
                    if (record == null)
                    {
                        continue;
                    }

                    string currentType = record.HeroType.ToString();
                    if (heroType == "HERO")
                    {
                        if (currentType == "BATTLEGROUNDS_HERO" || currentType == "BATTLEGROUNDS_GUIDE")
                        {
                            continue;
                        }
                    }
                    else if (currentType != heroType)
                    {
                        continue;
                    }

                    string name = SafeGetString(() => GameDbf.Card.GetRecord(record.CardId).Name.GetString(), $"英雄 {record.CardId}");
                    string heroClass = SafeGetString(() => DefLoader.Get().GetEntityDef(record.CardId).GetClass().ToString());
                    list.Add(SkinItem(record.CardId, name, heroType, currentType, heroClass));
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"英雄读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static List<Dictionary<string, object>> GetPets()
        {
            var list = new List<Dictionary<string, object>>
            {
                SkinItem(-1, "不修改", "pet"),
                SkinItem(0, "隐藏", "pet")
            };
            try
            {
                foreach (var record in GameDbf.PetVariant.GetRecords().OrderBy(x => x.ID).ToList())
                {
                    if (record != null)
                    {
                        list.Add(SkinItem(record.ID, SafeGetString(() => record.Name.GetString(), $"宠物 {record.ID}"), "pet", record.PetId.ToString()));
                    }
                }
            }
            catch (Exception ex)
            {
                list.Add(SkinItem(-999, $"宠物读取失败：{ex.Message}", "error"));
            }
            return list;
        }

        private static string ReadHsSkinsCfg()
        {
            string cfgPath = Path.Combine(BepInEx.Paths.ConfigPath, CommandConfig.GlobalHSUnitID, "HsSkins.cfg");
            if (!File.Exists(cfgPath))
            {
                cfgPath = Path.Combine(BepInEx.Paths.ConfigPath, "HsSkins.cfg");
            }

            if (!File.Exists(cfgPath))
            {
                return string.Empty;
            }

            try
            {
                using (FileStream fs = new FileStream(cfgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                return $"# 读取 HsSkins.cfg 失败：{ex.Message}";
            }
        }

        private static string GetConfigType(Type type)
        {
            if (type == typeof(bool)) return "bool";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(float)) return "float";
            if (type == typeof(string)) return "string";
            if (type == typeof(KeyboardShortcut)) return "keyboard";
            if (type.IsEnum) return "enum";
            return "string";
        }

    }
}
