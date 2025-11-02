using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

[DefaultExecutionOrder(-200)]
public class SaveLoadManager : MonoBehaviour
{
    public static SaveLoadManager Instance { get; private set; }

    [Header("Game References")]
    public BuildPlacement placement;
    public DroneBuildManager droneManager;
    public BuildingDef[] knownDefs;

    const string PLAYERPREFS_KEY_LASTSLOT = "LastSaveSlot";

    string currentSlot = "";
    string pendingLoadSlot = null;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // 前回のスロットを復元
        string last = PlayerPrefs.GetString(PLAYERPREFS_KEY_LASTSLOT, "");
        currentSlot = string.IsNullOrEmpty(last) ? "" : last;
    }

    // =====================================================
    // タイトルから既存スロットをロードしてスタート
    // =====================================================
    public void StartGameAndLoad(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            slot = "save";

        pendingLoadSlot = slot.Trim();
        SetSlot(pendingLoadSlot);

        var startUI = FindFirstObjectByType<StartMenuUI>();
        if (startUI != null)
        {
            if (startUI.startPanel) startUI.startPanel.SetActive(false);
            if (startUI.gameRoot) startUI.gameRoot.SetActive(true);
        }

        StartCoroutine(LoadAfterOneFrame(pendingLoadSlot));
    }

    IEnumerator LoadAfterOneFrame(string slot)
    {
        yield return null;

        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();

        LoadFrom(slot);
        pendingLoadSlot = null;
    }

    // =====================================================
    // 🔥 新しく始める
    // =====================================================
    public void StartNewGame()
    {
        // 自動で空いてるスロット名を作成
        string newSlot = GenerateNewSlotName();
        SetSlot(newSlot);

        var startUI = FindFirstObjectByType<StartMenuUI>();
        if (startUI != null)
        {
            if (startUI.startPanel) startUI.startPanel.SetActive(false);
            if (startUI.gameRoot) startUI.gameRoot.SetActive(true);
        }

        StartCoroutine(NewGameAfterOneFrame(newSlot));
    }

    IEnumerator NewGameAfterOneFrame(string slot)
    {
        yield return null;

        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();

        CreateEmptyWorldState();
        SaveTo(slot);
    }

    // =====================================================
    // 空のワールドを初期化
    // =====================================================
    void CreateEmptyWorldState()
    {
        if (!EnsureGameRefs()) return;

        placement.ClearAllPlaced();
        BuildPlacement.s_baseBuilt = false;

        if (droneManager != null)
        {
            droneManager.RestoreFromSave(
                new System.Collections.Generic.List<DroneTaskData>(),
                new System.Collections.Generic.List<DroneRuntimeData>(),
                placement,
                _ => null
            );
        }
    }

    // =====================================================
    // スロット管理
    // =====================================================
    public void SetSlot(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            return;

        currentSlot = slotName.Trim();
        PlayerPrefs.SetString(PLAYERPREFS_KEY_LASTSLOT, currentSlot);
        PlayerPrefs.Save();
        Debug.Log($"[SaveLoadManager] Slot changed to: {currentSlot}");
    }

    // =====================================================
    // 通常のセーブ・ロード
    // =====================================================
    public void Save() => SaveTo(currentSlot);
    public void Load() => LoadFrom(currentSlot);

    public void SaveTo(string slotName)
    {
        if (!EnsureGameRefs()) return;

        string path = MakePath(slotName);
        var data = new SaveData();

        data.baseBuilt = BuildPlacement.s_baseBuilt;

        var placed = placement.CollectForSave();
        foreach (var b in placed)
        {
            data.buildings.Add(new PlacedBuildingData
            {
                defName = b.defName,
                position = b.position,
                fine = b.isFine,
                isBase = b.isBase
            });
        }

        data.queuedTasks = droneManager.GetQueuedTasksForSave();
        data.drones = droneManager.GetRuntimeForSave();

        var json = JsonUtility.ToJson(data, true);
        File.WriteAllText(path, json);
        Debug.Log($"[SaveLoadManager] Saved to: {path}");

        SetSlot(slotName);
    }

    public void LoadFrom(string slotName)
    {
        if (!EnsureGameRefs()) return;

        string path = MakePath(slotName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[SaveLoadManager] No save file: {path}");
            return;
        }

        string json = File.ReadAllText(path);
        var data = JsonUtility.FromJson<SaveData>(json);
        if (data == null)
        {
            Debug.LogWarning($"[SaveLoadManager] Load failed (json null) from: {path}");
            return;
        }

        placement.ClearAllPlaced();

        BuildingDef[] allDefs = (knownDefs != null && knownDefs.Length > 0)
            ? knownDefs
            : Resources.LoadAll<BuildingDef>("");

        BuildingDef FindDef(string name)
        {
            if (string.IsNullOrEmpty(name) || allDefs == null) return null;

            var d1 = allDefs.FirstOrDefault(d => d && d.displayName == name);
            if (d1 != null) return d1;

            var d2 = allDefs.FirstOrDefault(d => d && d.prefab != null && d.prefab.name == name);
            if (d2 != null) return d2;

            var d3 = allDefs.FirstOrDefault(d => d && d.name == name);
            if (d3 != null) return d3;

            Debug.LogWarning($"[SaveLoadManager] BuildingDef not found: {name}");
            return null;
        }

        foreach (var b in data.buildings)
        {
            var def = FindDef(b.defName);
            if (def == null) continue;
            placement.RestoreBuilding(def, b.position, b.fine, b.isBase);
        }

        BuildPlacement.s_baseBuilt = data.baseBuilt;

        droneManager.RestoreFromSave(
            data.queuedTasks,
            data.drones,
            placement,
            FindDef
        );

        Debug.Log($"[SaveLoadManager] Loaded from: {path}");
        SetSlot(slotName);
    }

    // =====================================================
    // 保存済みファイル一覧（タイトルUI用）
    // =====================================================
    public string[] ListSaveFiles()
    {
        string dir = Application.persistentDataPath;
        if (!Directory.Exists(dir)) return new string[0];

        var files = Directory.GetFiles(dir, "*.json");
        for (int i = 0; i < files.Length; i++)
            files[i] = Path.GetFileName(files[i]);
        return files;
    }

    // =======================
    // セーブを1件だけ消す
    // =======================
    public void DeleteSave(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            return;

        // "save_012" → パスに変換
        string path = MakePath(slotName);
        if (File.Exists(path))
        {
            File.Delete(path);
            Debug.Log("[SaveLoadManager] deleted: " + path);
        }

        // もし消したのが今のスロットなら記憶も消す
        string last = PlayerPrefs.GetString(PLAYERPREFS_KEY_LASTSLOT, "");
        if (last == slotName)
        {
            PlayerPrefs.DeleteKey(PLAYERPREFS_KEY_LASTSLOT);
            PlayerPrefs.Save();
        }
    }

    // =======================
    // 全部消す（工場出荷）
    // =======================
    public void DeleteAllSaves()
    {
        string dir = Application.persistentDataPath;
        if (!Directory.Exists(dir)) return;

        var files = Directory.GetFiles(dir, "*.json");
        foreach (var f in files)
        {
            File.Delete(f);
            Debug.Log("[SaveLoadManager] deleted: " + f);
        }

        // 記憶してるスロットも忘れる
        PlayerPrefs.DeleteKey(PLAYERPREFS_KEY_LASTSLOT);
        PlayerPrefs.Save();
    }


    // =====================================================
    // 内部ヘルパー
    // =====================================================
    string MakePath(string slotName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            slotName = "save";
        if (!slotName.EndsWith(".json"))
            slotName += ".json";
        return Path.Combine(Application.persistentDataPath, slotName);
    }

    bool EnsureGameRefs()
    {
        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();

        if (!placement || !droneManager)
        {
            Debug.LogWarning("[SaveLoadManager] game refs not found yet.");
            return false;
        }
        return true;
    }

    string GenerateNewSlotName()
    {
        var files = ListSaveFiles();
        int idx = 1;
        if (!File.Exists(MakePath("save")))
            return "save";

        while (true)
        {
            string candidate = $"save_{idx:000}";
            if (!File.Exists(MakePath(candidate)))
                return candidate;
            idx++;
        }
    }
}
