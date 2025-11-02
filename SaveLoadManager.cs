using System.IO;
using System.Linq;
using UnityEngine;

public class SaveLoadManager : MonoBehaviour
{
    [Header("Refs")]
    public BuildPlacement placement;
    public DroneBuildManager droneManager;

    [Header("Defs (optional)")]
    [Tooltip("ここにBuildingDefを並べておくとResourcesを使わずにロードできます")]
    public BuildingDef[] knownDefs;   // ← ここに入れておくと安全

    [Header("File")]
    public string fileName = "save.json";

    string SavePath => Path.Combine(Application.persistentDataPath, fileName);

    void Awake()
    {
        if (!placement) placement = FindFirstObjectByType<BuildPlacement>();
        if (!droneManager) droneManager = FindFirstObjectByType<DroneBuildManager>();
    }

    // ================== Save ==================
    public void Save()
    {
        if (!placement || !droneManager)
        {
            Debug.LogWarning("[SaveLoadManager] refs not set.");
            return;
        }

        var data = new SaveData();

        // 全体
        data.baseBuilt = BuildPlacement.s_baseBuilt;

        // 建物
        var placed = placement.CollectForSave();
        foreach (var b in placed)
        {
            data.buildings.Add(new PlacedBuildingData
            {
                defName = b.defName,     // ← ここはPrefab名が入ってるはず
                position = b.position,
                fine = b.isFine,
                isBase = b.isBase
            });
        }

        // ドローン
        data.queuedTasks = droneManager.GetQueuedTasksForSave();
        data.drones = droneManager.GetRuntimeForSave();

        // 書き出し
        var json = JsonUtility.ToJson(data, true);
        File.WriteAllText(SavePath, json);
        Debug.Log("[SaveLoadManager] Saved to: " + SavePath);
    }

    // ================== Load ==================
    public void Load()
    {
        if (!File.Exists(SavePath))
        {
            Debug.LogWarning("[SaveLoadManager] no save file.");
            return;
        }
        if (!placement || !droneManager)
        {
            Debug.LogWarning("[SaveLoadManager] refs not set.");
            return;
        }

        string json = File.ReadAllText(SavePath);
        var data = JsonUtility.FromJson<SaveData>(json);
        if (data == null)
        {
            Debug.LogWarning("[SaveLoadManager] load failed (json null).");
            return;
        }

        // いったん全部消す
        placement.ClearAllPlaced();

        // 1) 使えるDefの一覧を用意
        BuildingDef[] allDefs = knownDefs != null && knownDefs.Length > 0
            ? knownDefs
            : Resources.LoadAll<BuildingDef>("");  // ← ここが空だと何も復元できない

        if (allDefs == null || allDefs.Length == 0)
        {
            Debug.LogWarning("[SaveLoadManager] no BuildingDef found (assign to knownDefs or put in Resources)");
        }

        // defを探す関数を用意
        BuildingDef FindDef(string name)
        {
            if (string.IsNullOrEmpty(name) || allDefs == null) return null;

            // ① displayName で探す
            var d1 = allDefs.FirstOrDefault(d => d && d.displayName == name);
            if (d1 != null) return d1;

            // ② prefab.name で探す
            var d2 = allDefs.FirstOrDefault(d => d && d.prefab != null && d.prefab.name == name);
            if (d2 != null) return d2;

            // ③ ScriptableObject 自体の name で探す
            var d3 = allDefs.FirstOrDefault(d => d && d.name == name);
            if (d3 != null) return d3;

            Debug.LogWarning("[SaveLoadManager] BuildingDef not found for name: " + name);
            return null;
        }

        // 2) 建物を復元
        foreach (var b in data.buildings)
        {
            var def = FindDef(b.defName);
            if (def == null)
            {
                // 見つからなかったのでスキップ
                continue;
            }

            placement.RestoreBuilding(def, b.position, b.fine, b.isBase);
            Debug.Log($"[SaveLoadManager] Restore building: {b.defName} at {b.position} (fine={b.fine}, base={b.isBase})");
        }

        // 3) Baseフラグ
        BuildPlacement.s_baseBuilt = data.baseBuilt;

        // 4) ドローン
        droneManager.RestoreFromSave(
            data.queuedTasks,
            data.drones,
            placement,
            FindDef
        );

        Debug.Log("[SaveLoadManager] Loaded.");
    }
}
