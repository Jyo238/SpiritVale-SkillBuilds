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
    /// Build 記憶：把「技能點＋能力點＋快捷列＋裝備／神器／魔導書＋時裝／外觀」存成 Build，一鍵還原。
    ///   還原順序：套用點法 → 配能力點 → 換裝 → 時裝／外觀 → 綁快捷列。
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
        public const string VERSION = "1.1.0";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<int> CfgCount;
        internal static ConfigEntry<bool> CfgHotkeys;
        internal static ConfigEntry<bool> CfgAttributes;
        internal static ConfigEntry<bool> CfgGear;
        internal static ConfigEntry<bool> CfgClearUnlisted;
        internal static ConfigEntry<bool> CfgCosmetics;
        internal static ConfigEntry<bool> CfgAppearance;
        internal static ConfigEntry<float> CfgPosX;
        internal static ConfigEntry<float> CfgPosY;
        internal static ConfigEntry<string> CfgStorePath;
        internal static ConfigEntry<bool> CfgDiagnostic;
        internal static ConfigEntry<bool> CfgRemountGrimoires;

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
                "還原順序是 套用點法→配能力點→穿裝備→時裝／外觀→綁快捷列：裝備先回到身上，" +
                "裝備賦予的技能才綁得上快捷列。");
            CfgClearUnlisted = Config.Bind("2.新組預設勾選", "卸下快照沒有的裝備", true,
                "新存的 Build，預設要不要勾「卸下快照沒有的裝備欄／神器欄／時裝欄」＝忠實還原當時的樣子；" +
                "不勾＝只穿上快照有的，其餘保持現狀。");
            CfgCosmetics = Config.Bind("2.新組預設勾選", "時裝", false,
                "新存的 Build，預設要不要勾時裝（衣櫃裡套用的外裝／武器外觀／坐騎／寵物／特效等）。" +
                "**預設不勾**：時裝通常是「人」的打扮不是「流派」的一部分，存完 Build 之後再換的坐騎、寵物" +
                "會在還原時被換回快照當時的樣子。想每組 Build 各有一套打扮的人再打勾（右鍵該組 → 編輯面板）。" +
                "還原走遊戲衣櫃的「套用」同一條路，只能套用衣櫃裡已擁有的時裝。");
            CfgAppearance = Config.Bind("2.新組預設勾選", "外觀", false,
                "新存的 Build，預設要不要勾外觀（人物長相：膚色／髮型／髮色／眉／鬍／嘴／眼／眼色／耳／瞳）。" +
                "**預設不勾**：理由同時裝——存完 Build 之後改的髮型會在還原時被改回去。" +
                "還原走衣櫃「外觀」頁籤／造型師 NPC 的同一條路，免費、無次數限制。");
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
            CfgRemountGrimoires = Config.Bind("3.診斷", "換裝後重掛魔導書", false,
                "實驗選項：還原的換裝階段結束後，把身上的魔導書逐本「卸下再裝回」（跟你手動拔掉重裝一樣的兩個動作）。" +
                "有玩家回報切 Build 後魔導書的「替換」效果（例如 Elementalist 的 Elemental Attunement）沒接上、" +
                "要手動拔掉重裝才會好——先用這個擋著；根因查清楚後這個選項會拿掉。每本多送 2 個 RPC。");

            CleanOrphanedConfig();

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

        /// <summary>
        /// 清掉歷代改名留下的孤兒設定鍵（素質點→能力點、預設組數量→Build 數量、自由洗點…）。
        /// BepInEx 認不得的鍵會原封留在 cfg 裡，於是升級者會在同一個區段同時看到
        /// 「素質點 = true」和「能力點 = false」而誤會。純美觀問題，插件本來就不讀它們。
        /// OrphanedEntries 是 protected internal，只能反射拿；拿不到就靜靜跳過。
        /// </summary>
        private void CleanOrphanedConfig()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (prop?.GetValue(Config) is System.Collections.IDictionary dict && dict.Count > 0)
                {
                    int n = dict.Count;
                    dict.Clear();
                    Config.Save();
                    Logger.LogInfo($"[Build] 已清除 {n} 個舊版遺留的設定鍵。");
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug($"[Build] 清理舊設定鍵失敗（不影響功能）：{ex.Message}");
            }
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

    /// <summary>時裝一格：CosmeticSlot 名稱（Head/Chest/Mount/Pet/Aura…）＋衣櫃物品 id。</summary>
    internal class PresetCosmetic
    {
        public string Slot { get; set; }
        public string Id { get; set; }
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
        /// <summary>
        /// 時裝／外觀：類別預設 **false**——舊版 JSON 沒有這兩個欄位，反序列化會吃這裡的預設值；
        /// 舊 Build 重存後若預設 true，玩家的長相／坐騎會在還原時被靜靜換掉。新建 Build 依 cfg 決定。
        /// </summary>
        public bool Cosmetics { get; set; } = false;
        public bool Appearance { get; set; } = false;
        public bool ClearUnlisted { get; set; } = true;

        /// <summary>新建 Build 的預設勾選（吃 cfg「2.新組預設勾選」）。</summary>
        internal static PresetFlags NewDefaults() => new PresetFlags
        {
            Skills = true,
            Hotbar = true,
            Attributes = Plugin.CfgAttributes?.Value ?? false,
            Equips = Plugin.CfgGear?.Value ?? true,
            Artifacts = Plugin.CfgGear?.Value ?? true,
            Grimoires = Plugin.CfgGear?.Value ?? true,
            Cosmetics = Plugin.CfgCosmetics?.Value ?? false,
            Appearance = Plugin.CfgAppearance?.Value ?? false,
            ClearUnlisted = Plugin.CfgClearUnlisted?.Value ?? true,
        };

        internal PresetFlags Clone() => new PresetFlags
        {
            Skills = Skills, Hotbar = Hotbar, Attributes = Attributes, Equips = Equips,
            Artifacts = Artifacts, Grimoires = Grimoires, Cosmetics = Cosmetics,
            Appearance = Appearance, ClearUnlisted = ClearUnlisted,
        };
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
        /// <summary>
        /// 時裝（衣櫃套用的外裝／武器外觀／坐騎／寵物／特效…）。
        /// **刻意不給預設值**：null＝舊快照沒記過（整段跳過），空清單＝當時一件都沒穿（會全卸）。
        /// </summary>
        public List<PresetCosmetic> Cosmetics { get; set; }
        /// <summary>外觀（長相）：欄位名 → 值（BodyColor/Hair/HairColor/Brow/Beard/Mouth/Eye/EyeColor/Ears/Iris）。null＝舊快照沒記。</summary>
        public Dictionary<string, int> Appearance { get; set; }
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
            Backup(uid, index, list[index], "刪除");
            list.RemoveAt(index);
            while (list.Count > 0 && list[list.Count - 1] == null) list.RemoveAt(list.Count - 1);
            if (list.Count == 0) _all.Remove(uid);
            Save();
        }

        /// <summary>取這組的勾選設定（空組給「新組預設勾選」，跟之後存入時一致）。</summary>
        internal static PresetFlags GetFlags(string uid, int index)
        {
            if (string.IsNullOrEmpty(uid)) return PresetFlags.NewDefaults();
            if (!_all.TryGetValue(uid, out var list) || list == null) return PresetFlags.NewDefaults();
            if (index < 0 || index >= list.Count || list[index] == null) return PresetFlags.NewDefaults();
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

        /// <summary>
        /// 只更新這組的時裝／外觀資料並把這兩個勾選打開，其餘欄位（含其他勾選）不動；
        /// 沒這組就什麼都不做。
        /// </summary>
        internal static void PutLook(string uid, int index, List<PresetCosmetic> cosmetics,
            Dictionary<string, int> appearance)
        {
            var p = Get(uid, index);
            if (p == null) return;
            if (cosmetics != null) p.Cosmetics = cosmetics;
            if (appearance != null) p.Appearance = appearance;
            p.Use ??= new PresetFlags();
            p.Use.Cosmetics = true;
            p.Use.Appearance = true;
            Save();
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
                Backup(uid, index, list[index], "覆寫");
            }
            list[index] = preset;
            Save();
        }

        private static void Save()
        {
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_all, JsonOpts)); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[Build] 寫入 Build 清單失敗：{ex.Message}"); }
        }

        // ---- 被覆寫／刪除的舊 Build 備份：同資料夾的 *.backup.json，每角色留最近 30 筆 ----
        //   救回來＝把那筆的 Preset 物件貼回 presets.json 對應位置（純資料檔，關遊戲後手動編輯即可）。
        //   動機：Shift+左鍵一手滑就把整組蓋掉，之前完全沒有退路（2026-08-16 使用者實錄）。

        private class BackupEntry
        {
            public string At { get; set; }
            public string Reason { get; set; }
            public int Index { get; set; }
            public string Name { get; set; }
            public Preset Preset { get; set; }
        }

        private const int MaxBackupPerChar = 30;

        internal static string BackupPath => _path == null ? null
            : Path.Combine(Path.GetDirectoryName(_path) ?? "", Path.GetFileNameWithoutExtension(_path) + ".backup.json");

        private static void Backup(string uid, int index, Preset old, string reason)
        {
            // 空殼（只有名字／勾選、沒配置）不值得備份
            if (old == null || ((old.Skills?.Count ?? 0) == 0 && (old.Assigned?.Count ?? 0) == 0)) return;
            try
            {
                string bp = BackupPath;
                if (bp == null) return;
                Dictionary<string, List<BackupEntry>> all = null;
                if (File.Exists(bp))
                {
                    try { all = JsonSerializer.Deserialize<Dictionary<string, List<BackupEntry>>>(File.ReadAllText(bp)); }
                    catch { all = null; }   // 壞掉就重建，別因為備份檔壞了連正常存檔都卡住
                }
                all ??= new Dictionary<string, List<BackupEntry>>(StringComparer.Ordinal);
                if (!all.TryGetValue(uid, out var list) || list == null) all[uid] = list = new List<BackupEntry>();
                list.Add(new BackupEntry
                {
                    At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Reason = reason,
                    Index = index,
                    Name = old.Name,
                    Preset = old,
                });
                while (list.Count > MaxBackupPerChar) list.RemoveAt(0);
                File.WriteAllText(bp, JsonSerializer.Serialize(all, JsonOpts));
                Plugin.Logger.LogInfo($"[Build] 已把被{reason}的舊「{old.Name}」（第 {index + 1} 組，{old.SavedAt}）備份到 {bp}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 備份舊 Build 失敗（不影響存檔）：{ex.Message}");
            }
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
        private static int _confirmSaveIdx = -1;
        private static float _confirmSaveUntil;

        /// <summary>還原完成後延遲再印一次診斷（狀態效果是排隊套用的，完成當下不一定落地）。</summary>
        internal static float PostDumpAt;

        internal static void OnUpdate()
        {
            var save = SafeGetSave();
            Machine.Tick(save);
            UiRow.Tick(save);
            TickHotkeys(save);
            if (_confirmIdx >= 0 && Time.unscaledTime > _confirmUntil) _confirmIdx = -1;
            if (_confirmSaveIdx >= 0 && Time.unscaledTime > _confirmSaveUntil) _confirmSaveIdx = -1;

            if (PostDumpAt > 0 && Time.unscaledTime > PostDumpAt)
            {
                PostDumpAt = 0;
                Diag.Dump("完成後 2 秒");
            }

            // Ctrl+Shift+D：隨時手動印一次狀態元件快照（追魔導書替換問題用）
            try
            {
                if (save != null && !IsTypingInInputField() &&
                    (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                    (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) &&
                    Input.GetKeyDown(KeyCode.D))
                {
                    Diag.Dump("手動 Ctrl+Shift+D");
                    UiRow.SetStatus("已把狀態元件快照寫進 log（Ctrl+Shift+D）。");
                }
            }
            catch { }
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

            string uid = SafeUid(save);

            if (shift)
            {
                // 覆寫既有 Build 也要點兩次——「Shift+左鍵＝把**目前身上**的一切整組蓋掉那組」
                // 是實測最容易出事的地方：剛還原完 A、想幫 B 補個時裝就 Shift+點 B，B 的點法就變成 A 的了
                //（2026-08-16 使用者實錄）。只想更新打扮走右鍵面板的「存打扮」。
                var existing = Store.Get(uid, index);
                if (existing != null && !(_confirmSaveIdx == index && Time.unscaledTime <= _confirmSaveUntil))
                {
                    _confirmSaveIdx = index;
                    _confirmSaveUntil = Time.unscaledTime + 3f;
                    UiRow.SetStatus($"再按一次確認「覆寫 {existing.Name}」——會用你目前身上的技能／裝備／打扮整組蓋掉它" +
                        "（只想更新打扮：右鍵這組 →「把目前的時裝／外觀存進這組」）", true);
                    return;
                }
                _confirmSaveIdx = -1;
                SavePreset(save, index);
                return;
            }

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
                Look.Snapshot(data, preset);
                preset.Attributes = Attr.Read(data);
                if (Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo("[Build][診斷] 能力點快照：" +
                        string.Join("、", preset.Attributes.Select(kv => $"{kv.Key}={kv.Value}")));

                preset.Use = PresetFlags.NewDefaults();

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
                if (preset.Cosmetics != null) gear += $"／{preset.Cosmetics.Count} 時裝";
                if (preset.Appearance != null && preset.Appearance.Count > 0) gear += "／外觀";
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
        internal const string AccL = "AccessoryLeft";
        internal const string AccR = "AccessoryRight";

        internal static bool IsAccessorySlot(string slotName) =>
            string.Equals(slotName, AccL, StringComparison.Ordinal) ||
            string.Equals(slotName, AccR, StringComparison.Ordinal);

        internal static string InstanceId(InventoryItemData item)
        {
            try { return item != null ? item.GetInstanceId() : null; } catch { return null; }
        }

        /// <summary>
        /// 這件裝備是不是「唯一」（`EquipConfig.Unique`）。查不到設定就當一般件——
        /// 規劃時猜錯欄位頂多多跑一輪補裝，補裝重試會收斂。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IsUnique(string equipId)
        {
            try
            {
                if (string.IsNullOrEmpty(equipId)) return false;
                var cfg = App.ServerRuntime?.GetEquip(equipId);
                return cfg != null && cfg.Unique;
            }
            catch { return false; }
        }

        /// <summary>該裝備欄上物品的設定 id（給人看的），空欄＝null。</summary>
        internal static string EquippedId(CharacterData data, string slotName)
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
                        return es.Equip.Id;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 這件物品現在在哪：背包／身上某欄／魔導書／找不到。監控用——
        /// `ApplyEquip_S` 伺服器端只從**背包**撈物品，已經穿在身上的送過去會被靜默忽略（＝逾時）。
        /// </summary>
        internal static string Locate(CharacterData data, string uid)
        {
            try
            {
                var inv = data.Inventory?.Equips;
                if (inv != null)
                    foreach (var kv in inv)
                        if (kv.Value != null && string.Equals(InstanceId(kv.Value), uid, StringComparison.Ordinal))
                            return "背包";

                var eq = data.Equips;
                if (eq != null)
                    for (int i = 0; i < eq.Count; i++)
                        if (eq[i] != null && eq[i].Equip != null &&
                            string.Equals(InstanceId(eq[i].Equip), uid, StringComparison.Ordinal))
                            return "身上" + eq[i].Slot;

                var gr = data.Grimoires;
                if (gr != null)
                    for (int i = 0; i < gr.Count; i++)
                        if (string.Equals(InstanceId(gr[i]), uid, StringComparison.Ordinal)) return "魔導書欄";
            }
            catch { }
            return "找不到";
        }

        /// <summary>兩個飾品欄的現況，一行給 log 看：`L=Glove_Int(唯一) R=SafetyGloves`。</summary>
        internal static string AccState(CharacterData data)
        {
            string l = EquippedId(data, AccL);
            string r = EquippedId(data, AccR);
            string Tag(string id) => id == null ? "空" : id + (IsUnique(id) ? "(唯一)" : "");
            return $"L={Tag(l)} R={Tag(r)}";
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
    //  時裝／外觀：衣櫃「套用」與「外觀」頁籤走的同一條路
    // =========================================================================

    internal enum LookKind { Cosmetic, RemoveCosmetic, Appearance }

    internal class LookAction
    {
        public LookKind Kind;
        public string Slot;     // CosmeticSlot 名稱
        public string Id;
        public override string ToString() =>
            Kind switch
            {
                LookKind.Cosmetic => $"時裝{Slot}={Id}",
                LookKind.RemoveCosmetic => $"卸時裝{Slot}",
                LookKind.Appearance => "外觀",
                _ => Kind.ToString(),
            };
    }

    /// <summary>
    /// 遊戲事實（ISIL 逐條讀過，2026-08-16）：
    ///   ・時裝：`PlayerSave.ApplyCosmetic(id, slot)` → `ApplyCosmetic_S` → 伺服器只驗「衣櫃裡有這件」
    ///     （WardrobeData.Get），無戰鬥閘門、無費用；`RemoveCosmetic(slot)` 同。這正是衣櫃 UI「套用」按的路。
    ///     坐騎（Mount）、寵物（Pet）、稱號、特效…都只是 CosmeticSlot 的一格，一視同仁。
    ///     武器外觀（Mainhand/Offhand）衣櫃 UI 會先檢查跟手上武器類型相容（CosmeticItem.WeaponType），
    ///     伺服器不驗但畫面不會顯示不相容的皮——我們照 UI 的規矩先擋。
    ///   ・外觀：`PlayerSave.ApplyAppearance(CharacterAppearanceDto, cb)` → 伺服器直接存，
    ///     **免費、無次數、無 NPC 檢查**。入口有兩個：衣櫃頁籤（隨處可開）與造型師 NPC。
    ///     DTO 用遊戲自己的工廠 `CharacterAppearanceDto.Create(CharacterAppearanceData, name, archetype)` 造，
    ///     不自己拼 struct（含 string 欄位的 il2cpp struct 是紅線）。name/archetype 伺服器端不用，只是照樣填。
    /// </summary>
    internal static class Look
    {
        internal static readonly string[] AppearanceKeys =
            { "BodyColor", "Hair", "HairColor", "Brow", "Beard", "Mouth", "Eye", "EyeColor", "Ears", "Iris" };

        internal static void Snapshot(CharacterData data, Preset preset)
        {
            try
            {
                var list = new List<PresetCosmetic>();
                foreach (var kv in CurrentCosmetics(data))
                    list.Add(new PresetCosmetic { Slot = kv.Key, Id = kv.Value });
                preset.Cosmetics = list;
            }
            catch (Exception ex)
            {
                preset.Cosmetics = null;
                Plugin.Logger.LogWarning($"[Build] 記錄時裝失敗（其餘仍會存）：{ex.Message}");
            }

            try { preset.Appearance = ReadAppearance(data); }
            catch (Exception ex)
            {
                preset.Appearance = null;
                Plugin.Logger.LogWarning($"[Build] 記錄外觀失敗（其餘仍會存）：{ex.Message}");
            }

            if (Plugin.CfgDiagnostic.Value)
                Plugin.Logger.LogInfo("[Build][診斷] 時裝快照：" +
                    string.Join("、", (preset.Cosmetics ?? new List<PresetCosmetic>()).Select(c => $"{c.Slot}={c.Id}")) +
                    "；外觀：" + (preset.Appearance == null ? "（無）" :
                        string.Join(" ", preset.Appearance.Select(kv => $"{kv.Key}{kv.Value}"))));
        }

        /// <summary>身上目前套用的時裝：CosmeticSlot 名稱 → id。</summary>
        internal static Dictionary<string, string> CurrentCosmetics(CharacterData data)
        {
            var res = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var list = data.Cosmetics;
                if (list == null) return res;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null || string.IsNullOrEmpty(c.Id)) continue;
                    res[c.Slot.ToString()] = c.Id;
                }
            }
            catch { }
            return res;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Dictionary<string, int> ReadAppearance(CharacterData data)
        {
            var a = data.Appearance;
            if (a == null) return null;
            return new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["BodyColor"] = a.BodyColor, ["Hair"] = a.Hair, ["HairColor"] = a.HairColor,
                ["Brow"] = a.Brow, ["Beard"] = a.Beard, ["Mouth"] = a.Mouth, ["Eye"] = a.Eye,
                ["EyeColor"] = a.EyeColor, ["Ears"] = a.Ears, ["Iris"] = a.Iris,
            };
        }

        internal static bool AppearanceMatches(Dictionary<string, int> want, CharacterData data)
        {
            Dictionary<string, int> cur;
            try { cur = ReadAppearance(data); } catch { return false; }
            if (want == null || cur == null) return false;
            foreach (var kv in want)
                if (!cur.TryGetValue(kv.Key, out int v) || v != kv.Value) return false;
            return true;
        }

        internal static string DescribeAppearance(Dictionary<string, int> a) =>
            a == null ? "（無）" : string.Join(" ", AppearanceKeys.Where(a.ContainsKey).Select(k => $"{k}{a[k]}"));

        /// <summary>衣櫃裡有沒有這件（伺服器端 ApplyCosmetic_S 的唯一檢查，先擋免得白送）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool InWardrobe(PlayerSave save, string id)
        {
            try
            {
                var w = save.PlayerData?.Wardrobe;
                if (w == null) return true;      // 拿不到就放行，讓伺服器當權威
                return w.Get(id) != null;
            }
            catch { return true; }
        }

        /// <summary>時裝設定是否還存在（改版可能移除）。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool CosmeticExists(string id)
        {
            try
            {
                var rt = App.ServerRuntime;
                if (rt == null) return true;
                return rt.GetCosmetic(id) != null;
            }
            catch { return true; }
        }

        /// <summary>
        /// 武器外觀跟手上武器類型相不相容（照衣櫃 UI `IsWeaponSkinCompatible` 的規矩）：
        /// 皮的 WeaponType==Invalid ＝ 通用；否則要等於該手武器的 EquipType；那手沒武器就當不相容。
        /// 非武器欄一律相容。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool WeaponSkinCompatible(CharacterData data, string slotName, string id)
        {
            try
            {
                string equipSlot = slotName == "Mainhand" ? "Mainhand" : slotName == "Offhand" ? "Offhand" : null;
                if (equipSlot == null) return true;
                var item = App.ServerRuntime?.GetCosmetic(id);
                if (item == null) return true;
                var need = item.WeaponType;
                if (need == EquipType.Invalid) return true;

                var equips = data.Equips;
                if (equips == null) return false;
                for (int i = 0; i < equips.Count; i++)
                {
                    var es = equips[i];
                    if (es == null || es.Equip == null) continue;
                    if (es.Slot.ToString() != equipSlot) continue;
                    var cfg = App.ServerRuntime?.GetEquip(es.Equip.Id);
                    return cfg != null && cfg.Type == need;
                }
                return false;
            }
            catch { return true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SendCosmetic(PlayerSave save, string slotName, string id)
        {
            var slot = (CosmeticSlot)Enum.Parse(typeof(CosmeticSlot), slotName);
            save.ApplyCosmetic(id, slot);   // 已在該格時回 false 不送——外層驗收會直接判到位
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SendRemoveCosmetic(PlayerSave save, string slotName)
        {
            save.RemoveCosmetic((CosmeticSlot)Enum.Parse(typeof(CosmeticSlot), slotName));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SendAppearance(PlayerSave save, Dictionary<string, int> want)
        {
            var data = save.Data;
            // 從現況複製一份再覆蓋快照有的欄位（快照缺欄位時不要把它歸零）
            var cur = data.Appearance;
            var a = new CharacterAppearanceData();
            int Pick(string k, int fallback) => want.TryGetValue(k, out int v) ? v : fallback;
            a.BodyColor = Pick("BodyColor", cur?.BodyColor ?? 0);
            a.Hair = Pick("Hair", cur?.Hair ?? 0);
            a.HairColor = Pick("HairColor", cur?.HairColor ?? 0);
            a.Brow = Pick("Brow", cur?.Brow ?? 0);
            a.Beard = Pick("Beard", cur?.Beard ?? 0);
            a.Mouth = Pick("Mouth", cur?.Mouth ?? 0);
            a.Eye = Pick("Eye", cur?.Eye ?? 0);
            a.EyeColor = Pick("EyeColor", cur?.EyeColor ?? 0);
            a.Ears = Pick("Ears", cur?.Ears ?? 0);
            a.Iris = Pick("Iris", cur?.Iris ?? 0);

            var arche = Archetype.Novice;
            try
            {
                var list = data.Archetypes;
                if (list != null && list.Count > 0) arche = list[0];
            }
            catch { }

            var dto = CharacterAppearanceDto.Create(a, data.Name, arche);
            save.ApplyAppearance(dto, null);
        }
    }

    // =========================================================================
    //  診斷：狀態元件快照（魔導書「替換」機制追查用）
    // =========================================================================

    /// <summary>
    /// 把本地玩家 StatusComponent 裡跟「替換／調諧」有關的東西印成一行：
    ///   SkillReplacements（技能替換表）、StatusReplacements（狀態替換表）、Attunements（調諧歷史）、
    ///   Buffs、畫面上的技能／狀態顯示（SkillDisplays_C / StatusDisplays_C）、武器元素、主手、魔導書。
    /// 遊戲事實（ISIL）：`StatusComponent.SetGear` 一開始就 `SkillReplacements.Clear()`＋`ClearStatusReplacements()`
    /// （後者把替換表裡「原始＋替換」兩邊的狀態都 RemoveEffect），再從裝備／神器／魔導書的 AddStat 重新登錄；
    /// 任何狀態在 `ApplyEffectImmediate` 套用時才查 StatusReplacements 換名。所以「Wind Attunement 沒被換成
    /// Elemental Attunement」＝那個狀態被套用的當下替換表裡沒有它。這個 dump 就是要抓那一刻前後的差異。
    /// Ctrl+Shift+D 隨時可手動印一次；還原流程的關鍵節點自動印。
    /// </summary>
    internal static class Diag
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Dump(string tag)
        {
            try
            {
                var player = App.Player;
                var status = player?.Status;
                var data = player?.Save?.Data;
                if (status == null) { Plugin.Logger.LogInfo($"[Build][診斷:{tag}] 拿不到 StatusComponent"); return; }

                string Dict(Il2CppSystem.Collections.Generic.Dictionary<string, string> d)
                {
                    try
                    {
                        if (d == null) return "null";
                        var parts = new List<string>();
                        foreach (var kv in d) parts.Add($"{kv.Key}→{kv.Value}");
                        return parts.Count == 0 ? "{}" : "{" + string.Join(", ", parts) + "}";
                    }
                    catch (Exception ex) { return "(讀取失敗:" + ex.GetType().Name + ")"; }
                }
                string Keys(Il2CppSystem.Collections.Generic.Dictionary<string, StatusEffectState> d)
                {
                    try
                    {
                        if (d == null) return "null";
                        var parts = new List<string>();
                        foreach (var kv in d) parts.Add(kv.Key);
                        return parts.Count == 0 ? "{}" : "{" + string.Join(", ", parts) + "}";
                    }
                    catch (Exception ex) { return "(讀取失敗:" + ex.GetType().Name + ")"; }
                }
                string StrList(Il2CppSystem.Collections.Generic.List<string> l)
                {
                    try
                    {
                        if (l == null) return "null";
                        var parts = new List<string>();
                        for (int i = 0; i < l.Count; i++) parts.Add(l[i]);
                        return "[" + string.Join(", ", parts) + "]";
                    }
                    catch (Exception ex) { return "(讀取失敗:" + ex.GetType().Name + ")"; }
                }
                string weaponEl = "?";
                try { weaponEl = status.WeaponElement != null ? status.WeaponElement.Value.ToString() : "null"; } catch { }
                string mainhand = data != null ? (Gear.EquippedId(data, "Mainhand") ?? "空") : "?";
                string grims = "?";
                try
                {
                    var g = data?.Grimoires;
                    grims = g == null ? "null" : "[" + string.Join(", ", Enumerable.Range(0, g.Count).Select(i => g[i]?.Id)) + "]";
                }
                catch { }
                string granted = "?";
                try
                {
                    var gs = status.GrantedSkills;
                    granted = gs == null ? "null" : "[" + string.Join(", ", Enumerable.Range(0, gs.Count).Select(i => $"{gs[i]?.Id}:{gs[i]?.Level}")) + "]";
                }
                catch { }

                Plugin.Logger.LogInfo($"[Build][診斷:{tag}] 主手={mainhand} 武器元素={weaponEl} 魔導書={grims}" +
                    $"｜技能替換={Dict(status.SkillReplacements)}" +
                    $"｜狀態替換={Dict(status.StatusReplacements)}" +
                    $"｜調諧={StrList(status.Attunements)}" +
                    $"｜技能顯示={Keys(status.SkillDisplays_C)}" +
                    $"｜狀態顯示={Keys(status.StatusDisplays_C)}" +
                    $"｜賦予技能={granted}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build][診斷:{tag}] dump 失敗：{ex.Message}");
            }
        }
    }

    // =========================================================================
    //  還原狀態機：套用點法 → 配能力點 → 穿裝備 → 時裝／外觀 → 逐格綁快捷列（謂詞輪詢推進，每步有逾時）
    // =========================================================================

    internal static class Machine
    {
        private enum Step { Idle, WaitApply, AttrApply, Gear, Look, Assign }

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

        private static List<GearAction> _gear;      // 換裝佇列（補裝時會被換成重試清單）
        private static int _gearIdx;
        private static bool _gearSent;
        private static int _gearRound;
        /// <summary>
        /// 第一輪算出來要換的件數＝「跟快照有出入的件數」。回報用。
        /// 不能拿 `_gear.Count`——補裝時它會被換成重試清單，件數會少算。
        /// </summary>
        private static int _gearPlanned;
        private static readonly List<string> _gearMissing = new List<string>();
        private static bool _remounted;             // 「換裝後重掛魔導書」這次還原跑過了沒

        private static List<LookAction> _look;      // 時裝／外觀佇列
        private static int _lookIdx;
        private static bool _lookSent;
        private static int _lookPlanned;
        private static readonly List<string> _lookMissing = new List<string>();

        private const float StepTimeout = 10f;      // 套用點法／能力點單步逾時
        private const float AssignTimeout = 1.0f;   // 單格綁定驗收逾時（過了就下一格）
        private const float AssignGap = 0.15f;      // 兩格之間最小間隔（實測 23 格快捷列，別拖太久）
        private const float RetryGap = 1.0f;        // 補綁前的沉澱時間（等賦予技能重新掛回來）
        private const int MaxRetry = 2;             // 補綁輪數上限
        private const float GearTimeout = 2.0f;     // 單件換裝驗收逾時（伺服器要寫檔，給寬一點）
        private const float GearGap = 0.2f;         // 兩件換裝之間的間隔
        private const int MaxGearRetry = 2;         // 補裝輪數上限
        private const float LookTimeout = 2.0f;     // 單件時裝／外觀驗收逾時
        private const float LookGap = 0.2f;

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
            if (_use.Cosmetics && _target.Cosmetics != null) parts.Add($"{_target.Cosmetics.Count} 時裝");
            if (_use.Appearance && (_target.Appearance?.Count ?? 0) > 0) parts.Add("外觀");
            if (_use.Hotbar && _slots.Count > 0) parts.Add($"{_slots.Count} 快捷格");
            return parts.Count == 0 ? "（沒有勾選任何項目）" : string.Join("／", parts);
        }

        /// <summary>
        /// 勾了時裝／外觀但這組根本沒記過（升級前存的舊快照）——要明講，不然玩家會以為功能壞了：
        /// 「打勾只是開關，資料要那一組自己存過一次才有」是最常見的誤會（2026-08-16 實測第一個回報就是這個）。
        /// </summary>
        private static string MissingLookNote()
        {
            if (_target == null) return null;
            var m = new List<string>();
            if (_use.Cosmetics && _target.Cosmetics == null) m.Add("時裝");
            if (_use.Appearance && (_target.Appearance?.Count ?? 0) == 0) m.Add("外觀");
            if (m.Count == 0) return null;
            return $"這組沒有{string.Join("／", m)}資料（升級前存的）——右鍵這組 →「把目前的時裝／外觀存進這組」補上";
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
            _gearPlanned = 0;
            _gearMissing.Clear();
            _remounted = false;
            _look = null;
            _lookPlanned = 0;
            _lookMissing.Clear();

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

            Plugin.Logger.LogInfo($"[Build] 開始還原「{_target.Name}」：{Describe()}" +
                (MissingLookNote() is string mln ? $"；{mln}" : ""));
            Diag.Dump("還原開始前");

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
                        Diag.Dump("技能點套用後");
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

                case Step.Look:
                    TickLook(save, now);
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
            _gearMissing.Clear();          // 要在規劃**之前**清：規劃階段也會記缺件備註
            _gear = HasGearData(_target) ? BuildGearQueue(save) : new List<GearAction>();
            _gearIdx = 0;
            _gearSent = false;
            _gearRound = 0;
            _gearPlanned = _gear.Count;   // 只在這裡設，補裝輪不覆寫
            _nextAssignAt = Time.unscaledTime;   // 別沿用上一輪的節流時間

            if (_gear.Count == 0) { BeginLook(save); return; }

            _step = Step.Gear;
            _deadline = Time.unscaledTime + StepTimeout;
            UiRow.SetStatus($"還原「{_target.Name}」：換裝中…（{_gear.Count} 件）");
            // 一行／次還原，常駐印出——回報「換裝失敗」時第一個要看的就是這行
            Plugin.Logger.LogInfo("[Build] 換裝佇列：" + string.Join("、", _gear.Select(g => g.ToString())));
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
                // 飾品左右欄另外規劃（PlanAccessories），這裡只處理「一種類型一個欄位」的部位。
                var wantEquip = new Dictionary<string, PresetGear>(StringComparer.Ordinal);
                if (_use.Equips)
                    foreach (var g in _target.Equips ?? new List<PresetGear>())
                        if (!string.IsNullOrEmpty(g.Slot)) wantEquip[g.Slot] = g;

                foreach (var kv in wantEquip)
                {
                    if (Gear.IsAccessorySlot(kv.Key)) continue;
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
                            if (Gear.IsAccessorySlot(slot)) continue;
                            if (wantEquip.ContainsKey(slot)) continue;
                            q.Add(new GearAction { Kind = GearKind.Unequip, Slot = slot, Id = es.Equip.Id });
                        }
                }

                var accessory = _use.Equips ? PlanAccessories(data, wantEquip, clear) : new List<GearAction>();

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

                // 飾品動作**保持規劃出來的順序**接在最後——它的卸／穿交錯是刻意的
                //（先讓欄位再穿），不能被上面的排序打散。飾品欄與其他部位互不影響。
                q.AddRange(accessory);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 產生換裝清單失敗，跳過換裝：{ex.Message}");
                return new List<GearAction>();
            }
            return q;
        }

        /// <summary>
        /// 飾品左右欄的換裝規劃。**依遊戲 `PlayerSave.ApplyEquip` 的欄位規則模擬**，一輪就到位：
        ///
        ///   遊戲收到 ApplyEquip(飾品) 時自己挑欄位——
        ///     ・唯一（EquipConfig.Unique）：**一律塞左欄**，左欄原本的擠回背包
        ///     ・一般：左空→左；左滿右空→右；**兩欄都滿→換左欄**（右欄永遠不會被直接換掉）
        ///   而且 ApplyEquip_S 只從**背包**撈物品：已在身上的送過去會被靜默忽略。
        ///
        /// 所以「兩欄都滿時要換右邊那件」非先卸右欄不可，否則遊戲會把左欄（常常是唯一手套）擠掉，
        /// 舊版補裝重試也救不回來——每輪都在左欄來回擠。這就是「換手套失敗」的根因。
        ///
        /// 規劃原則：飾品以**集合**看待（要的兩件都在身上就算到位，不計較左右——
        /// 唯一件反正只能在左）；動作順序＝卸不要的 → 唯一件先穿 → 一般件補進空欄，
        /// 兩欄都滿就先讓出不要的那欄。動作的 Slot 欄位是預測值，驗收看集合。
        /// </summary>
        private static List<GearAction> PlanAccessories(CharacterData data,
            Dictionary<string, PresetGear> wantEquip, bool clear)
        {
            var acts = new List<GearAction>();

            var want = new List<PresetGear>();
            if (wantEquip.TryGetValue(Gear.AccL, out var wl) && !string.IsNullOrEmpty(wl.Uid)) want.Add(wl);
            if (wantEquip.TryGetValue(Gear.AccR, out var wr) && !string.IsNullOrEmpty(wr.Uid) &&
                !want.Any(w => string.Equals(w.Uid, wr.Uid, StringComparison.Ordinal))) want.Add(wr);
            var wantUids = new HashSet<string>(want.Select(w => w.Uid), StringComparer.Ordinal);

            // 模擬狀態（uid；null＝空欄）
            string simL = Gear.EquippedUid(data, Gear.AccL);
            string simR = Gear.EquippedUid(data, Gear.AccR);
            bool Wanted(string uid) => uid != null && wantUids.Contains(uid);
            bool OnBody(string uid) => uid != null &&
                (string.Equals(uid, simL, StringComparison.Ordinal) || string.Equals(uid, simR, StringComparison.Ordinal));

            // A. 卸下快照沒有的（勾了才做）
            if (clear)
            {
                if (simL != null && !Wanted(simL))
                {
                    acts.Add(new GearAction { Kind = GearKind.Unequip, Slot = Gear.AccL, Id = Gear.EquippedId(data, Gear.AccL) });
                    simL = null;
                }
                if (simR != null && !Wanted(simR))
                {
                    acts.Add(new GearAction { Kind = GearKind.Unequip, Slot = Gear.AccR, Id = Gear.EquippedId(data, Gear.AccR) });
                    simR = null;
                }
            }

            var uniques = want.Where(w => Gear.IsUnique(w.Id)).ToList();
            var normals = want.Where(w => !Gear.IsUnique(w.Id)).ToList();

            // B. 唯一件先穿：遊戲一律放左欄、左欄原本的擠回背包（若那件也是要的，C 步會補回右欄）
            bool uniquePlaced = uniques.Any(u => OnBody(u.Uid));
            foreach (var u in uniques)
            {
                if (OnBody(u.Uid)) continue;
                if (uniquePlaced)
                {
                    // 兩件唯一飾品同時上身走 ApplyEquip 是做不到的（都只進左欄、互相擠掉）
                    _gearMissing.Add($"{u.Id}(唯一飾品只能佔左欄，已被另一件唯一飾品佔用)");
                    continue;
                }
                acts.Add(new GearAction { Kind = GearKind.Equip, Slot = Gear.AccL, Uid = u.Uid, Id = u.Id });
                simL = u.Uid;
                uniquePlaced = true;
            }

            // C. 一般件：左空→左、左滿右空→右；兩欄都滿→先讓出「不要的那欄」再穿
            //   （讓左則進左、讓右則進右——正好落在讓出來的那格）
            foreach (var n in normals)
            {
                if (OnBody(n.Uid)) continue;
                if (simL == null)
                {
                    acts.Add(new GearAction { Kind = GearKind.Equip, Slot = Gear.AccL, Uid = n.Uid, Id = n.Id });
                    simL = n.Uid;
                    continue;
                }
                if (simR == null)
                {
                    acts.Add(new GearAction { Kind = GearKind.Equip, Slot = Gear.AccR, Uid = n.Uid, Id = n.Id });
                    simR = n.Uid;
                    continue;
                }
                // 兩欄都滿：都不要就讓左欄（跟遊戲預設一致）
                string free = !Wanted(simR) ? Gear.AccR : (!Wanted(simL) ? Gear.AccL : null);
                if (free == null)
                {
                    _gearMissing.Add($"{n.Id}(飾品欄已滿)");   // 要的超過兩件？理論上不會發生
                    continue;
                }
                acts.Add(new GearAction { Kind = GearKind.Unequip, Slot = free, Id = Gear.EquippedId(data, free) });
                acts.Add(new GearAction { Kind = GearKind.Equip, Slot = free, Uid = n.Uid, Id = n.Id });
                if (free == Gear.AccL) simL = n.Uid; else simR = n.Uid;
            }

            if (acts.Count > 0 || want.Count > 0)
            {
                Plugin.Logger.LogInfo("[Build] 飾品規劃：現況 " + Gear.AccState(data) +
                    " → 目標 {" + string.Join("、", want.Select(w => w.Id + (Gear.IsUnique(w.Id) ? "(唯一)" : ""))) + "}" +
                    (acts.Count == 0 ? " → 已到位" : " → 動作 " + string.Join("→", acts.Select(a => a.ToString()))));
            }
            return acts;
        }

        /// <summary>身上每本魔導書 → 卸下、裝回 兩個動作（跟玩家手動拔掉重裝一模一樣）。</summary>
        private static List<GearAction> BuildRemountQueue(PlayerSave save)
        {
            var q = new List<GearAction>();
            try
            {
                var g = save.Data.Grimoires;
                if (g == null) return q;
                for (int i = 0; i < g.Count; i++)
                {
                    string uid = Gear.InstanceId(g[i]);
                    if (string.IsNullOrEmpty(uid)) continue;
                    q.Add(new GearAction { Kind = GearKind.RemoveGrimoire, Uid = uid, Id = g[i].Id });
                    q.Add(new GearAction { Kind = GearKind.Grimoire, Uid = uid, Id = g[i].Id });
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 產生重掛魔導書清單失敗：{ex.Message}");
                q.Clear();
            }
            return q;
        }

        private static void TickGear(PlayerSave save, float now)
        {
            if (_gearIdx >= _gear.Count)
            {
                // 補裝：重新比對現況，沒到位的再送一輪。
                // 飾品欄位規則已在 PlanAccessories 依遊戲邏輯模擬，正常一輪到位；
                // 這裡的重試留給伺服器偶發沒回應、或規劃猜錯（例如唯一設定查不到）的收斂。
                if (_gearRound < MaxGearRetry)
                {
                    // 重新規劃前先清缺件清單：規劃階段本身會記備註；重新比對後全到位＝上一輪的
                    // 「逾時」其實後來有落地，不該還掛在報告上
                    _gearMissing.Clear();
                    var again = BuildGearQueue(save);
                    if (again.Count > 0)
                    {
                        _gearRound++;
                        _gear = again;
                        _gearIdx = 0;
                        _gearSent = false;
                        _nextAssignAt = now + RetryGap;
                        Plugin.Logger.LogInfo($"[Build] 第 {_gearRound} 次補裝：{again.Count} 件未到位（" +
                            string.Join("、", again.Take(4).Select(a => a.ToString())) + "）；飾品現況 " +
                            Gear.AccState(save.Data));
                        UiRow.SetStatus($"補裝 {again.Count} 件…");
                        return;
                    }
                }

                if (_gearMissing.Count > 0)
                    Plugin.Logger.LogWarning($"[Build] {_gearMissing.Count} 件裝備沒還原：{string.Join("、", _gearMissing)}" +
                        $"；飾品現況 {Gear.AccState(save.Data)}");
                Diag.Dump("換裝階段結束");

                // 實驗：把魔導書逐本卸下再裝回（＝玩家手動拔掉重裝），只跑一次
                if (Plugin.CfgRemountGrimoires.Value && !_remounted)
                {
                    _remounted = true;
                    var remount = BuildRemountQueue(save);
                    if (remount.Count > 0)
                    {
                        _gear = remount;
                        _gearIdx = 0;
                        _gearSent = false;
                        _gearRound = MaxGearRetry;      // 這輪跑完不要再補裝重試
                        _nextAssignAt = now + GearGap;
                        Plugin.Logger.LogInfo("[Build] 重掛魔導書：" + string.Join("、", remount.Select(a => a.ToString())));
                        UiRow.SetStatus($"重掛魔導書 {remount.Count / 2} 本…");
                        return;
                    }
                }
                BeginLook(save);
                return;
            }

            var act = _gear[_gearIdx];

            if (!_gearSent)
            {
                if (now < _nextAssignAt) return;

                // 送出前先驗：前一件的連鎖效應（例如唯一飾品把左欄擠回背包、或已在身上）
                // 可能讓這件已經到位——不送就不會有「已在身上→伺服器忽略→逾時」的假失敗，也少一次 RPC
                if (GearMatches(save, act))
                {
                    if (Plugin.CfgDiagnostic.Value)
                        Plugin.Logger.LogInfo($"[Build][診斷] 換裝已到位，略過：{act}");
                    NextGear(now);
                    return;
                }

                bool sent = SendGear(save, act);
                if (!sent) { NextGear(now); return; }   // 缺件／不能穿：已記錄，直接下一件
                _gearSent = true;
                _deadline = now + GearTimeout;
                UiRow.SetStatus($"還原「{_target.Name}」：換裝 {_gearIdx + 1}/{_gear.Count}（{act}）…");
                return;
            }

            if (GearMatches(save, act))
            {
                if (Plugin.CfgDiagnostic.Value)
                {
                    Plugin.Logger.LogInfo($"[Build][診斷] 換裝到位：{act}；飾品現況 {Gear.AccState(save.Data)}");
                    Diag.Dump($"換裝到位 {act}");
                }
                NextGear(now);
            }
            else if (now > _deadline)
            {
                _gearMissing.Add($"{act}(逾時)");
                // 常駐印：逾時是「伺服器沒接受／靜默忽略」的訊號，要連當下狀態一起留下來
                string where = act.Uid != null ? $"；物品位置={Gear.Locate(save.Data, act.Uid)}" : "";
                Plugin.Logger.LogWarning($"[Build] 換裝逾時：{act}{where}；飾品現況 {Gear.AccState(save.Data)}");
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
                        // 飾品看集合：在左或右都算到位（欄位是遊戲挑的，唯一件永遠在左；
                        // 硬要對左右只會逼出「已在身上→再送→被忽略→逾時」的假失敗）
                        if (Gear.IsAccessorySlot(act.Slot))
                            return string.Equals(Gear.EquippedUid(data, Gear.AccL), act.Uid, StringComparison.Ordinal) ||
                                   string.Equals(Gear.EquippedUid(data, Gear.AccR), act.Uid, StringComparison.Ordinal);
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

        // ---- 時裝／外觀階段：換裝之後（武器外觀要對手上的武器）、綁快捷列之前 ----

        private static void BeginLook(PlayerSave save)
        {
            _lookMissing.Clear();
            _look = BuildLookQueue(save);
            _lookIdx = 0;
            _lookSent = false;
            _lookPlanned = _look.Count;
            _nextAssignAt = Time.unscaledTime;

            if (_look.Count == 0) { BeginAssign(); return; }

            _step = Step.Look;
            _deadline = Time.unscaledTime + StepTimeout;
            UiRow.SetStatus($"還原「{_target.Name}」：套用時裝／外觀…（{_look.Count} 項）");
            Plugin.Logger.LogInfo("[Build] 時裝／外觀佇列：" + string.Join("、", _look.Select(a => a.ToString())));
        }

        /// <summary>
        /// 只送「現況與快照不符」的格子。時裝欄之間互不影響（一格一件、伺服器 RemoveAll(slot) 再加），
        /// 不需要像飾品那樣模擬順序。舊快照沒記過（null）整段跳過——跟裝備同一條紅線。
        /// </summary>
        private static List<LookAction> BuildLookQueue(PlayerSave save)
        {
            var q = new List<LookAction>();
            try
            {
                var data = save.Data;

                if (_use.Cosmetics && _target.Cosmetics != null)
                {
                    var want = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var c in _target.Cosmetics)
                        if (c != null && !string.IsNullOrEmpty(c.Slot) && !string.IsNullOrEmpty(c.Id)) want[c.Slot] = c.Id;
                    var cur = Look.CurrentCosmetics(data);

                    if (_use.ClearUnlisted)
                        foreach (var kv in cur)
                            if (!want.ContainsKey(kv.Key))
                                q.Add(new LookAction { Kind = LookKind.RemoveCosmetic, Slot = kv.Key, Id = kv.Value });

                    foreach (var kv in want)
                    {
                        if (cur.TryGetValue(kv.Key, out var have) && string.Equals(have, kv.Value, StringComparison.Ordinal)) continue;
                        q.Add(new LookAction { Kind = LookKind.Cosmetic, Slot = kv.Key, Id = kv.Value });
                    }
                }

                if (_use.Appearance && (_target.Appearance?.Count ?? 0) > 0 &&
                    !Look.AppearanceMatches(_target.Appearance, data))
                    q.Add(new LookAction { Kind = LookKind.Appearance });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 產生時裝／外觀清單失敗，跳過：{ex.Message}");
                return new List<LookAction>();
            }
            return q;
        }

        private static void TickLook(PlayerSave save, float now)
        {
            if (_lookIdx >= _look.Count)
            {
                if (_lookMissing.Count > 0)
                    Plugin.Logger.LogWarning($"[Build] {_lookMissing.Count} 項時裝／外觀沒還原：{string.Join("、", _lookMissing)}");
                BeginAssign();
                return;
            }

            var act = _look[_lookIdx];

            if (!_lookSent)
            {
                if (now < _nextAssignAt) return;
                if (LookMatches(save, act)) { NextLook(now); return; }
                bool sent = SendLook(save, act);
                if (!sent) { NextLook(now); return; }
                _lookSent = true;
                _deadline = now + LookTimeout;
                UiRow.SetStatus($"還原「{_target.Name}」：時裝／外觀 {_lookIdx + 1}/{_look.Count}（{act}）…");
                return;
            }

            if (LookMatches(save, act))
            {
                if (Plugin.CfgDiagnostic.Value) Plugin.Logger.LogInfo($"[Build][診斷] 時裝／外觀到位：{act}");
                NextLook(now);
            }
            else if (now > _deadline)
            {
                _lookMissing.Add($"{act}(逾時)");
                Plugin.Logger.LogWarning($"[Build] 時裝／外觀逾時：{act}" +
                    (act.Kind == LookKind.Appearance
                        ? $"；目標 {Look.DescribeAppearance(_target.Appearance)}；現況 {Look.DescribeAppearance(SafeReadAppearance(save))}"
                        : ""));
                NextLook(now);
            }
        }

        private static void NextLook(float now)
        {
            _lookIdx++;
            _lookSent = false;
            _nextAssignAt = now + LookGap;
        }

        private static Dictionary<string, int> SafeReadAppearance(PlayerSave save)
        {
            try { return Look.ReadAppearance(save.Data); } catch { return null; }
        }

        private static bool LookMatches(PlayerSave save, LookAction act)
        {
            try
            {
                var data = save.Data;
                switch (act.Kind)
                {
                    case LookKind.Cosmetic:
                        return Look.CurrentCosmetics(data).TryGetValue(act.Slot, out var have) &&
                               string.Equals(have, act.Id, StringComparison.Ordinal);
                    case LookKind.RemoveCosmetic:
                        return !Look.CurrentCosmetics(data).ContainsKey(act.Slot);
                    case LookKind.Appearance:
                        return Look.AppearanceMatches(_target.Appearance, data);
                }
            }
            catch { }
            return false;
        }

        /// <summary>回傳 false＝送不出去（不在衣櫃／已不存在／武器外觀不相容），已記進 _lookMissing。</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SendLook(PlayerSave save, LookAction act)
        {
            try
            {
                switch (act.Kind)
                {
                    case LookKind.Cosmetic:
                        if (!Look.CosmeticExists(act.Id)) { _lookMissing.Add($"{act.Id}(已不存在)"); return false; }
                        if (!Look.InWardrobe(save, act.Id)) { _lookMissing.Add($"{act.Id}(不在衣櫃)"); return false; }
                        if (!Look.WeaponSkinCompatible(save.Data, act.Slot, act.Id))
                        {
                            _lookMissing.Add($"{act.Id}(武器類型不符)");
                            return false;
                        }
                        Look.SendCosmetic(save, act.Slot, act.Id);
                        return true;
                    case LookKind.RemoveCosmetic:
                        Look.SendRemoveCosmetic(save, act.Slot);
                        return true;
                    case LookKind.Appearance:
                        Look.SendAppearance(save, _target.Appearance);
                        return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 時裝／外觀送出失敗（{act}）：{ex.Message}");
                _lookMissing.Add($"{act}(錯誤)");
            }
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
            string lookNote = _lookMissing.Count > 0
                ? $"　時裝／外觀未還原：{string.Join("、", _lookMissing)}" : "";
            string attrNote = _skippedAttr != null ? $"　{_skippedAttr}" : "";

            // 換到位的件數＝第一輪算出的差異件數 − 最後仍缺的件數。
            // 有缺漏時也要報——只講「缺了什麼」不講「做成了什麼」會讓人以為根本沒換裝。
            int gearOk = Math.Max(0, _gearPlanned - _gearMissing.Count);
            string gearDone = gearOk > 0 ? $"／換裝 {gearOk} 件" : "";
            int lookOk = Math.Max(0, _lookPlanned - _lookMissing.Count);
            if (lookOk > 0) gearDone += $"／時裝外觀 {lookOk} 項";
            gearNote += lookNote;
            // 勾了時裝／外觀但這組沒資料：不是失敗，但一定要講，否則玩家以為功能沒動
            string lookHint = MissingLookNote() is string h ? $"　{h}" : "";

            if ((failed == null || failed.Count == 0) && _gearMissing.Count == 0 && _lookMissing.Count == 0 && _skippedAttr == null)
            {
                Plugin.Logger.LogInfo($"[Build] 還原完成：「{_target.Name}」{_target.Skills.Count} 技能／{total} 快捷格{gearDone}{lookHint}");
                UiRow.SetStatus($"還原完成：「{_target.Name}」（{_target.Skills.Count} 技能／{total} 快捷格{gearDone}）{lookHint}",
                    lookHint.Length > 0);
            }
            else
            {
                string list = failed == null || failed.Count == 0
                    ? "" : "未綁上：" + string.Join("、", failed.Select(f => $"#{f.Slot + 1}={f.Id}"));
                Plugin.Logger.LogWarning($"[Build] 還原完成：{_target.Skills.Count} 技能／快捷 {ok}/{total}{gearDone}。" +
                    $"有缺漏：{list}{gearNote}{attrNote}{lookHint}");
                UiRow.SetStatus($"還原完成：{_target.Skills.Count} 技能／快捷 {ok}/{total}{gearDone}。" +
                    $"{list}{gearNote}{attrNote}{lookHint}", true);
            }

            Diag.Dump("還原完成");
            Core.PostDumpAt = Time.unscaledTime + 2f;

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
                        // ApplyEquip_S 伺服器端只從背包撈：已穿在身上（別的欄位）的送過去會被靜默忽略。
                        // 監控用：印出物品實際位置，逾時時對得上原因。
                        string where = Gear.Locate(data, act.Uid);
                        if (Plugin.CfgDiagnostic.Value || where != "背包")
                            Plugin.Logger.LogInfo($"[Build] 送出換裝：{act}；物品位置={where}；飾品現況 {Gear.AccState(data)}");
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
        private const string SaveLookName = "SBSaveLook";
        private static float _deleteConfirmUntil;
        private static TextMeshProUGUI _deleteLabel;
        private static TextMeshProUGUI _saveLookLabel;
        private static string _builtUid;
        private static GameObject _panel;
        private static readonly List<TextMeshProUGUI> _chkLabels = new List<TextMeshProUGUI>();
        private static readonly List<Image> _chkBgs = new List<Image>();
        private static TextMeshProUGUI _panelTitle;
        private static int _editIdx = -1;
        private static PresetFlags _editFlags;

        /// <summary>面板上的勾選項，順序＝畫面由上到下。最後一項固定是「卸下快照沒有的」。</summary>
        private static readonly string[] ChkText =
            { "技能點", "快捷列", "能力點", "裝備", "神器", "魔導書",
              "時裝（含坐騎／寵物）", "外觀（長相）", "卸下快照沒有的裝備／時裝欄" };

        private static bool GetFlag(PresetFlags f, int i) => i switch
        {
            0 => f.Skills,
            1 => f.Hotbar,
            2 => f.Attributes,
            3 => f.Equips,
            4 => f.Artifacts,
            5 => f.Grimoires,
            6 => f.Cosmetics,
            7 => f.Appearance,
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
                case 6: f.Cosmetics = !f.Cosmetics; break;
                case 7: f.Appearance = !f.Appearance; break;
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
            // 編輯的是副本，按取消就原封不動
            _editFlags = Store.GetFlags(uid, index).Clone();
            _editIdx = index;

            try
            {
                var p = Store.Get(uid, index);
                string name = Store.GetName(uid, index);
                if (_panelTitle != null)
                    _panelTitle.text = $"編輯第 {index + 1} 組" +
                        (p == null ? "（尚未存入配置）" :
                            $"　{p.Skills.Count} 技能／{p.Equips.Count} 裝備" +
                            (p.Cosmetics != null ? $"／{p.Cosmetics.Count} 時裝" : "／舊快照無時裝外觀"));
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
                if (hit == SaveLookName) { ClickSaveLook(save); return; }
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

        /// <summary>
        /// 只把「目前身上的時裝／外觀」寫進這組（技能／裝備／能力點／快捷列原封不動），
        /// 並順手把這組的「時裝」「外觀」勾起來——會按這顆的人就是要它跟著還原。
        /// 面板不關，讓玩家看得到勾選狀態變了。
        /// </summary>
        private static void ClickSaveLook(PlayerSave save)
        {
            int idx = _editIdx;
            string uid = Core.SafeUid(save);
            if (save == null || idx < 0 || uid == null) return;

            if (Store.Get(uid, idx) == null)
            {
                SetStatus("這組還沒存過配置，先 Shift+左鍵存一次整組。", true);
                return;
            }

            var tmp = new Preset();
            try { Look.Snapshot(save.Data, tmp); }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Build] 讀取目前時裝／外觀失敗：{ex.Message}");
                SetStatus("讀不到目前的時裝／外觀，詳見 log。", true);
                return;
            }
            if (tmp.Cosmetics == null && tmp.Appearance == null)
            {
                SetStatus("讀不到目前的時裝／外觀，沒有存入。", true);
                return;
            }

            _editFlags.Cosmetics = true;
            _editFlags.Appearance = true;
            Store.PutLook(uid, idx, tmp.Cosmetics, tmp.Appearance);
            DrawPanel();
            Refresh();
            string name = Store.GetName(uid, idx) ?? $"Build {idx + 1}";
            SetStatus($"已把目前的 {tmp.Cosmetics?.Count ?? 0} 時裝{(tmp.Appearance != null ? "／外觀" : "")} 存進「{name}」" +
                "（其他不動），並勾選時裝／外觀。");
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
            SetStatus($"第 {idx + 1} 組已更新：勾選 {on}/{ChkText.Length - 1} 類。");
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
                    bool partial = !(u.Skills && u.Hotbar && u.Attributes && u.Equips && u.Artifacts && u.Grimoires &&
                                     u.Cosmetics && u.Appearance);
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
                float panelH = Pad + 26f + 6f + BtnH + 8f + ChkText.Length * RowH + 10f + 32f + 8f + 32f + Pad;

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
                // 「只把目前的打扮存進這組」：升級後舊 Build 沒有時裝／外觀資料，要補得先還原那組、
                // 穿好、再整組重存——太繞。這顆只更新時裝／外觀兩塊，技能／裝備／能力點原封不動。
                _saveLookLabel = MakeButton(panel.transform, SaveLookName, "把目前的時裝／外觀存進這組（其他不動）",
                    font, new Vector2(Pad, y), new Vector2(PanelW - Pad * 2, 32f));
                _saveLookLabel.fontSize = 16f;
                y -= 32f + 8f;

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
                // 往上找幾層是為了保險（例如 TMP_InputField 的內部子物件）。
                // 每個可點的名字都要列在這裡——v1.0.1 漏了 DeleteName，刪除鈕的點擊
                // 一路往上找到 SkillBuildsPanel 就回傳面板名，TickPanel 永遠對不上「刪除」。
                var t = go.transform;
                for (int d = 0; d < 3 && t != null; d++, t = t.parent)
                {
                    string n = t.name;
                    if (n.StartsWith(BtnPrefix, StringComparison.Ordinal) ||
                        n.StartsWith(ChkPrefix, StringComparison.Ordinal) ||
                        n == OkName || n == CancelName || n == DeleteName || n == SaveLookName ||
                        n == "SkillBuildsPanel")
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
