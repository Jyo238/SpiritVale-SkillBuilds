using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SpiritValeSkillBuilds
{
    /// <summary>
    /// Build 記憶：把「技能點＋能力點＋快捷列＋裝備／神器／魔導書」存成 Build，一鍵還原。
    ///   還原順序：套用點法 → 配能力點 → 換裝 → 綁快捷列。
    ///   全程不呼叫任何「重置」——重置是 Waybinder NPC 專屬的入口，玩家自己去按。
    ///   ・技能視窗內多一排 Build 按鈕：左鍵＝還原（3 秒內再點一次確認）、Shift+左鍵＝儲存目前配置
    ///   ・還原流程：直接送出最終點法 → 配能力點 → 換裝 → 逐格綁快捷列，全程呼叫遊戲自己的
    ///     PlayerSave.ResetSkills / ApplySkills / AssignSkill（伺服器端完整驗證，不改數值不偽造封包）
    ///   ・Build 按角色隔離（CharacterData.UID），JSON 存 BepInEx\config
    ///   ・狀態機推進用「輪詢謂詞」：每步伺服器回應（CharacterCallback_T）會把本地
    ///     CharacterData 整包換新，直接對資料驗收，不依賴帶編譯雜湊的 RPC 方法名 —— 改版免疫
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.spiritvale.skillbuilds";
        public const string NAME = "SpiritVale Skill Builds (Build 切換)";
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<int> CfgCount;
        internal static ConfigEntry<bool> CfgHotkeys;
        internal static ConfigEntry<bool> CfgAttributes;
        internal static ConfigEntry<bool> CfgGear;
        internal static ConfigEntry<bool> CfgClearUnlisted;
        internal static ConfigEntry<float> CfgPosX;
        internal static ConfigEntry<float> CfgPosY;
        internal static ConfigEntry<string> CfgStorePath;
        internal static ConfigEntry<bool> CfgDiagnostic;

        public override void Load()
        {
            Logger = base.Log;

            CfgCount = Config.Bind("1.操作", "Build 數量", 3,
                new ConfigDescription("Build 數量（1~6）；不夠用可以在遊戲裡按「＋」加。改了要重開遊戲。", new AcceptableValueRange<int>(1, 6)));
            CfgHotkeys = Config.Bind("1.操作", "啟用熱鍵", true,
                "true＝Ctrl+F1..F6 還原對應Build（再按一次確認）、Ctrl+Shift+F1..F6 儲存目前配置。" +
                "不開技能視窗也能用。");
            // 以下三項只是「新建 Build 時的預設勾選」。實際還原動什麼，
            // 一律看每組自己的勾選（遊戲內對該組按鈕按右鍵 → 編輯面板）。
            CfgAttributes = Config.Bind("2.新組預設勾選", "能力點", false,
                "新存的 Build，預設要不要勾能力點（Str/Vit/Agi/Dex/Int/Luk）。" +
                "**預設不勾**：能力點只能加不能減，一旦加下去就得跑 Waybinder NPC 才能重來，" +
                "所以預設不動它比較安全；真的要一起換的人自己去編輯面板打勾。" +
                "注意：能力點只能「加點」。遊戲裡減能力點的唯一途徑是 Waybinder NPC 的整體重置，" +
                "所以快照若需要調降某項能力點，本插件會跳過能力點並提示你先去 NPC 重置。");
            CfgGear = Config.Bind("2.新組預設勾選", "裝備神器魔導書", true,
                "新存的 Build，預設要不要勾裝備／神器／魔導書。" +
                "還原順序是 套用點法→配能力點→穿裝備→綁快捷列：裝備先回到身上，" +
                "裝備賦予的技能才綁得上快捷列。");
            CfgClearUnlisted = Config.Bind("2.新組預設勾選", "卸下快照沒有的裝備", true,
                "新存的 Build，預設要不要勾「卸下快照沒有的裝備欄／神器欄」＝忠實還原當時的樣子；" +
                "不勾＝只穿上快照有的，其餘保持現狀。");
            CfgPosX = Config.Bind("1.操作", "按鈕列水平微調", 0f,
                "按鈕列預設貼在技能視窗右上角。往左移填負數、往右移填正數（單位約等於 1440p 下的像素）。");
            CfgPosY = Config.Bind("1.操作", "按鈕列垂直微調", 0f,
                "往下移填負數、往上移填正數。");
            CfgStorePath = Config.Bind("1.操作", "存檔資料夾", "",
                "留空＝存在遊戲的 BepInEx\\config 底下（換電腦不會跟著走）。" +
                "填一個資料夾路徑（例如 OneDrive／Google 雲端硬碟底下的資料夾），" +
                "Build 就會存在那裡，多台電腦自動共用。" +
                "注意：不要兩台同時開遊戲改 Build，雲端同步會打架。");
            CfgDiagnostic = Config.Bind("3.診斷", "診斷模式", false,
                "把快照內容、還原狀態機的每一步判定寫進 log。回報問題時再開。");

            Store.Init();

            // 啟動簽名驗證（MarketPrice 紅線 4）：名稱查找＋參數型別名比對，不硬引用。
            // 遊戲改版把方法改掉時優雅降級（按鈕顯示待更新），絕不呼叫。
            Core.SendAvailable = ValidateSignatures();
            if (!Core.SendAvailable)
                Logger.LogWarning("[Build] PlayerSave.ResetSkills/ApplySkills/AssignSkill 簽名不符——" +
                    "遊戲版本可能已更新，還原功能停用（仍可瀏覽/儲存快照），請等待插件更新。");

            var harmony = new Harmony(GUID);

            // 每幀入口：熱鍵、按鈕點擊偵測、還原狀態機（禁 AddComponent 注入，紅線 1）
            TryPatch(harmony, "每幀泵(UIManager.LateUpdate)",
                () => AccessTools.Method(typeof(UIManager), "LateUpdate"),
                postfix: nameof(Patches.UIManagerLateUpdate_Postfix));

            // 技能視窗重繪：快取實例＋確保 Build 按鈕列存在
            TryPatch(harmony, "技能視窗掛按鈕(UISkills.Draw)",
                () => AccessTools.Method(typeof(UISkills), nameof(UISkills.Draw),
                    new[] { typeof(CharacterData) }),
                postfix: nameof(Patches.UISkillsDraw_Postfix));

            Logger.LogInfo($"{NAME} v{VERSION} 已載入。Build {CfgCount.Value} 組，" +
                $"已存角色數：{Store.CharacterCount}");
        }

        /// <summary>逐一掛載 patch，任一失敗只記警告不中斷 —— 遊戲改版時降級而非崩潰。</summary>
        private static void TryPatch(Harmony harmony, string label,
            Func<System.Reflection.MethodBase> resolver, string prefix = null, string postfix = null)
        {
            try
            {
                var target = resolver();
                if (target == null)
                {
                    Logger.LogWarning($"[{label}] 找不到目標方法，略過（遊戲版本可能已更新）。");
                    return;
                }

                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));

                Logger.LogInfo($"[{label}] 掛載成功。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[{label}] 掛載失敗，略過：{ex.Message}");
            }
        }

        private static bool ValidateSignatures()
        {
            try
            {
                var reset = AccessTools.Method(typeof(PlayerSave), "ResetSkills");
                var apply = AccessTools.Method(typeof(PlayerSave), "ApplySkills");
                var assign = AccessTools.Method(typeof(PlayerSave), "AssignSkill");
                if (reset == null || apply == null || assign == null) return false;

                var rp = reset.GetParameters();
                var ap = apply.GetParameters();
                var sp = assign.GetParameters();

                return
                    rp.Length == 1 && rp[0].ParameterType.Name == "Action`1" &&
                        rp[0].ParameterType.GetGenericArguments()[0].Name == "CharacterData" &&
                    ap.Length == 2 && ap[0].ParameterType.Name == "List`1" &&
                        ap[0].ParameterType.GetGenericArguments()[0].Name == "SkillData" &&
                        ap[1].ParameterType.Name == "Action`1" &&
                        ap[1].ParameterType.GetGenericArguments()[0].Name == "CharacterData" &&
                    sp.Length == 3 && sp[0].ParameterType == typeof(int) &&
                        sp[1].ParameterType == typeof(string) && sp[2].ParameterType == typeof(int);
            }
            catch { return false; }
        }
    }

    internal static class Patches
    {
        public static void UIManagerLateUpdate_Postfix()
        {
            try { Core.OnUpdate(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Build] 每幀處理失敗：{ex.Message}"); }
        }

        public static void UISkillsDraw_Postfix(UISkills __instance)
        {
            try { UiRow.Attach(__instance); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Build] 掛按鈕列失敗：{ex.Message}"); }
        }
    }

    // =========================================================================
    //  Build 資料模型與持久化（JSON，按角色 UID 隔離）
    // =========================================================================

    internal class PresetSkill
    {
        public string Id { get; set; }
        public int Lv { get; set; }
    }

    internal class PresetSlot
    {
        public int Slot { get; set; }
        public string Id { get; set; }
        public int Lv { get; set; }
    }

    /// <summary>裝備／神器／魔導書的一格快照。Uid＝物品實例 id（RefinableItemData.GetInstanceId）。</summary>
    internal class PresetGear
    {
        public string Slot { get; set; }    // EquipSlot / ArtifactSlot 名稱；魔導書不用
        public string Uid { get; set; }
        public string Id { get; set; }      // 設定檔 id，缺件回報時給人看的
        public int Refine { get; set; }
    }

    /// <summary>
    /// 這一組還原時「要動哪些東西」。
    /// 儲存永遠是全部記下來，這裡只控制還原——所以隨時改主意都不用重存快照。
    /// </summary>
    internal class PresetFlags
    {
        public bool Skills { get; set; } = true;
        public bool Hotbar { get; set; } = true;
        public bool Attributes { get; set; } = true;
        public bool Equips { get; set; } = true;
        public bool Artifacts { get; set; } = true;
        public bool Grimoires { get; set; } = true;
        public bool ClearUnlisted { get; set; } = true;
    }

    internal class Preset
    {
        public string Name { get; set; }
        public PresetFlags Use { get; set; } = new PresetFlags();
        public List<PresetSkill> Skills { get; set; } = new List<PresetSkill>();
        public List<PresetSlot> Assigned { get; set; } = new List<PresetSlot>();
        public List<PresetGear> Equips { get; set; } = new List<PresetGear>();
        public List<PresetGear> Artifacts { get; set; } = new List<PresetGear>();
        public List<PresetGear> Grimoires { get; set; } = new List<PresetGear>();
        /// <summary>能力點：StatType 名稱 → 值（Str/Vit/Agi/Dex/Int/Luk）。</summary>
        public Dictionary<string, int> Attributes { get; set; } = new Dictionary<string, int>();
        public string Loadout { get; set; }
        public string SavedAt { get; set; }
        public int CharLevel { get; set; }
    }

    internal static class Store
    {
        private static string _path;
        private static Dictionary<string, List<Preset>> _all =
            new Dictionary<string, List<Preset>>(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 讓中文 Build 名稱以明文存檔，方便玩家直接編輯 JSON 改名
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        internal static int CharacterCount => _all.Count;

        private const string FileName = "local.spiritvale.skillbuilds.presets.json";

        internal static void Init()
        {
            _path = ResolvePath();
            Plugin.Logger.LogInfo($"[Build] 存檔位置：{_path}");
            try
            {
                if (File.Exists(_path))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, List<Preset>>>(
                        File.ReadAllText(_path));
                    if (loaded != null) _all = loaded;
                    MigrateNames();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Build] 讀取 Build 清單失敗：{ex.Message}"); }
        }

        /// <summary>
        /// 存檔位置：預設在 BepInEx\config；設定了「存檔資料夾」就改存那裡
        /// （指到雲端同步資料夾＝多台電腦共用同一份 Build）。
        /// 資料夾建不出來就退回預設位置，絕不因為路徑打錯就整個存不了。
        /// </summary>
        private static string ResolvePath()
        {
            string fallback = Path.Combine(Paths.ConfigPath, FileName);
            try
            {
                string dir = (Plugin.CfgStorePath?.Value ?? "").Trim().Trim('"');
                if (dir.Length == 0) return fallback;

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string custom = Path.Combine(dir, FileName);

                // 首次切換：把本機既有的 Build 搬過去，不要讓玩家以為資料不見了
                if (!File.Exists(custom) && File.Exists(fallback))
                {
                    File.Copy(fallback, custom);
                    Plugin.Logger.LogInfo($"[Build] 已把既有 Build 複製到新位置：{custom}");
                }
                return custom;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 存檔資料夾「{Plugin.CfgStorePath?.Value}」無法使用" +
                    $"（{ex.Message}），改用預設位置。");
                return fallback;
            }
        }

        internal static Preset Get(string uid, int index)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            if (!_all.TryGetValue(uid, out var list) || list == null) return null;
            if (index < 0 || index >= list.Count) return null;
            var p = list[index];
            // 空殼（沒技能也沒快捷格）視同空槽
            if (p == null || ((p.Skills == null || p.Skills.Count == 0) &&
                              (p.Assigned == null || p.Assigned.Count == 0))) return null;
            return p;
        }

        /// <summary>只改名字：空組也能先取名（存一個只有名字的殼）。</summary>
        internal static void Rename(string uid, int index, string name)
        {
            if (string.IsNullOrEmpty(uid) || index < 0 || string.IsNullOrEmpty(name)) return;
            if (!_all.TryGetValue(uid, out var list) || list == null)
            {
                list = new List<Preset>();
                _all[uid] = list;
            }
            while (list.Count <= index) list.Add(null);
            if (list[index] == null) list[index] = new Preset();
            list[index].Name = name;
            Save();
        }

        /// <summary>舊版自動命名的「流派N」換成「Build N」；玩家自己取的名字不動。</summary>
        private static void MigrateNames()
        {
            bool changed = false;
            foreach (var kv in _all)
            {
                var list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(p.Name, @"^流派\s*(\d+)$");
                    if (!m.Success) continue;
                    p.Name = "Build " + m.Groups[1].Value;
                    changed = true;
                }
            }
            if (changed) Save();
        }

        /// <summary>這個角色實際存了幾個 Build 槽（含中間的空槽）。</summary>
        internal static int SlotCount(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return 0;
            if (!_all.TryGetValue(uid, out var list) || list == null) return 0;
            return list.Count;
        }

        /// <summary>刪除一個 Build：整個移除，後面的往前遞補（不是清空留洞）。</summary>
        internal static void Delete(string uid, int index)
        {
            if (string.IsNullOrEmpty(uid)) return;
            if (!_all.TryGetValue(uid, out var list) || list == null) return;
            if (index < 0 || index >= list.Count) return;
            list.RemoveAt(index);
            while (list.Count > 0 && list[list.Count - 1] == null) list.RemoveAt(list.Count - 1);
            if (list.Count == 0) _all.Remove(uid);
            Save();
        }

        /// <summary>取這組的勾選設定（空組也給一份可編輯的預設）。</summary>
        internal static PresetFlags GetFlags(string uid, int index)
        {
            if (string.IsNullOrEmpty(uid)) return new PresetFlags();
            if (!_all.TryGetValue(uid, out var list) || list == null) return new PresetFlags();
            if (index < 0 || index >= list.Count || list[index] == null) return new PresetFlags();
            return list[index].Use ?? new PresetFlags();
        }

        internal static void SetFlags(string uid, int index, PresetFlags flags)
        {
            if (string.IsNullOrEmpty(uid) || index < 0 || flags == null) return;
            if (!_all.TryGetValue(uid, out var list) || list == null)
            {
                list = new List<Preset>();
                _all[uid] = list;
            }
            while (list.Count <= index) list.Add(null);
            if (list[index] == null) list[index] = new Preset();
            list[index].Use = flags;
            Save();
        }

        /// <summary>取名字（含尚未存配置的空組）。</summary>
        internal static string GetName(string uid, int index)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            if (!_all.TryGetValue(uid, out var list) || list == null) return null;
            if (index < 0 || index >= list.Count) return null;
            return list[index]?.Name;
        }

        internal static void Put(string uid, int index, Preset preset)
        {
            if (string.IsNullOrEmpty(uid) || index < 0) return;
            if (!_all.TryGetValue(uid, out var list) || list == null)
            {
                list = new List<Preset>();
                _all[uid] = list;
            }
            while (list.Count <= index) list.Add(null);
            // 覆寫時保留玩家改過的名字與勾選設定（重存快照不該把設定洗掉）
            if (list[index] != null)
            {
                if (!string.IsNullOrEmpty(list[index].Name)) preset.Name = list[index].Name;
                if (list[index].Use != null) preset.Use = list[index].Use;
            }
            list[index] = preset;
            Save();
        }

        private static void Save()
        {
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_all, JsonOpts)); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Build] 寫入 Build 清單失敗：{ex.Message}"); }
        }
    }

    // =========================================================================
    //  核心：每幀泵（熱鍵＋點擊）、快照、二次確認
    // =========================================================================

    internal static class Core
    {
        internal static bool SendAvailable;

        private static int _confirmIdx = -1;
        private static float _confirmUntil;

        internal static void OnUpdate()
        {
            var save = SafeGetSave();
            Machine.Tick(save);
            UiRow.Tick(save);
            TickHotkeys(save);
            if (_confirmIdx >= 0 && Time.unscaledTime > _confirmUntil) _confirmIdx = -1;
        }

        /// <summary>取得本地玩家的 PlayerSave；登入畫面等時機為 null。</summary>
        internal static PlayerSave SafeGetSave()
        {
            try
            {
                var player = App.Player;
                if (player == null) return null;
                var save = player.Save;
                if (save == null || save.Data == null) return null;
                return save;
            }
            catch { return null; }
        }

        private static void TickHotkeys(PlayerSave save)
        {
            if (!Plugin.CfgHotkeys.Value || save == null) return;
            if (IsTypingInInputField()) return;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (!ctrl) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            int count = Plugin.CfgCount.Value;
            for (int i = 0; i < count; i++)
            {
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.F1 + i)))
                {
                    Activate(save, i, shift);
                    return;
                }
            }
        }

        /// <summary>按鈕點擊或熱鍵觸發的統一入口。shift=true 走儲存。</summary>
        internal static void Activate(PlayerSave save, int index, bool shift)
        {
            if (save == null) return;

            if (Machine.Busy)
            {
                UiRow.SetStatus("還原進行中，請稍候…");
                return;
            }

            if (shift)
            {
                SavePreset(save, index);
                return;
            }

            string uid = SafeUid(save);
            var preset = Store.Get(uid, index);
            if (preset == null)
            {
                UiRow.SetStatus($"第 {index + 1} 組是空的。Shift+點擊（或 Ctrl+Shift+F{index + 1}）＝儲存目前配置。");
                return;
            }

            if (!SendAvailable)
            {
                UiRow.SetStatus("遊戲已改版，還原功能暫停，請等待插件更新。", true);
                return;
            }

            if (_confirmIdx == index && Time.unscaledTime <= _confirmUntil)
            {
                _confirmIdx = -1;
                Machine.Begin(save, preset);
            }
            else
            {
                _confirmIdx = index;
                _confirmUntil = Time.unscaledTime + 3f;
                UiRow.SetStatus($"再按一次確認還原「{preset.Name}」");
            }
        }

        private static void SavePreset(PlayerSave save, int index)
        {
            try
            {
                var data = save.Data;
                var sys = data.Skills;
                if (sys == null)
                {
                    UiRow.SetStatus("讀不到技能資料，無法儲存。", true);
                    return;
                }

                var preset = new Preset
                {
                    Name = $"Build {index + 1}",
                    SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    CharLevel = SafeLevel(data),
                };

                var skills = sys.Skills;
                if (skills != null)
                {
                    for (int i = 0; i < skills.Count; i++)
                    {
                        var sd = skills[i];
                        if (sd == null || string.IsNullOrEmpty(sd.Id) || sd.Level <= 0) continue;
                        preset.Skills.Add(new PresetSkill { Id = sd.Id, Lv = sd.Level });
                    }
                }

                var assigned = sys.Assigned;
                if (assigned != null)
                {
                    for (int i = 0; i < assigned.Count; i++)
                    {
                        var sd = assigned[i];
                        if (sd == null || string.IsNullOrEmpty(sd.Id)) continue;
                        preset.Assigned.Add(new PresetSlot { Slot = i, Id = sd.Id, Lv = sd.Level });
                    }
                }

                // 快照永遠全記——勾選只管「還原時要不要動」，
                // 這樣之後改主意不用重存。新組的預設勾選才吃全域設定。
                SnapshotGear(data, preset);
                preset.Attributes = Attr.Read(data);
                if (Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo("[Build][診斷] 能力點快照：" +
                        string.Join("、", preset.Attributes.Select(kv => $"{kv.Key}={kv.Value}")));

                preset.Use = new PresetFlags
                {
                    Skills = true,
                    Hotbar = true,
                    Attributes = Plugin.CfgAttributes.Value,
                    Equips = Plugin.CfgGear.Value,
                    Artifacts = Plugin.CfgGear.Value,
                    Grimoires = Plugin.CfgGear.Value,
                    ClearUnlisted = Plugin.CfgClearUnlisted.Value,
                };

                if (preset.Skills.Count == 0 && preset.Assigned.Count == 0 &&
                    preset.Equips.Count == 0 && preset.Artifacts.Count == 0)
                {
                    UiRow.SetStatus("目前沒有已點的技能，也沒穿裝備，沒東西可存。");
                    return;
                }

                if (Plugin.CfgDiagnostic.Value)
                {
                    Plugin.Logger.LogInfo($"[Build][診斷] 快照第 {index + 1} 組：技能 " +
                        string.Join(", ", preset.Skills.Select(s => $"{s.Id}:{s.Lv}")) + "；快捷 " +
                        string.Join(", ", preset.Assigned.Select(s => $"#{s.Slot}={s.Id}:{s.Lv}")));
                }

                Store.Put(SafeUid(save), index, preset);
                UiRow.Refresh();
                var saved = Store.Get(SafeUid(save), index);
                string gear = $"／{preset.Equips.Count} 裝備／{preset.Artifacts.Count} 神器" +
                    $"／{preset.Grimoires.Count} 魔導書";
                if (preset.Attributes.Count > 0) gear += "／能力點";
                UiRow.SetStatus($"已存入「{saved?.Name ?? preset.Name}」：{preset.Skills.Count} 技能／{preset.Assigned.Count} 快捷格{gear}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 儲存快照失敗：{ex.Message}");
                UiRow.SetStatus("儲存失敗，詳見 log。", true);
            }
        }

        /// <summary>把身上的裝備／神器／魔導書記進快照（全部以實例 id 為鍵）。</summary>
        private static void SnapshotGear(CharacterData data, Preset preset)
        {
            try
            {
                var equips = data.Equips;
                if (equips != null)
                {
                    for (int i = 0; i < equips.Count; i++)
                    {
                        var es = equips[i];
                        if (es == null || es.Equip == null) continue;
                        string uid = Gear.InstanceId(es.Equip);
                        if (string.IsNullOrEmpty(uid)) continue;
                        preset.Equips.Add(new PresetGear
                        {
                            Slot = es.Slot.ToString(),
                            Uid = uid,
                            Id = es.Equip.Id,
                            Refine = SafeRefine(es.Equip),
                        });
                    }
                }

                var arts = data.Artifacts;
                if (arts != null)
                {
                    for (int i = 0; i < arts.Count; i++)
                    {
                        var a = arts[i];
                        if (a == null) continue;
                        string uid = Gear.InstanceId(a);
                        if (string.IsNullOrEmpty(uid)) continue;
                        preset.Artifacts.Add(new PresetGear
                        {
                            Slot = a.Slot.ToString(),
                            Uid = uid,
                            Id = a.Id,
                            Refine = SafeRefine(a),
                        });
                    }
                }

                var grims = data.Grimoires;
                if (grims != null)
                {
                    for (int i = 0; i < grims.Count; i++)
                    {
                        var g = grims[i];
                        if (g == null) continue;
                        string uid = Gear.InstanceId(g);
                        if (string.IsNullOrEmpty(uid)) continue;
                        preset.Grimoires.Add(new PresetGear { Uid = uid, Id = g.Id, Refine = SafeRefine(g) });
                    }
                }

                try { preset.Loadout = data.ActiveLoadout.ToString(); } catch { }

                if (Plugin.CfgDiagnostic.Value)
                {
                    Plugin.Logger.LogInfo("[Build][診斷] 裝備快照：" +
                        string.Join("、", preset.Equips.Select(e => $"{e.Slot}={e.Id}")) +
                        "；神器：" + string.Join("、", preset.Artifacts.Select(e => $"{e.Slot}={e.Id}")) +
                        "；魔導書：" + string.Join("、", preset.Grimoires.Select(e => e.Id)) +
                        $"；武器組={preset.Loadout}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 記錄裝備失敗（技能部分仍會存）：{ex.Message}");
            }
        }

        private static int SafeRefine(InventoryItemData item)
        {
            try { return item.TryCast<RefinableItemData>()?.Refine ?? 0; } catch { return 0; }
        }

        internal static string SafeUid(PlayerSave save)
        {
            try { return save?.Data?.UID; } catch { return null; }
        }

        private static int SafeLevel(CharacterData data)
        {
            try { return data.Level; } catch { return 0; }
        }

        /// <summary>搜尋欄等輸入框有焦點時不要搶按鍵。</summary>
        internal static bool IsTypingInInputField()
        {
            try
            {
                var es = EventSystem.current;
                var sel = es != null ? es.currentSelectedGameObject : null;
                return sel != null && sel.GetComponent<TMP_InputField>() != null;
            }
            catch { return false; }
        }
    }

    // =========================================================================
    //  裝備／神器／魔導書：查找與現況比對（全部用實例 id，不重造遊戲邏輯）
    // =========================================================================

    /// <summary>
    /// 能力點：`CharacterData.Attributes` 是以 StatType 為索引的 int 陣列。
    /// 重置（`ResetAttributes_S`）伺服器端只有 ResetAttributes + 存檔——免費、無 NPC 檢查、
    /// 也不像換裝那樣有戰鬥閘門。加點（`ApplyAttributes_S`）收的是**差額**，
    /// 由 `Formula.GetRemainingPoints` 驗證。
    /// </summary>
    internal static class Attr
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Dictionary<string, int> Read(CharacterData data)
        {
            var res = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var arr = data.Attributes;
                var types = StatUtil.AttributeTypes;
                if (arr == null || types == null) return res;
                for (int i = 0; i < types.Length; i++)
                {
                    var t = types[i];
                    int idx = (int)t;
                    if (idx < 0 || idx >= arr.Length) continue;
                    res[t.ToString()] = arr[idx];
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Build] 讀取能力點失敗：{ex.Message}"); }
            return res;
        }

        /// <summary>快照每一項都 ≥ 現況（＝只要加點就能達成，不需要退點）。</summary>
        internal static bool CanReachByAdding(Dictionary<string, int> want, Dictionary<string, int> cur)
        {
            foreach (var kv in want)
            {
                int c = cur.TryGetValue(kv.Key, out var v) ? v : 0;
                if (kv.Value < c) return false;
            }
            return true;
        }

        internal static bool Matches(Dictionary<string, int> want, Dictionary<string, int> cur)
        {
            foreach (var kv in want)
            {
                int c = cur.TryGetValue(kv.Key, out var v) ? v : 0;
                if (kv.Value != c) return false;
            }
            return true;
        }

        internal static string Describe(Dictionary<string, int> a) =>
            string.Join(" ", a.Select(kv => $"{kv.Key}{kv.Value}"));
    }

    internal enum GearKind { Equip, Unequip, Artifact, RemoveArtifact, Grimoire, RemoveGrimoire }

    internal class GearAction
    {
        public GearKind Kind;
        public string Slot;     // 欄位名稱（EquipSlot / ArtifactSlot）
        public string Uid;
        public string Id;
        public override string ToString() =>
            Kind switch
            {
                GearKind.Equip => $"穿{Slot}={Id}",
                GearKind.Unequip => $"卸{Slot}",
                GearKind.Artifact => $"神器{Slot}={Id}",
                GearKind.RemoveArtifact => $"卸神器{Slot}",
                GearKind.Grimoire => $"魔導書{Id}",
                GearKind.RemoveGrimoire => $"卸魔導書{Id}",
                _ => Kind.ToString(),
            };
    }

    internal static class Gear
    {
        internal static string InstanceId(InventoryItemData item)
        {
            try { return item != null ? item.GetInstanceId() : null; } catch { return null; }
        }

        /// <summary>目前該裝備欄上的物品實例 id（空欄＝null）。</summary>
        internal static string EquippedUid(CharacterData data, string slotName)
        {
            try
            {
                var list = data.Equips;
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                {
                    var es = list[i];
                    if (es == null || es.Equip == null) continue;
                    if (string.Equals(es.Slot.ToString(), slotName, StringComparison.Ordinal))
                        return InstanceId(es.Equip);
                }
            }
            catch { }
            return null;
        }

        internal static string ArtifactUid(CharacterData data, string slotName)
        {
            try
            {
                var list = data.Artifacts;
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (a == null) continue;
                    if (string.Equals(a.Slot.ToString(), slotName, StringComparison.Ordinal))
                        return InstanceId(a);
                }
            }
            catch { }
            return null;
        }

        internal static bool HasGrimoire(CharacterData data, string uid)
        {
            try
            {
                var list = data.Grimoires;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                    if (string.Equals(InstanceId(list[i]), uid, StringComparison.Ordinal)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>從背包找裝備實例；找不到再找身上（換欄位的情況）。</summary>
        internal static EquipData FindEquip(CharacterData data, string uid)
        {
            try
            {
                var inv = data.Inventory?.Equips;
                if (inv != null)
                    foreach (var kv in inv)
                        if (kv.Value != null && string.Equals(InstanceId(kv.Value), uid, StringComparison.Ordinal))
                            return kv.Value;

                var eq = data.Equips;
                if (eq != null)
                    for (int i = 0; i < eq.Count; i++)
                        if (eq[i] != null && eq[i].Equip != null &&
                            string.Equals(InstanceId(eq[i].Equip), uid, StringComparison.Ordinal))
                            return eq[i].Equip;

                var gr = data.Grimoires;
                if (gr != null)
                    for (int i = 0; i < gr.Count; i++)
                        if (string.Equals(InstanceId(gr[i]), uid, StringComparison.Ordinal)) return gr[i];
            }
            catch { }
            return null;
        }

        internal static ArtifactData FindArtifact(CharacterData data, string uid)
        {
            try
            {
                var inv = data.Inventory?.Artifacts;
                if (inv != null)
                    foreach (var kv in inv)
                        if (kv.Value != null && string.Equals(InstanceId(kv.Value), uid, StringComparison.Ordinal))
                            return kv.Value;

                var list = data.Artifacts;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (string.Equals(InstanceId(list[i]), uid, StringComparison.Ordinal)) return list[i];
            }
            catch { }
            return null;
        }

        /// <summary>戰鬥中伺服器會拒絕換裝（CheckCanChangeGear → CombatComponent.IsInCombat）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool InCombat()
        {
            try
            {
                var player = App.Player;
                var combat = player != null ? player.Combat : null;
                return combat != null && combat.IsInCombat;
            }
            catch { return false; }
        }
    }

    // =========================================================================
    //  還原狀態機：套用點法 → 配能力點 → 穿裝備 → 逐格綁快捷列（謂詞輪詢推進，每步有逾時）
    // =========================================================================

    internal static class Machine
    {
        private enum Step { Idle, WaitApply, AttrApply, Gear, Assign }

        /// <summary>本次還原被跳過的能力點步驟原因（null＝沒跳過）；完成時一併回報。</summary>
        private static string _skippedAttr;

        private static Step _step = Step.Idle;
        private static float _deadline;
        private static float _nextAssignAt;
        private static int _assignIdx;
        private static bool _assignSent;
        private static Preset _target;
        private static PresetFlags _use = new PresetFlags();
        private static string _uid;
        private static List<PresetSlot> _slots;     // 本輪要送的格子（重試時只剩失敗的）
        private static List<PresetSlot> _allSlots;  // 完整清單，總驗收與回報用
        private static int _retry;

        private static List<GearAction> _gear;      // 換裝佇列
        private static int _gearIdx;
        private static bool _gearSent;
        private static int _gearRound;
        private static readonly List<string> _gearMissing = new List<string>();

        private const float StepTimeout = 10f;      // 套用點法／能力點單步逾時
        private const float AssignTimeout = 1.0f;   // 單格綁定驗收逾時（過了就下一格）
        private const float AssignGap = 0.15f;      // 兩格之間最小間隔（實測 23 格快捷列，別拖太久）
        private const float RetryGap = 1.0f;        // 補綁前的沉澱時間（等賦予技能重新掛回來）
        private const int MaxRetry = 2;             // 補綁輪數上限
        private const float GearTimeout = 2.0f;     // 單件換裝驗收逾時（伺服器要寫檔，給寬一點）
        private const float GearGap = 0.2f;         // 兩件換裝之間的間隔
        private const int MaxGearRetry = 2;         // 補裝輪數上限

        internal static bool Busy => _step != Step.Idle;

        /// <summary>這次還原實際會動到哪些東西（依勾選）。</summary>
        private static string Describe()
        {
            var parts = new List<string>();
            if (_use.Skills && _target.Skills.Count > 0) parts.Add($"{_target.Skills.Count} 技能");
            if (_use.Attributes && (_target.Attributes?.Count ?? 0) > 0) parts.Add("能力點");
            if (_use.Equips && (_target.Equips?.Count ?? 0) > 0) parts.Add($"{_target.Equips.Count} 裝備");
            if (_use.Artifacts && (_target.Artifacts?.Count ?? 0) > 0) parts.Add($"{_target.Artifacts.Count} 神器");
            if (_use.Grimoires && (_target.Grimoires?.Count ?? 0) > 0) parts.Add($"{_target.Grimoires.Count} 魔導書");
            if (_use.Hotbar && _slots.Count > 0) parts.Add($"{_slots.Count} 快捷格");
            return parts.Count == 0 ? "（沒有勾選任何項目）" : string.Join("／", parts);
        }

        internal static void Begin(PlayerSave save, Preset preset)
        {
            _target = preset;
            _uid = Core.SafeUid(save);
            _use = preset.Use ?? new PresetFlags();

            // 快捷格整理：去空、依 slot 排序、超出容量的丟棄（沒勾就整段不做）
            _slots = (_use.Hotbar ? preset.Assigned ?? new List<PresetSlot>() : new List<PresetSlot>())
                .Where(s => s != null && !string.IsNullOrEmpty(s.Id))
                .OrderBy(s => s.Slot).ToList();
            int max = SafeMaxSlots(save);
            if (max > 0 && _slots.Any(s => s.Slot >= max))
            {
                int dropped = _slots.RemoveAll(s => s.Slot >= max);
                Plugin.Logger.LogWarning($"[Build] {dropped} 個快捷格超出容量 {max}，略過。");
            }
            _allSlots = new List<PresetSlot>(_slots);
            _retry = 0;
            _skippedAttr = null;
            _gear = null;
            _gearMissing.Clear();

            // 戰鬥中伺服器會拒絕換裝（CheckCanChangeGear），先擋下免得換到一半失敗
            if (HasGearData(preset) && Gear.InCombat())
            {
                Abort("戰鬥中無法換裝，脫離戰鬥後再還原。");
                return;
            }

            bool doSkills = _use.Skills && _target.Skills.Count > 0;

            if (doSkills)
            {
                // 預檢 1：技能是否還存在（改版可能移除）——伺服器會整包拒絕，先擋下白做工
                var missing = new List<string>();
                foreach (var s in _target.Skills)
                    if (!SkillExists(s.Id)) missing.Add(s.Id);
                if (missing.Count > 0)
                {
                    Abort($"這個 Build 內有 {missing.Count} 個技能已不存在（{string.Join(", ", missing.Take(3))}…），" +
                        "可能遊戲改版了，請重新配置後再存一次。");
                    return;
                }

                // 預檢 2：點數是否足夠（總點數上限對比快照總層級）
                int need = _target.Skills.Sum(s => s.Lv);
                int cap = SafeMaxPoints(save);
                if (cap > 0 && need > cap)
                {
                    Abort($"點數不足：快照需要 {need} 點，目前上限 {cap} 點（快照存檔時等級 {_target.CharLevel}）。");
                    return;
                }
            }

            Plugin.Logger.LogInfo($"[Build] 開始還原「{_target.Name}」：{Describe()}");

            if (!doSkills)
            {
                BeginAttributes(save);
                return;
            }

            // 直接送「最終點法清單」,不經過任何重置——
            // 這正是玩家在技能視窗右鍵退點、再按「套用」走的路（ApplySkills 收的是完整終態,
            // 可增可減）,所以隨處可用。`ResetSkills` 反而是 Waybinder NPC 專屬的入口,不碰。
            // 另一個更重要的理由是**原子性**:先洗光再重加的話,中間有一段「角色技能全空」的視窗,
            // 加點那步一旦失敗（斷線/點數不符）玩家就卡在全空狀態;直接送終態則是
            // 伺服器要嘛整包接受、要嘛整包拒絕,失敗時原狀不變。
            // 附帶好處:不會觸發 DoResetSkills 的 ClearLoadouts/ValidateEquips 把裝備卸掉。
            BeginApply(save);
        }

        internal static void Tick(PlayerSave save)
        {
            if (_step == Step.Idle) return;

            // 角色狀態守門：登出／換角一律中止
            if (save == null || Core.SafeUid(save) != _uid)
            {
                Abort("角色狀態變更，中止還原。");
                return;
            }

            float now = Time.unscaledTime;

            switch (_step)
            {
                case Step.WaitApply:
                    if (ApplyMatches(save))
                    {
                        if (Plugin.CfgDiagnostic.Value) Plugin.Logger.LogInfo("[Build][診斷] 加點驗收通過。");
                        BeginAttributes(save);
                    }
                    else if (now > _deadline)
                    {
                        // 直接送終態的好處:被拒絕時原本的點法原封不動,不會留下半殘狀態
                        Abort("技能點未被伺服器接受（點數或前置技能不符？），你原本的點法沒有變動。");
                    }
                    break;

                case Step.AttrApply:
                    if (Attr.Matches(_target.Attributes, Attr.Read(save.Data)))
                    {
                        if (Plugin.CfgDiagnostic.Value) Plugin.Logger.LogInfo("[Build][診斷] 能力點驗收通過。");
                        BeginGear(save);
                    }
                    else if (now > _deadline)
                    {
                        Plugin.Logger.LogWarning("[Build] 能力點未被伺服器接受，跳過能力點繼續還原其餘項目。");
                        _skippedAttr = "能力點未被接受（點數不足？）";
                        BeginGear(save);
                    }
                    break;

                case Step.Gear:
                    TickGear(save, now);
                    break;

                case Step.Assign:
                    if (_assignIdx >= _slots.Count)
                    {
                        VerifyOrRetry(save);
                        return;
                    }
                    var slot = _slots[_assignIdx];
                    if (!_assignSent)
                    {
                        if (now < _nextAssignAt) return;
                        SendAssign(save, slot.Slot, slot.Id, slot.Lv);
                        _assignSent = true;
                        _deadline = now + AssignTimeout;
                        UiRow.SetStatus($"還原「{_target.Name}」：綁快捷列 {_assignIdx + 1}/{_slots.Count}…");
                    }
                    else
                    {
                        bool ok = AssignedMatches(save, slot);
                        if (ok || now > _deadline)
                        {
                            if (!ok && Plugin.CfgDiagnostic.Value)
                                Plugin.Logger.LogInfo($"[Build][診斷] 快捷格 #{slot.Slot}={slot.Id} 未確認，逕行下一格。");
                            _assignIdx++;
                            _assignSent = false;
                            _nextAssignAt = now + AssignGap;
                        }
                    }
                    break;
            }
        }

        private static void BeginApply(PlayerSave save)
        {
            if (_target.Skills.Count == 0)
            {
                BeginAttributes(save);
                return;
            }
            SendApply(save, _target.Skills);
            _step = Step.WaitApply;
            _deadline = Time.unscaledTime + StepTimeout;
            UiRow.SetStatus($"還原「{_target.Name}」：加點中…（{_target.Skills.Count} 技能）");
        }

        // ---- 能力點階段：接在加點之後、換裝之前 ----

        private static void BeginAttributes(PlayerSave save)
        {
            var want = _target.Attributes;
            // 沒勾、或舊快照沒有能力點資料 ⇒ 整段跳過
            //（絕不能對著空快照做重置，那會把能力點洗光）
            if (!_use.Attributes || want == null || want.Count == 0)
            {
                BeginGear(save);
                return;
            }

            var cur = Attr.Read(save.Data);
            if (Attr.Matches(want, cur))
            {
                if (Plugin.CfgDiagnostic.Value) Plugin.Logger.LogInfo("[Build][診斷] 能力點已相符，跳過。");
                BeginGear(save);
                return;
            }

            // 需要「減能力點」的話,我們什麼都不做。
            //
            // 遊戲裡玩家減能力點的唯一途徑是 Waybinder NPC 的「Reset Attributes」（整體重置）——
            // 能力點視窗只有 + 沒有 −。伺服器端 ApplyAttributes_S 其實只檢查
            // 「GetRemainingPoints >= 0」,**沒有擋負差額**,所以硬送負數是會成功的;
            // 但那就是讓玩家做到遊戲介面做不到的事＝外掛,不是自動化。這條線不能越。
            var lower = want.Where(kv => kv.Value < (cur.TryGetValue(kv.Key, out var c) ? c : 0))
                            .Select(kv => $"{kv.Key} {(cur.TryGetValue(kv.Key, out var c2) ? c2 : 0)}→{kv.Value}")
                            .ToList();
            if (lower.Count > 0)
            {
                _skippedAttr = $"能力點需要調降（{string.Join("、", lower.Take(3))}" +
                    (lower.Count > 3 ? "…" : "") + "）——請先到 Waybinder NPC 重置能力點，再還原一次";
                Plugin.Logger.LogWarning($"[Build] 跳過能力點：{_skippedAttr}");
                BeginGear(save);
                return;
            }

            // 只需加點:算清楚要幾點、夠不夠,不夠就講明白差多少
            int need = want.Sum(kv => kv.Value - (cur.TryGetValue(kv.Key, out var c3) ? c3 : 0));
            int have = SafeRemainingAttrPoints(save);
            if (have >= 0 && need > have)
            {
                _skippedAttr = $"能力點不足：還差 {need - have} 點（需要 {need}、剩餘 {have}）";
                Plugin.Logger.LogWarning($"[Build] 跳過能力點：{_skippedAttr}");
                BeginGear(save);
                return;
            }

            SendAttrApply(save);
        }

        // ---- 換裝階段：先穿裝備再綁快捷列（裝備賦予的技能要先存在才綁得上）----

        private static void BeginGear(PlayerSave save)
        {
            _gear = HasGearData(_target) ? BuildGearQueue(save) : new List<GearAction>();
            _gearMissing.Clear();
            _gearIdx = 0;
            _gearSent = false;
            _gearRound = 0;
            _nextAssignAt = Time.unscaledTime;   // 別沿用上一輪的節流時間

            if (_gear.Count == 0) { BeginAssign(); return; }

            _step = Step.Gear;
            _deadline = Time.unscaledTime + StepTimeout;
            UiRow.SetStatus($"還原「{_target.Name}」：換裝中…（{_gear.Count} 件）");
            if (Plugin.CfgDiagnostic.Value)
                Plugin.Logger.LogInfo("[Build][診斷] 換裝佇列：" + string.Join("、", _gear.Select(g => g.ToString())));
        }

        /// <summary>
        /// 這個 Build 到底有沒有記過裝備？
        /// 沒有就整個跳過換裝——**絕不能**讓「卸下快照沒有的欄位」對著一份沒有裝備資料的
        /// 舊快照跑，那會把角色整身扒光（v1.0 存的 Build 就是這種）。
        /// </summary>
        private static bool HasGearData(Preset p)
        {
            var u = p.Use ?? new PresetFlags();
            return (u.Equips && (p.Equips?.Count ?? 0) > 0)
                || (u.Artifacts && (p.Artifacts?.Count ?? 0) > 0)
                || (u.Grimoires && (p.Grimoires?.Count ?? 0) > 0);
        }

        /// <summary>
        /// 比對快照與現況，產生最小換裝動作集。
        /// 已經對的位置完全不動——少送一次 RPC 就少一次被伺服器拒絕的機會。
        /// </summary>
        private static List<GearAction> BuildGearQueue(PlayerSave save)
        {
            var q = new List<GearAction>();
            try
            {
                var data = save.Data;
                bool clear = _use.ClearUnlisted;

                // --- 裝備 ---（沒勾就整類不動，連「卸下多餘的」都不做）
                var wantEquip = new Dictionary<string, PresetGear>(StringComparer.Ordinal);
                if (_use.Equips)
                    foreach (var g in _target.Equips ?? new List<PresetGear>())
                        if (!string.IsNullOrEmpty(g.Slot)) wantEquip[g.Slot] = g;

                foreach (var kv in wantEquip)
                {
                    string cur = Gear.EquippedUid(data, kv.Key);
                    if (string.Equals(cur, kv.Value.Uid, StringComparison.Ordinal)) continue;
                    q.Add(new GearAction { Kind = GearKind.Equip, Slot = kv.Key, Uid = kv.Value.Uid, Id = kv.Value.Id });
                }

                if (clear && _use.Equips)
                {
                    var list = data.Equips;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var es = list[i];
                            if (es == null || es.Equip == null) continue;
                            string slot = es.Slot.ToString();
                            if (wantEquip.ContainsKey(slot)) continue;
                            q.Add(new GearAction { Kind = GearKind.Unequip, Slot = slot, Id = es.Equip.Id });
                        }
                }

                // --- 神器 ---
                var wantArt = new Dictionary<string, PresetGear>(StringComparer.Ordinal);
                if (_use.Artifacts)
                    foreach (var g in _target.Artifacts ?? new List<PresetGear>())
                        if (!string.IsNullOrEmpty(g.Slot)) wantArt[g.Slot] = g;

                foreach (var kv in wantArt)
                {
                    string cur = Gear.ArtifactUid(data, kv.Key);
                    if (string.Equals(cur, kv.Value.Uid, StringComparison.Ordinal)) continue;
                    q.Add(new GearAction { Kind = GearKind.Artifact, Slot = kv.Key, Uid = kv.Value.Uid, Id = kv.Value.Id });
                }

                if (clear && _use.Artifacts)
                {
                    var list = data.Artifacts;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                        {
                            var a = list[i];
                            if (a == null) continue;
                            string slot = a.Slot.ToString();
                            if (wantArt.ContainsKey(slot)) continue;
                            q.Add(new GearAction { Kind = GearKind.RemoveArtifact, Slot = slot, Id = a.Id });
                        }
                }

                // --- 魔導書（無欄位概念，用集合差集）---
                if (_use.Grimoires)
                {
                    var wantGrim = new HashSet<string>(
                        (_target.Grimoires ?? new List<PresetGear>()).Select(g => g.Uid), StringComparer.Ordinal);

                    var curGrim = data.Grimoires;
                    if (curGrim != null)
                        for (int i = 0; i < curGrim.Count; i++)
                        {
                            string uid = Gear.InstanceId(curGrim[i]);
                            if (uid == null || wantGrim.Contains(uid)) continue;
                            q.Add(new GearAction { Kind = GearKind.RemoveGrimoire, Uid = uid, Id = curGrim[i].Id });
                        }

                    foreach (var g in _target.Grimoires ?? new List<PresetGear>())
                    {
                        if (Gear.HasGrimoire(data, g.Uid)) continue;
                        q.Add(new GearAction { Kind = GearKind.Grimoire, Uid = g.Uid, Id = g.Id });
                    }
                }

                // 先卸後穿：欄位／魔導書上限先讓出來，避免「位置已滿」被拒
                q = q.OrderBy(a => a.Kind == GearKind.Unequip || a.Kind == GearKind.RemoveArtifact ||
                                   a.Kind == GearKind.RemoveGrimoire ? 0 : 1).ToList();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 產生換裝清單失敗，跳過換裝：{ex.Message}");
                return new List<GearAction>();
            }
            return q;
        }

        private static void TickGear(PlayerSave save, float now)
        {
            if (_gearIdx >= _gear.Count)
            {
                // 補裝：重新比對現況，沒到位的再送一輪。
                // 主因是**飾品左右欄由遊戲決定**——ApplyEquip 只收物品不收欄位，
                // 它用 GetOccupiedSlot 挑空的那格。第一輪兩格都空時可能放錯邊，
                // 後面那件再把它擠掉；第二輪時另一格已被正確佔住，剩下的只能進對的欄位。
                if (_gearRound < MaxGearRetry)
                {
                    var again = BuildGearQueue(save);
                    if (again.Count > 0)
                    {
                        _gearRound++;
                        _gear = again;
                        _gearIdx = 0;
                        _gearSent = false;
                        _gearMissing.Clear();
                        _nextAssignAt = now + RetryGap;
                        Plugin.Logger.LogInfo($"[Build] 第 {_gearRound} 次補裝：{again.Count} 件未到位（" +
                            string.Join("、", again.Take(4).Select(a => a.ToString())) + "）");
                        UiRow.SetStatus($"補裝 {again.Count} 件…");
                        return;
                    }
                }

                if (_gearMissing.Count > 0)
                    Plugin.Logger.LogWarning($"[Build] {_gearMissing.Count} 件裝備沒還原：{string.Join("、", _gearMissing)}");
                BeginAssign();
                return;
            }

            var act = _gear[_gearIdx];

            if (!_gearSent)
            {
                if (now < _nextAssignAt) return;
                bool sent = SendGear(save, act);
                if (!sent) { NextGear(now); return; }   // 缺件／不能穿：已記錄，直接下一件
                _gearSent = true;
                _deadline = now + GearTimeout;
                UiRow.SetStatus($"還原「{_target.Name}」：換裝 {_gearIdx + 1}/{_gear.Count}（{act}）…");
                return;
            }

            if (GearMatches(save, act))
            {
                NextGear(now);
            }
            else if (now > _deadline)
            {
                _gearMissing.Add($"{act}(逾時)");
                if (Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo($"[Build][診斷] 換裝逾時：{act}");
                NextGear(now);
            }
        }

        private static void NextGear(float now)
        {
            _gearIdx++;
            _gearSent = false;
            _nextAssignAt = now + GearGap;
        }

        private static bool GearMatches(PlayerSave save, GearAction act)
        {
            try
            {
                var data = save.Data;
                switch (act.Kind)
                {
                    case GearKind.Equip:
                        return string.Equals(Gear.EquippedUid(data, act.Slot), act.Uid, StringComparison.Ordinal);
                    case GearKind.Unequip:
                        return Gear.EquippedUid(data, act.Slot) == null;
                    case GearKind.Artifact:
                        return string.Equals(Gear.ArtifactUid(data, act.Slot), act.Uid, StringComparison.Ordinal);
                    case GearKind.RemoveArtifact:
                        return Gear.ArtifactUid(data, act.Slot) == null;
                    case GearKind.Grimoire:
                        return Gear.HasGrimoire(data, act.Uid);
                    case GearKind.RemoveGrimoire:
                        return !Gear.HasGrimoire(data, act.Uid);
                }
            }
            catch { }
            return false;
        }

        private static void BeginAssign()
        {
            _assignIdx = 0;
            _assignSent = false;
            _nextAssignAt = Time.unscaledTime + GearGap;
            _step = Step.Assign;
        }

        /// <summary>
        /// 綁完一輪後的總驗收：沒對上的格子自動補綁。
        /// 主因是裝備／狀態「賦予」的技能（不在已學清單，如 ShadowStep）——換裝當下
        /// 伺服器跑 ClearLoadouts/ValidateEquips，賦予來源可能暫時消失導致該格被拒。
        /// 沉澱一秒再送，通常就掛得回去。
        /// </summary>
        private static void VerifyOrRetry(PlayerSave save)
        {
            var failed = _allSlots.Where(s => !AssignedMatches(save, s)).ToList();

            if (failed.Count == 0) { Finish(null); return; }

            if (_retry < MaxRetry)
            {
                _retry++;
                Plugin.Logger.LogInfo($"[Build] 第 {_retry} 次補綁：{failed.Count} 格未確認（" +
                    string.Join("、", failed.Take(5).Select(f => $"#{f.Slot}={f.Id}")) + "）");
                UiRow.SetStatus($"補綁 {failed.Count} 格…");
                _slots = failed;
                _assignIdx = 0;
                _assignSent = false;
                _nextAssignAt = Time.unscaledTime + RetryGap;
                _step = Step.Assign;
                return;
            }

            Finish(failed);
        }

        private static void Finish(List<PresetSlot> failed)
        {
            int total = _allSlots.Count;
            int ok = total - (failed?.Count ?? 0);

            string gearNote = _gearMissing.Count > 0
                ? $"　裝備未還原：{string.Join("、", _gearMissing)}" : "";
            string attrNote = _skippedAttr != null ? $"　{_skippedAttr}" : "";

            if ((failed == null || failed.Count == 0) && _gearMissing.Count == 0 && _skippedAttr == null)
            {
                Plugin.Logger.LogInfo($"[Build] 還原完成：「{_target.Name}」{_target.Skills.Count} 技能／{total} 快捷格" +
                    (_gear != null && _gear.Count > 0 ? $"／換裝 {_gear.Count} 件" : ""));
                UiRow.SetStatus($"還原完成：「{_target.Name}」（{_target.Skills.Count} 技能／{total} 快捷格" +
                    (_gear != null && _gear.Count > 0 ? $"／換裝 {_gear.Count} 件）" : "）"));
            }
            else
            {
                string list = failed == null || failed.Count == 0
                    ? "" : "未綁上：" + string.Join("、", failed.Select(f => $"#{f.Slot + 1}={f.Id}"));
                Plugin.Logger.LogWarning($"[Build] 還原完成但有缺漏：{list}{gearNote}{attrNote}");
                UiRow.SetStatus($"還原完成：{_target.Skills.Count} 技能／快捷 {ok}/{total}。{list}{gearNote}{attrNote}", true);
            }

            _step = Step.Idle;
            _target = null;
            _skippedAttr = null;
            UiRow.Refresh();
        }

        private static void Abort(string reason)
        {
            Plugin.Logger.LogWarning($"[Build] {reason}");
            UiRow.SetStatus(reason, true);
            _step = Step.Idle;
            _target = null;
            _skippedAttr = null;
            UiRow.Refresh();
        }

        // ---- 謂詞（每幀對本地 CharacterData 驗收；伺服器回應會整包換新資料）----

        private static bool ApplyMatches(PlayerSave save)
        {
            try
            {
                var list = save.Data.Skills?.Skills;
                if (list == null) return false;
                var current = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; i++)
                {
                    var sd = list[i];
                    if (sd != null && !string.IsNullOrEmpty(sd.Id)) current[sd.Id] = sd.Level;
                }
                foreach (var s in _target.Skills)
                    if (!current.TryGetValue(s.Id, out int lv) || lv != s.Lv) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool AssignedMatches(PlayerSave save, PresetSlot slot)
        {
            try
            {
                var list = save.Data.Skills?.Assigned;
                if (list == null || slot.Slot >= list.Count) return false;
                var sd = list[slot.Slot];
                return sd != null && string.Equals(sd.Id, slot.Id, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        // ---- 發送與遊戲查表：全部關進 NoInlining 方法（遊戲改版簽名變時只死這裡，
        //      SendAvailable 旗標已把路擋住，pump 本體不受牽連）----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendApply(PlayerSave save, List<PresetSkill> skills)
        {
            // SkillData 是 il2cpp class（僅 Id/Level），managed 端 new＋塞 il2cpp List 安全
            //（紅線只禁「含參考型別欄位的 struct」進 il2cpp 泛型容器）。
            // ApplySkills 語義＝完整終態清單（ISIL：遊戲 UI 也是整份送）。
            var list = new Il2CppSystem.Collections.Generic.List<SkillData>();
            foreach (var s in skills)
            {
                var sd = new SkillData { Id = s.Id, Level = s.Lv };
                list.Add(sd);
            }
            save.ApplySkills(list, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendAssign(PlayerSave save, int slot, string id, int lv)
        {
            save.AssignSkill(slot, id, lv);
        }

        /// <summary>剩餘可分配的能力點；拿不到就回 -1（呼叫端跳過預檢，讓伺服器當權威）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SafeRemainingAttrPoints(PlayerSave save)
        {
            try
            {
                var data = save.Data;
                return Formula.GetRemainingPoints(data, data.Attributes, null);
            }
            catch { return -1; }
        }

        /// <summary>
        /// 送出能力點差額。伺服器收的是 change（差額）不是絕對值，
        /// 所以每次都拿「當下」的能力點重新算，避免用過期的基準。
        /// **只送正差額**：負差額＝減能力點，遊戲介面做不到（唯一途徑是 Waybinder 的整體重置），
        /// 伺服器雖然不擋，但送了就越過「只自動化玩家做得到的事」這條線。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendAttrApply(PlayerSave save)
        {
            var want = _target.Attributes;
            var cur = Attr.Read(save.Data);

            var change = new StatArray();
            int total = 0;
            foreach (var kv in want)
            {
                if (!Enum.TryParse(kv.Key, out StatType type)) continue;
                int c = cur.TryGetValue(kv.Key, out var v) ? v : 0;
                int delta = kv.Value - c;
                if (delta <= 0) continue;       // 硬性防線：絕不送負數
                change.Set(type, delta);
                total += delta;
            }

            if (total == 0) { BeginGear(save); return; }

            if (Plugin.CfgDiagnostic.Value)
                Plugin.Logger.LogInfo($"[Build][診斷] 送出能力點差額 共 {total} 點：目標 {Attr.Describe(want)}");

            save.ApplyAttributes(change, null);
            _step = Step.AttrApply;
            _deadline = Time.unscaledTime + StepTimeout;
            UiRow.SetStatus($"還原「{_target.Name}」：配能力點中…（{total} 點）");
        }

        /// <summary>
        /// 送出一件換裝動作。回傳 false＝這件送不出去（缺件／不能穿），已記進 _gearMissing。
        /// 全部走遊戲自己的高階包裝（含它自己的欄位判定與需求檢查），不裸發 _S。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SendGear(PlayerSave save, GearAction act)
        {
            try
            {
                var data = save.Data;
                switch (act.Kind)
                {
                    case GearKind.Equip:
                    {
                        var equip = Gear.FindEquip(data, act.Uid);
                        if (equip == null) { _gearMissing.Add($"{act.Id}(不在背包)"); return false; }
                        var cfg = App.ServerRuntime?.GetEquip(equip.Id);
                        if (cfg != null && !Formula.CanEquip(data, cfg))
                        {
                            _gearMissing.Add($"{act.Id}(需求不符)");
                            return false;
                        }
                        // 重裝武器走另一條入口（ApplyEquip 會擋下並跳訊息）
                        if (cfg != null && EquipUtil.IsHeavy(cfg.Type)) save.ApplyHeavyEquip(equip);
                        else save.ApplyEquip(equip);
                        return true;
                    }
                    case GearKind.Unequip:
                        save.RemoveEquip((EquipSlot)Enum.Parse(typeof(EquipSlot), act.Slot));
                        return true;

                    case GearKind.Artifact:
                    {
                        var art = Gear.FindArtifact(data, act.Uid);
                        if (art == null) { _gearMissing.Add($"{act.Id}(不在背包)"); return false; }
                        // 傳遊戲自己的那個實例（別自己造物件——SellFavorite 的字典物件教訓）
                        save.ApplyArtifact(art);
                        return true;
                    }
                    case GearKind.RemoveArtifact:
                        save.RemoveArtifact((ArtifactSlot)Enum.Parse(typeof(ArtifactSlot), act.Slot));
                        return true;

                    case GearKind.Grimoire:
                    {
                        var g = Gear.FindEquip(data, act.Uid);
                        if (g == null) { _gearMissing.Add($"{act.Id}(不在背包)"); return false; }
                        save.ApplyGrimoire(g);
                        return true;
                    }
                    case GearKind.RemoveGrimoire:
                    {
                        var g = Gear.FindEquip(data, act.Uid);
                        if (g == null) return false;
                        save.RemoveGrimoire(g);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 換裝送出失敗（{act}）：{ex.Message}");
                _gearMissing.Add($"{act.Id}(錯誤)");
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SkillExists(string id)
        {
            try
            {
                var rt = App.ServerRuntime;
                if (rt == null) return true;    // 查不到表就放行，讓伺服器當權威
                return rt.GetSkill(id) != null;
            }
            catch { return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SafeMaxPoints(PlayerSave save)
        {
            try { return Formula.GetSkillPointsMax(save.Data); }
            catch { return 0; }     // 拿不到就跳過預檢，伺服器仍會驗
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SafeMaxSlots(PlayerSave save)
        {
            try { return SkillSystemData.MaxAssignableSlots; }   // interop 上是靜態常數
            catch { return 0; }
        }
    }

    // =========================================================================
    //  技能視窗按鈕列：TMP 文字按鈕＋raycast 點擊偵測（零 delegate，紅線 2）
    // =========================================================================

    internal static class UiRow
    {
        private const string RowName = "SkillBuildsRow";
        private const string BtnPrefix = "SkillBuildsBtn_";

        private static UISkills _ui;
        internal static UISkills Ui => _ui;
        private static GameObject _row;
        private static RectTransform _rowRt;
        private static TextMeshProUGUI _status;
        private static readonly List<TextMeshProUGUI> _labels = new List<TextMeshProUGUI>();
        private static readonly List<Image> _bgs = new List<Image>();
        private static float _statusUntil;

        // 遊戲面板本身近乎全黑，底色太暗會讓按鈕看起來像浮在空中的字（v1.5 首測的抱怨）
        private static readonly Color BgIdle = new Color(0.24f, 0.23f, 0.34f, 0.98f);
        private static readonly Color BgHover = new Color(0.42f, 0.36f, 0.62f, 1.00f);
        private static readonly Color BgBusy = new Color(0.14f, 0.14f, 0.18f, 0.85f);
        private static readonly Color TxNormal = new Color(0.92f, 0.90f, 1.00f);
        private static readonly Color TxEmpty = new Color(0.55f, 0.55f, 0.60f);
        private static readonly Color TxError = new Color(1.00f, 0.45f, 0.40f);
        private static readonly Color TxStatus = new Color(0.75f, 0.85f, 1.00f);

        private const float BtnW = 132f;
        private const float BtnH = 34f;
        private const float Gap = 8f;
        private const float AddBtnW = 40f;
        private const int MaxGroups = 12;

        private static TMP_InputField _input;
        private static GameObject _inputGo;

        // ---- 編輯面板 ----
        private const string ChkPrefix = "SBChk_";
        private const string OkName = "SBOk";
        private const string CancelName = "SBCancel";
        private const string DeleteName = "SBDelete";
        private static float _deleteConfirmUntil;
        private static TextMeshProUGUI _deleteLabel;
        private static string _builtUid;
        private static GameObject _panel;
        private static readonly List<TextMeshProUGUI> _chkLabels = new List<TextMeshProUGUI>();
        private static readonly List<Image> _chkBgs = new List<Image>();
        private static TextMeshProUGUI _panelTitle;
        private static int _editIdx = -1;
        private static PresetFlags _editFlags;

        /// <summary>面板上的勾選項，順序＝畫面由上到下。</summary>
        private static readonly string[] ChkText =
            { "技能點", "快捷列", "能力點", "裝備", "神器", "魔導書", "卸下快照沒有的裝備欄" };

        private static bool GetFlag(PresetFlags f, int i) => i switch
        {
            0 => f.Skills,
            1 => f.Hotbar,
            2 => f.Attributes,
            3 => f.Equips,
            4 => f.Artifacts,
            5 => f.Grimoires,
            _ => f.ClearUnlisted,
        };

        private static void ToggleFlag(PresetFlags f, int i)
        {
            switch (i)
            {
                case 0: f.Skills = !f.Skills; break;
                case 1: f.Hotbar = !f.Hotbar; break;
                case 2: f.Attributes = !f.Attributes; break;
                case 3: f.Equips = !f.Equips; break;
                case 4: f.Artifacts = !f.Artifacts; break;
                case 5: f.Grimoires = !f.Grimoires; break;
                default: f.ClearUnlisted = !f.ClearUnlisted; break;
            }
        }

        private static bool _drawSeen;

        internal static void Attach(UISkills ui)
        {
            if (!_drawSeen)
            {
                _drawSeen = true;
                Plugin.Logger.LogInfo("[Build] 技能視窗 Draw 已攔截，建立按鈕列。");
            }
            _ui = ui;
            Ensure();
            Refresh();
        }

        internal static void Tick(PlayerSave save)
        {
            if (_ui == null || _row == null) return;
            bool open;
            try { open = _ui.gameObject.activeInHierarchy; }
            catch { _ui = null; _row = null; return; }
            if (!open)
            {
                // 視窗被關掉時面板要跟著收，否則下次開啟會卡在編輯狀態
                if (_editIdx >= 0) ClosePanel(save, false);
                return;
            }

            // 換角色：按鈕數與內容都要重來（Build 是每角色各自一套）
            string uid = Core.SafeUid(save);
            if (uid != null && uid != _builtUid && !Machine.Busy)
            {
                Rebuild();
                return;
            }

            // 每幀對位：面板位置／解析度變動時跟著走（成本可忽略）
            SyncPosition();

            // 狀態列到期回到提示文字
            if (_status != null && _statusUntil > 0 && Time.unscaledTime > _statusUntil)
            {
                _statusUntil = 0;
                SetHint();
            }

            // 編輯面板開著：只處理面板互動，其他全擋
            if (_editIdx >= 0) { TickPanel(save); return; }

            int hover = HitIndex();
            bool busy = Machine.Busy;
            int count = _labels.Count - 1;      // 最後一顆是「＋」
            for (int i = 0; i < _bgs.Count; i++)
            {
                if (_bgs[i] == null) continue;
                bool isAdd = i >= count;
                _bgs[i].color = (busy && !isAdd) ? BgBusy : (i == hover ? BgHover : BgIdle);
            }

            if (hover < 0 || Core.IsTypingInInputField()) return;

            if (Input.GetMouseButtonDown(0))
            {
                if (hover >= count) { AddGroup(); return; }
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                Core.Activate(save, hover, shift);
            }
            else if (Input.GetMouseButtonDown(1) && hover < count)
            {
                OpenPanel(save, hover);
            }
        }

        private static void AddGroup()
        {
            int count = VisibleCount();
            if (count >= MaxGroups)
            {
                SetStatus($"最多 {MaxGroups} 組。");
                return;
            }
            Plugin.CfgCount.Value = count + 1;
            Rebuild();
            SetStatus($"已新增第 {count + 1} 組（Shift+左鍵存入、右鍵開編輯面板）。");
        }

        /// <summary>
        /// 要顯示幾顆按鈕：設定值與「這個角色實際存了幾組」取大的。
        /// 組數設定是全域的、Build 資料是每角色的——只看設定值會把別的角色的 Build 藏起來。
        /// </summary>
        private static int VisibleCount()
        {
            int cfg = Plugin.CfgCount.Value;
            int stored = Store.SlotCount(Core.SafeUid(Core.SafeGetSave()));
            return Math.Max(1, Math.Min(MaxGroups, Math.Max(cfg, stored)));
        }

        /// <summary>整排重建（組數變動時）。</summary>
        private static void Rebuild()
        {
            try
            {
                if (_row != null) UnityEngine.Object.Destroy(_row);
            }
            catch { }
            _row = null; _rowRt = null; _status = null;
            _input = null; _inputGo = null; _panel = null; _panelTitle = null;
            _editIdx = -1;
            _labels.Clear(); _bgs.Clear(); _chkLabels.Clear(); _chkBgs.Clear();
            Ensure();
            Refresh();
        }

        private static void OpenPanel(PlayerSave save, int index)
        {
            if (_panel == null)
            {
                SetStatus("編輯面板無法建立，請直接編輯設定資料夾裡的 presets.json。", true);
                return;
            }
            string uid = Core.SafeUid(save);
            var src = Store.GetFlags(uid, index);
            // 編輯的是副本，按取消就原封不動
            _editFlags = new PresetFlags
            {
                Skills = src.Skills,
                Hotbar = src.Hotbar,
                Attributes = src.Attributes,
                Equips = src.Equips,
                Artifacts = src.Artifacts,
                Grimoires = src.Grimoires,
                ClearUnlisted = src.ClearUnlisted,
            };
            _editIdx = index;

            try
            {
                var p = Store.Get(uid, index);
                string name = Store.GetName(uid, index);
                if (_panelTitle != null)
                    _panelTitle.text = $"編輯第 {index + 1} 組" +
                        (p == null ? "（尚未存入配置）" : $"　{p.Skills.Count} 技能／{p.Equips.Count} 裝備");
                _panel.SetActive(true);
                if (_input != null)
                {
                    _input.text = string.IsNullOrEmpty(name) ? $"Build {index + 1}" : name;
                    _input.Select();
                    _input.ActivateInputField();
                    _input.caretPosition = _input.text.Length;
                }
                DrawPanel();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 開啟編輯面板失敗：{ex.Message}");
                ClosePanel(save, false);
                return;
            }
            SetStatus("左鍵勾選要還原的項目，Enter／確定 存檔，Esc／取消 放棄。");
        }

        private static void DrawPanel()
        {
            for (int i = 0; i < _chkLabels.Count; i++)
            {
                if (_chkLabels[i] == null) continue;
                bool on = GetFlag(_editFlags, i);
                // 勾選符號只用 ASCII：☑／☐ 這類字元不保證在遊戲字型裡，
                // 缺字會讓 TMP 每次重繪都噴警告灌爆 log（MarketPrice 的 ◈ 事故）
                _chkLabels[i].text = (on ? "[X]  " : "[  ]  ") + ChkText[i];
                _chkLabels[i].color = on ? TxNormal : TxEmpty;
            }
        }

        /// <summary>面板互動：不掛 delegate，每幀輪詢滑鼠與按鍵（紅線 2）。</summary>
        private static void TickPanel(PlayerSave save)
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { ClosePanel(save, false); return; }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    ClosePanel(save, true); return;
                }

                string hit = HitOurName();

                for (int i = 0; i < _chkBgs.Count; i++)
                {
                    if (_chkBgs[i] == null) continue;
                    bool hover = hit != null && hit == ChkPrefix + i;
                    _chkBgs[i].color = hover ? BgHover : BgIdle;
                }

                // 刪除確認倒數過了就恢復原樣
                if (_deleteConfirmUntil > 0 && Time.unscaledTime > _deleteConfirmUntil)
                {
                    _deleteConfirmUntil = 0;
                    if (_deleteLabel != null) { _deleteLabel.text = "刪除"; _deleteLabel.color = TxError; }
                }

                if (!Input.GetMouseButtonDown(0) || hit == null) return;

                if (hit == OkName) { ClosePanel(save, true); return; }
                if (hit == CancelName) { ClosePanel(save, false); return; }
                if (hit == DeleteName) { ClickDelete(save); return; }
                if (hit.StartsWith(ChkPrefix, StringComparison.Ordinal) &&
                    int.TryParse(hit.Substring(ChkPrefix.Length), out int idx) &&
                    idx >= 0 && idx < ChkText.Length)
                {
                    ToggleFlag(_editFlags, idx);
                    DrawPanel();
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 面板互動失敗：{ex.Message}");
                ClosePanel(save, false);
            }
        }

        /// <summary>刪除要點兩次（第一次變成「確定刪除?」，3 秒內再點才真的刪）。</summary>
        private static void ClickDelete(PlayerSave save)
        {
            if (_deleteConfirmUntil > 0 && Time.unscaledTime <= _deleteConfirmUntil)
            {
                int idx = _editIdx;
                string uid = Core.SafeUid(save);
                string name = Store.GetName(uid, idx) ?? $"Build {idx + 1}";

                _deleteConfirmUntil = 0;
                _editIdx = -1;
                try { _input?.DeactivateInputField(); _panel?.SetActive(false); } catch { }

                Store.Delete(uid, idx);
                Plugin.Logger.LogInfo($"[Build] 已刪除「{name}」（第 {idx + 1} 組），後面的往前遞補。");
                Rebuild();
                SetStatus($"已刪除「{name}」。");
                return;
            }

            _deleteConfirmUntil = Time.unscaledTime + 3f;
            if (_deleteLabel != null) { _deleteLabel.text = "確定刪除?"; _deleteLabel.color = TxNormal; }
            SetStatus("再點一次「確定刪除?」就會刪掉這組（後面的往前遞補）。");
        }

        private static void ClosePanel(PlayerSave save, bool commit)
        {
            int idx = _editIdx;
            _editIdx = -1;
            _deleteConfirmUntil = 0;
            if (_deleteLabel != null) { _deleteLabel.text = "刪除"; _deleteLabel.color = TxError; }
            string text = null;
            try
            {
                text = _input != null ? _input.text : null;
                _input?.DeactivateInputField();
                _panel?.SetActive(false);
            }
            catch { }

            if (!commit || idx < 0) { SetHint(); return; }

            string uid = Core.SafeUid(save);
            Store.SetFlags(uid, idx, _editFlags);

            text = (text ?? "").Trim();
            if (text.Length > 12) text = text.Substring(0, 12);
            if (text.Length > 0) Store.Rename(uid, idx, text);

            Refresh();
            int on = 0;
            for (int i = 0; i < ChkText.Length - 1; i++) if (GetFlag(_editFlags, i)) on++;
            SetStatus($"第 {idx + 1} 組已更新：勾選 {on}/6 類。");
        }

        /// <summary>cfg 微調值變動時跟著移動（改設定不用重開遊戲）。</summary>
        private static void SyncPosition()
        {
            try
            {
                if (_rowRt == null) return;
                var want = new Vector2(-24f + Plugin.CfgPosX.Value, -24f + Plugin.CfgPosY.Value);
                if (_rowRt.anchoredPosition != want) _rowRt.anchoredPosition = want;
            }
            catch { }
        }

        /// <summary>刷新按鈕文字（存檔後／還原結束後呼叫）。</summary>
        internal static void Refresh()
        {
            if (_row == null) return;
            var save = Core.SafeGetSave();
            string uid = Core.SafeUid(save);
            int count = _labels.Count - 1;              // 最後一顆是「＋」
            for (int i = 0; i < _labels.Count; i++)
            {
                if (_labels[i] == null) continue;
                if (i >= count) continue;               // 「＋」按鈕標籤固定
                var p = Store.Get(uid, i);
                string name = Store.GetName(uid, i);
                if (p == null)
                {
                    _labels[i].text = string.IsNullOrEmpty(name) ? $"{i + 1} 空" : $"{i + 1} {name}（空）";
                    _labels[i].color = TxEmpty;
                }
                else
                {
                    // 有取消勾選任何一類就加個 * 提示「這組是部分還原」
                    var u = p.Use ?? new PresetFlags();
                    bool partial = !(u.Skills && u.Hotbar && u.Attributes && u.Equips && u.Artifacts && u.Grimoires);
                    _labels[i].text = $"{i + 1} {p.Name}" + (partial ? " *" : "");
                    _labels[i].color = TxNormal;
                }
            }
        }

        internal static void SetStatus(string text, bool error = false)
        {
            Plugin.Logger.LogInfo($"[Build] {text}");
            if (_status == null) return;
            try
            {
                _status.text = text;
                _status.color = error ? TxError : TxStatus;
                _statusUntil = Time.unscaledTime + 6f;
            }
            catch { }
        }

        private static void SetHint()
        {
            if (_status == null) return;
            try
            {
                _status.text = "左鍵＝還原（點兩次）／Shift+左鍵＝儲存／右鍵＝編輯（改名＋勾選）／＋＝加一組";
                _status.color = TxEmpty;
            }
            catch { }
        }

        private static void Ensure()
        {
            try
            {
                if (_row != null) return;
                if (_ui == null) return;

                // 錨定技能視窗本體頂端中央（不掛在按鈕列的 parent 上——那裡可能有
                // LayoutGroup 會把我們的位置整個蓋掉，v1.0.0 首測就是這樣消失的）
                var windowRt = _ui.GetComponent<RectTransform>();
                if (windowRt == null)
                {
                    Plugin.Logger.LogWarning("[Build] 技能視窗沒有 RectTransform，按鈕列停用（熱鍵仍可用）。");
                    return;
                }

                _builtUid = Core.SafeUid(Core.SafeGetSave());
                int count = VisibleCount();
                float rowW = count * BtnW + count * Gap + AddBtnW;   // 末端多一顆「＋」

                var row = new GameObject(RowName);
                row.transform.SetParent(windowRt, false);
                // 貼技能視窗「右上角」：左下角是遊戲自己的 點數／還原／套用，擠在那會跑版。
                // 右上角在各解析度下都是空的（分頁標籤只到中間）。位置可用 cfg 微調。
                var rowRt = row.AddComponent<RectTransform>();
                rowRt.anchorMin = new Vector2(1f, 1f);
                rowRt.anchorMax = new Vector2(1f, 1f);
                rowRt.pivot = new Vector2(1f, 1f);
                rowRt.sizeDelta = new Vector2(rowW, BtnH);
                rowRt.anchoredPosition = new Vector2(-24f + Plugin.CfgPosX.Value,
                                                     -24f + Plugin.CfgPosY.Value);
                // 防視窗根上有 LayoutGroup 亂排我們
                var le = row.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                row.transform.SetAsLastSibling();
                _row = row;
                _rowRt = rowRt;

                TMP_FontAsset font = null;
                try { font = _ui.SkillPoints != null ? _ui.SkillPoints.font : null; } catch { }

                _labels.Clear();
                _bgs.Clear();
                for (int i = 0; i <= count; i++)     // 最後一顆是「＋」
                {
                    bool isAdd = (i == count);
                    float w = isAdd ? AddBtnW : BtnW;

                    var btn = new GameObject(BtnPrefix + i);
                    btn.transform.SetParent(row.transform, false);
                    var rt = btn.AddComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0f, 0.5f);
                    rt.anchorMax = new Vector2(0f, 0.5f);
                    rt.pivot = new Vector2(0f, 0.5f);
                    rt.sizeDelta = new Vector2(w, BtnH);
                    rt.anchoredPosition = new Vector2(i * (BtnW + Gap), 0f);

                    var bg = btn.AddComponent<Image>();
                    bg.color = BgIdle;
                    bg.raycastTarget = true;
                    _bgs.Add(bg);

                    var labelGo = new GameObject("label");
                    labelGo.transform.SetParent(btn.transform, false);
                    var lrt = labelGo.AddComponent<RectTransform>();
                    lrt.anchorMin = Vector2.zero;
                    lrt.anchorMax = Vector2.one;
                    lrt.offsetMin = new Vector2(4f, 1f);
                    lrt.offsetMax = new Vector2(-4f, -1f);

                    var tmp = labelGo.AddComponent<TextMeshProUGUI>();
                    if (font != null) tmp.font = font;
                    tmp.alignment = TextAlignmentOptions.Center;
                    // 固定字級（autosize 會把字縮成看不清楚，v1.0.0 首測的抱怨點）
                    tmp.enableAutoSizing = false;
                    tmp.fontSize = 19f;
                    tmp.fontStyle = FontStyles.Bold;
                    tmp.raycastTarget = false;
                    tmp.text = isAdd ? "+" : $"{i + 1} 空";
                    tmp.color = isAdd ? TxNormal : TxEmpty;
                    _labels.Add(tmp);
                }

                BuildPanel(row.transform, font);

                // 狀態列：按鈕列正下方、靠右對齊往左延伸（貼右上角，往右會戳出視窗外）
                var statusGo = new GameObject("SkillBuildsStatus");
                statusGo.transform.SetParent(row.transform, false);
                var srt = statusGo.AddComponent<RectTransform>();
                srt.anchorMin = new Vector2(1f, 0f);
                srt.anchorMax = new Vector2(1f, 0f);
                srt.pivot = new Vector2(1f, 1f);
                srt.sizeDelta = new Vector2(Math.Max(rowW * 2.2f, 900f), 24f);
                srt.anchoredPosition = new Vector2(0f, -6f);
                _status = statusGo.AddComponent<TextMeshProUGUI>();
                if (font != null) _status.font = font;
                _status.alignment = TextAlignmentOptions.Right;
                _status.enableAutoSizing = false;
                _status.fontSize = 17f;
                _status.raycastTarget = false;
                SetHint();

                // 永遠印建立位置：對位靠這行
                Plugin.Logger.LogInfo($"[Build] 按鈕列已建：視窗={windowRt.rect.size} 位置=右上角" +
                    $"{rowRt.anchoredPosition} 尺寸={rowRt.sizeDelta} 按鈕數={count}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 建立按鈕列失敗：{ex.Message}");
            }
        }

        /// <summary>
        /// 編輯面板：改名輸入框 ＋ 每組獨立的「還原時要動哪些」勾選。
        /// 全部用遊戲既有的 il2cpp 型別（Image / TMP / TMP_InputField），
        /// 不注入自訂 MonoBehaviour（紅線 1）、不掛任何 delegate（紅線 2，改每幀輪詢）。
        /// </summary>
        private static void BuildPanel(Transform parent, TMP_FontAsset font)
        {
            try
            {
                const float PanelW = 340f;
                const float RowH = 30f;
                const float Pad = 12f;
                float panelH = Pad + 26f + 6f + BtnH + 8f + ChkText.Length * RowH + 10f + 32f + Pad;

                var panel = new GameObject("SkillBuildsPanel");
                panel.transform.SetParent(parent, false);
                var prt = panel.AddComponent<RectTransform>();
                prt.anchorMin = new Vector2(0f, 0.5f);
                prt.anchorMax = new Vector2(0f, 0.5f);
                prt.pivot = new Vector2(0f, 1f);
                prt.sizeDelta = new Vector2(PanelW, panelH);
                prt.anchoredPosition = new Vector2(0f, -BtnH);   // 掛在按鈕列正下方
                var pbg = panel.AddComponent<Image>();
                pbg.color = new Color(0.07f, 0.07f, 0.11f, 0.98f);
                pbg.raycastTarget = true;      // 吃掉點擊，避免穿透到底下的技能樹
                panel.transform.SetAsLastSibling();
                _panel = panel;

                float y = -Pad;

                // 標題
                _panelTitle = MakeText(panel.transform, "Title", font, 17f,
                    new Vector2(Pad, y), new Vector2(PanelW - Pad * 2, 26f), TextAlignmentOptions.Left);
                _panelTitle.color = TxStatus;
                _panelTitle.text = "編輯";
                y -= 26f + 6f;

                // 改名輸入框
                BuildInput(panel.transform, font, new Vector2(Pad, y), new Vector2(PanelW - Pad * 2, BtnH));
                y -= BtnH + 8f;

                // 勾選項
                _chkLabels.Clear();
                _chkBgs.Clear();
                for (int i = 0; i < ChkText.Length; i++)
                {
                    var row = new GameObject(ChkPrefix + i);
                    row.transform.SetParent(panel.transform, false);
                    var rt = row.AddComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.sizeDelta = new Vector2(PanelW - Pad * 2, RowH - 2f);
                    rt.anchoredPosition = new Vector2(Pad, y);
                    var bg = row.AddComponent<Image>();
                    bg.color = BgIdle;
                    bg.raycastTarget = true;
                    _chkBgs.Add(bg);

                    var lbl = MakeText(row.transform, "label", font, 17f,
                        new Vector2(8f, 0f), new Vector2(PanelW - Pad * 2 - 16f, RowH - 2f),
                        TextAlignmentOptions.Left);
                    _chkLabels.Add(lbl);
                    y -= RowH;
                }

                y -= 10f;
                float bw = (PanelW - Pad * 2 - 16f) / 3f;
                MakeButton(panel.transform, OkName, "確定", font, new Vector2(Pad, y), new Vector2(bw, 32f));
                MakeButton(panel.transform, CancelName, "取消", font,
                    new Vector2(Pad + bw + 8f, y), new Vector2(bw, 32f));
                _deleteLabel = MakeButton(panel.transform, DeleteName, "刪除", font,
                    new Vector2(Pad + (bw + 8f) * 2f, y), new Vector2(bw, 32f));
                _deleteLabel.color = TxError;

                panel.SetActive(false);
            }
            catch (Exception ex)
            {
                _panel = null;
                _input = null;
                _inputGo = null;
                Plugin.Logger.LogWarning($"[Build] 編輯面板建立失敗（改名／勾選停用，可直接編輯 presets.json）：{ex.Message}");
            }
        }

        private static TextMeshProUGUI MakeText(Transform parent, string name, TMP_FontAsset font,
            float size, Vector2 pos, Vector2 sz, TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = sz;
            rt.anchoredPosition = pos;
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.alignment = align;
            t.enableAutoSizing = false;
            t.fontSize = size;
            t.raycastTarget = false;
            t.color = TxNormal;
            return t;
        }

        private static TextMeshProUGUI MakeButton(Transform parent, string name, string label,
            TMP_FontAsset font, Vector2 pos, Vector2 sz)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = sz;
            rt.anchoredPosition = pos;
            var bg = go.AddComponent<Image>();
            bg.color = BgIdle;
            bg.raycastTarget = true;
            var t = MakeText(go.transform, "label", font, 18f, Vector2.zero, sz, TextAlignmentOptions.Center);
            t.text = label;
            t.fontStyle = FontStyles.Bold;
            return t;
        }

        private static void BuildInput(Transform parent, TMP_FontAsset font, Vector2 pos, Vector2 sz)
        {
            var go = new GameObject("SkillBuildsRename");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = sz;
            rt.anchoredPosition = pos;

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.03f, 0.03f, 0.05f, 1f);

            var areaGo = new GameObject("TextArea");
            areaGo.transform.SetParent(go.transform, false);
            var art = areaGo.AddComponent<RectTransform>();
            art.anchorMin = Vector2.zero;
            art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(6f, 2f);
            art.offsetMax = new Vector2(-6f, -2f);
            areaGo.AddComponent<RectMask2D>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(areaGo.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            var text = textGo.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.alignment = TextAlignmentOptions.Left;
            text.enableAutoSizing = false;
            text.fontSize = 18f;
            text.color = TxNormal;

            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = art;
            input.textComponent = text;
            if (font != null) input.fontAsset = font;
            input.pointSize = 18f;
            input.characterLimit = 12;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.onFocusSelectAll = true;

            _inputGo = go;
            _input = input;
        }

        /// <summary>
        /// 滑鼠正下方（最上層）是不是我們的互動元件；是就回傳它的名稱，否則 null。
        /// 只認最上層＝被別的視窗蓋住時不會誤觸。
        /// </summary>
        private static string HitOurName()
        {
            try
            {
                var es = EventSystem.current;
                if (es == null) return null;

                var ped = new PointerEventData(es);
                var mp = Input.mousePosition;
                ped.position = new Vector2(mp.x, mp.y);

                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                es.RaycastAll(ped, results);
                if (results.Count == 0) return null;

                var go = results[0].gameObject;
                if (go == null) return null;

                // 吃 raycast 的是 Image（掛在命名物件上），label 不吃；
                // 往上找幾層是為了保險（例如 TMP_InputField 的內部子物件）
                var t = go.transform;
                for (int d = 0; d < 3 && t != null; d++, t = t.parent)
                {
                    string n = t.name;
                    if (n.StartsWith(BtnPrefix, StringComparison.Ordinal) ||
                        n.StartsWith(ChkPrefix, StringComparison.Ordinal) ||
                        n == OkName || n == CancelName || n == "SkillBuildsPanel")
                        return n;
                }
            }
            catch { }
            return null;
        }

        /// <summary>滑鼠下方是不是 Build 按鈕；回傳按鈕序號，否則 -1。</summary>
        private static int HitIndex()
        {
            string n = HitOurName();
            if (n == null || !n.StartsWith(BtnPrefix, StringComparison.Ordinal)) return -1;
            return int.TryParse(n.Substring(BtnPrefix.Length), out int idx) ? idx : -1;
        }
    }
}
