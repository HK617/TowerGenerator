using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using System.Linq;   // ← もし Where を使うなら

public class EnemySpawnerRaid : MonoBehaviour
{
    [Header("Scene References")]
    public Grid grid;
    public Tilemap groundTilemap;

    [Header("Who to spawn")]
    public GameObject enemyPrefab;

    [Header("Where to spawn")]
    public LayerMask placeableLayerMask;
    [Min(1)] public int minDistanceInTiles = 6;
    [Min(1)] public int sitesPerRaid = 4;
    [Min(1)] public int enemiesPerSite = 3;
    public float jitterWorld = 0.20f;
    public int maxAttemptsPerSite = 60;
    public float minEnemySeparationWorld = 0.8f;

    [Header("Rules")]
    public bool requireGroundTile = true;
    public bool useHexLikeCellDistance = true;

    [Header("Trigger")]
    public bool raid = false;

    [Header("Auto Raid Timer")]
    public float raidIntervalSeconds = 60f;
    public float initialRaidDelaySeconds = 60f;
    public bool autoRaidEnabled = true;

    [Header("Base condition")]
    [Tooltip("Baseが建つまでRaidを始めない")]
    public bool requireBaseBeforeRaid = true;

    bool _raidConsumed = false;
    readonly List<Vector3> _spawnedPositions = new();

    void Reset()
    {
        if (!grid) grid = FindObjectOfType<Grid>();
    }

    void OnEnable()
    {
        ScheduleAutoRaid();
    }

    void Start()
    {
        ScheduleAutoRaid();
    }

    void OnDisable()
    {
        CancelInvoke(nameof(TriggerRaid));
    }

    void ScheduleAutoRaid()
    {
        CancelInvoke(nameof(TriggerRaid));
        if (autoRaidEnabled && raidIntervalSeconds > 0f)
        {
            float first = Mathf.Max(0f, initialRaidDelaySeconds);
            InvokeRepeating(nameof(TriggerRaid), first, raidIntervalSeconds);
        }
    }

    void Update()
    {
        if (!enemyPrefab || !grid) return;

        // ★Baseがまだなら何もしない
        if (requireBaseBeforeRaid && !BuildPlacement.s_baseBuilt)
            return;

        if (raid && !_raidConsumed)
        {
            _raidConsumed = true;
            SpawnForRaid();
            raid = false;
        }
        else if (!raid)
        {
            _raidConsumed = false;
        }

        // オートレイドのON/OFFの反映
        if (!IsInvoking(nameof(TriggerRaid)) && autoRaidEnabled && raidIntervalSeconds > 0f)
        {
            InvokeRepeating(nameof(TriggerRaid),
                Mathf.Max(0f, initialRaidDelaySeconds), raidIntervalSeconds);
        }
        else if (IsInvoking(nameof(TriggerRaid)) && (!autoRaidEnabled || raidIntervalSeconds <= 0f))
        {
            CancelInvoke(nameof(TriggerRaid));
        }
    }

    void SpawnForRaid()
    {
        _spawnedPositions.Clear();

        var placeables = CollectPlaceables();

        if (placeables.Count == 0)
        {
            Debug.LogWarning("[EnemySpawnerRaid] Placeable レイヤーの対象が見つかりません。");
            return;
        }

        int madeSites = 0;
        int safety = sitesPerRaid * maxAttemptsPerSite;

        while (madeSites < sitesPerRaid && safety-- > 0)
        {
            var anchor = placeables[Random.Range(0, placeables.Count)];
            if (TryPickSpawnCellFarFromPlaceables(anchor.position, placeables, out var spawnCell))
            {
                Vector3 center = grid.GetCellCenterWorld(spawnCell);
                if (TrySpawnSite(center))
                {
                    madeSites++;
                }
            }
        }

        if (madeSites == 0)
            Debug.LogWarning("[EnemySpawnerRaid] スポーン地点が見つかりませんでした。パラメータを見直してください。");
    }

    // ★ ここを新しいAPIに変えた
    List<Transform> CollectPlaceables()
    {
        int mask = placeableLayerMask.value;
        var result = new List<Transform>();

        // 非推奨だった FindObjectsOfType<T>(true) の代わり
        var all = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);

        foreach (var t in all)
        {
            if (!t) continue;
            if (!t.gameObject.activeInHierarchy) continue;
            if (((1 << t.gameObject.layer) & mask) != 0)
            {
                result.Add(t);
            }
        }

        return result;
    }

    bool TryPickSpawnCellFarFromPlaceables(Vector3 baseWorld, List<Transform> placeables, out Vector3Int cell)
    {
        var cs = grid.cellSize;
        float cellDiag = Mathf.Max(0.01f, new Vector2(cs.x, cs.y).magnitude);

        int attempts = maxAttemptsPerSite;
        int minD = Mathf.Max(1, minDistanceInTiles);
        int maxD = minD + 8;

        while (attempts-- > 0)
        {
            int d = Random.Range(minD, maxD + 1);
            float ang = Random.Range(0f, Mathf.PI * 2f);

            var offset = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
            var candidateWorld = baseWorld + offset * (d * cellDiag * 0.72f);
            var candidateCell = grid.WorldToCell(candidateWorld);

            if (requireGroundTile && groundTilemap && !groundTilemap.HasTile(candidateCell))
                continue;

            bool ok = true;
            foreach (var p in placeables)
            {
                var pc = grid.WorldToCell(p.position);
                if (CellDistance(pc, candidateCell) < minD)
                {
                    ok = false; break;
                }
            }

            if (!ok) continue;

            cell = candidateCell;
            return true;
        }

        cell = default;
        return false;
    }

    int CellDistance(Vector3Int a, Vector3Int b)
    {
        int dx = b.x - a.x;
        int dy = b.y - a.y;

        if (!useHexLikeCellDistance)
            return Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));

        int adx = Mathf.Abs(dx);
        int ady = Mathf.Abs(dy);
        return Mathf.Max(adx, ady, Mathf.Abs(dx + dy));
    }

    bool TrySpawnSite(Vector3 centerWorld)
    {
        foreach (var p in _spawnedPositions)
        {
            if ((p - centerWorld).sqrMagnitude < (minEnemySeparationWorld * minEnemySeparationWorld))
                return false;
        }
        _spawnedPositions.Add(centerWorld);

        for (int i = 0; i < enemiesPerSite; i++)
        {
            Vector2 j = Random.insideUnitCircle * jitterWorld;
            var pos = new Vector3(centerWorld.x + j.x, centerWorld.y + j.y, 0f);
            Instantiate(enemyPrefab, pos, Quaternion.identity);
        }
        return true;
    }

    public void TriggerRaid()
    {
        if (requireBaseBeforeRaid && !BuildPlacement.s_baseBuilt)
            return;

        raid = true;
    }
}