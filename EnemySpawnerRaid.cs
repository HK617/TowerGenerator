using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class EnemySpawnerRaid : MonoBehaviour
{
    [Header("Scene References")]
    public Grid grid;
    public Tilemap groundTilemap;
    public FlowField025 flowField;

    [Header("Who to spawn")]
    public GameObject enemyPrefab;

    [Header("Distance from Base (tiles)")]
    public int minDistanceInTiles = 6;
    public int maxDistanceInTiles = 12;

    [Header("Spawn site settings")]
    public int maxSpawnSitesPerRaid = 3;
    public int enemiesPerSite = 3;
    public float jitterWorld = 0.35f;
    public int maxAttemptsPerSite = 40;
    public float minEnemySeparationWorld = 1.2f;

    [Header("Raid control")]
    public bool raid = false;
    public bool autoRaidEnabled = true;
    public float initialRaidDelaySeconds = 5f;
    public float raidIntervalSeconds = 60f;
    public bool requireBaseBeforeRaid = true;

    [Header("Preview")]
    public GameObject spawnPreviewPrefab;
    public float previewLifeSeconds = 3f;
    public bool previewDrawPath = true;

    [Header("Placeable check")]
    public LayerMask placeableLayerMask = 0;

    bool _raidConsumed = false;
    readonly List<Vector3> _spawnedSitesThisRaid = new();

    void Start()
    {
        if (autoRaidEnabled && raidIntervalSeconds > 0f)
        {
            InvokeRepeating(nameof(TriggerRaid),
                Mathf.Max(0f, initialRaidDelaySeconds),
                raidIntervalSeconds);
        }
    }

    void Update()
    {
        if (!enemyPrefab || !grid)
            return;

        // Baseが必要で、まだ置かれてないならやらない
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

        if (autoRaidEnabled && raidIntervalSeconds > 0f && !IsInvoking(nameof(TriggerRaid)))
        {
            InvokeRepeating(nameof(TriggerRaid),
                Mathf.Max(0f, initialRaidDelaySeconds),
                raidIntervalSeconds);
        }
        else if (!autoRaidEnabled && IsInvoking(nameof(TriggerRaid)))
        {
            CancelInvoke(nameof(TriggerRaid));
        }
    }

    public void TriggerRaid()
    {
        if (requireBaseBeforeRaid && !BuildPlacement.s_baseBuilt)
            return;
        raid = true;
    }

    void SpawnForRaid()
    {
        _spawnedSitesThisRaid.Clear();

        // ★ここでBuildPlacementが覚えているBase座標を使う
        Vector3 baseWorld = GetBaseWorldFromBuildPlacement();
        var placeables = CollectPlaceables();

        int sites = 0;
        for (int i = 0; i < maxSpawnSitesPerRaid; i++)
        {
            if (TryPickSpawnCellNearBase(baseWorld, placeables, out var cell))
            {
                Vector3 worldCenter = CellToWorldCenter(cell);
                if (TrySpawnAtWorld(worldCenter, baseWorld))
                {
                    _spawnedSitesThisRaid.Add(worldCenter);
                    sites++;
                }
            }
            else
            {
                // 見つからなかったらフォールバック（なくてもいい）
                if (TryPickSpawnCellGlobal(placeables, out var cell2))
                {
                    Vector3 worldCenter = CellToWorldCenter(cell2);
                    if (TrySpawnAtWorld(worldCenter, baseWorld))
                    {
                        _spawnedSitesThisRaid.Add(worldCenter);
                        sites++;
                    }
                }
            }
        }

        if (sites == 0)
        {
            Debug.LogWarning("[EnemySpawnerRaid] 近くにスポーンできるタイルがありませんでした。");
        }
    }

    Vector3 GetBaseWorldFromBuildPlacement()
    {
        if (BuildPlacement.s_hasBaseWorld)
            return BuildPlacement.s_baseWorld;

        // 念のためのフォールバック
        return Vector3.zero;
    }

    bool TrySpawnAtWorld(Vector3 centerWorld, Vector3 baseWorld)
    {
        foreach (var p in _spawnedSitesThisRaid)
        {
            if ((p - centerWorld).sqrMagnitude < (minEnemySeparationWorld * minEnemySeparationWorld))
                return false;
        }

        if (spawnPreviewPrefab != null)
        {
            var go = Instantiate(spawnPreviewPrefab, centerWorld, Quaternion.identity);
            var prev = go.GetComponent<SpawnPreviewToEnemy>();
            if (prev != null)
            {
                // ★ここでBaseの座標も渡す！
                prev.Init(
                    enemyPrefab,
                    enemiesPerSite,
                    jitterWorld,
                    previewLifeSeconds,
                    flowField,
                    previewDrawPath,
                    baseWorld,
                    BuildPlacement.s_hasBaseWorld
                );
            }
        }
        else
        {
            // 従来どおり即スポーン
            for (int i = 0; i < enemiesPerSite; i++)
            {
                Vector2 j = Random.insideUnitCircle * jitterWorld;
                var pos = new Vector3(centerWorld.x + j.x, centerWorld.y + j.y, 0f);
                Instantiate(enemyPrefab, pos, Quaternion.identity);
            }
        }

        return true;
    }

    bool TryPickSpawnCellNearBase(Vector3 baseWorld, List<Transform> placeables, out Vector3Int cell)
    {
        cell = default;
        if (grid == null || groundTilemap == null)
            return false;

        Vector3Int baseCell = grid.WorldToCell(baseWorld);
        int attempts = Mathf.Max(1, maxAttemptsPerSite);

        int minD = Mathf.Max(1, minDistanceInTiles);
        int maxD = Mathf.Max(minD + 1, maxDistanceInTiles);

        while (attempts-- > 0)
        {
            int r = Random.Range(minD, maxD + 1);
            float ang = Random.Range(0f, Mathf.PI * 2f);
            int cx = baseCell.x + Mathf.RoundToInt(r * Mathf.Cos(ang));
            int cy = baseCell.y + Mathf.RoundToInt(r * Mathf.Sin(ang));
            var c = new Vector3Int(cx, cy, 0);

            if (!groundTilemap.HasTile(c))
                continue;

            Vector3 w = CellToWorldCenter(c);

            // FlowFieldの外は捨てる
            if (flowField != null && !flowField.WorldToCell(w, out _, out _))
                continue;

            // 他サイトとの距離
            bool tooClose = false;
            foreach (var s in _spawnedSitesThisRaid)
            {
                if ((s - w).sqrMagnitude < (minEnemySeparationWorld * minEnemySeparationWorld))
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
                continue;

            // Placeableとの距離
            if (!IsFarEnoughFromPlaceables(w, placeables, minD))
                continue;

            cell = c;
            return true;
        }

        return false;
    }

    bool TryPickSpawnCellGlobal(List<Transform> placeables, out Vector3Int cell)
    {
        cell = default;
        if (groundTilemap == null || grid == null)
            return false;

        BoundsInt b = groundTilemap.cellBounds;
        int attempts = Mathf.Max(1, maxAttemptsPerSite);
        int minD = Mathf.Max(1, minDistanceInTiles);

        while (attempts-- > 0)
        {
            int cx = Random.Range(b.xMin, b.xMax);
            int cy = Random.Range(b.yMin, b.yMax);
            var c = new Vector3Int(cx, cy, 0);

            if (!groundTilemap.HasTile(c))
                continue;

            Vector3 w = CellToWorldCenter(c);

            if (flowField != null && !flowField.WorldToCell(w, out _, out _))
                continue;

            if (!IsFarEnoughFromPlaceables(w, placeables, minD))
                continue;

            cell = c;
            return true;
        }

        return false;
    }

    bool IsFarEnoughFromPlaceables(Vector3 world, List<Transform> placeables, int minTiles)
    {
        if (placeables == null || placeables.Count == 0)
            return true;

        foreach (var p in placeables)
        {
            if (!p) continue;

            Vector3 diff = world - p.position;
            float dx = Mathf.Abs(diff.x) / grid.cellSize.x;
            float dy = Mathf.Abs(diff.y) / grid.cellSize.y;
            float tileDist = Mathf.Sqrt(dx * dx + dy * dy);
            if (tileDist < minTiles)
                return false;
        }

        return true;
    }

    List<Transform> CollectPlaceables()
    {
        int mask = placeableLayerMask.value;
        var result = new List<Transform>();
        var all = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        foreach (var t in all)
        {
            if (!t) continue;
            if (!t.gameObject.activeInHierarchy) continue;
            if (((1 << t.gameObject.layer) & mask) != 0)
                result.Add(t);
        }
        return result;
    }

    Vector3 CellToWorldCenter(Vector3Int cell)
    {
        Vector3 w = grid.CellToWorld(cell);
        w += (Vector3)grid.cellSize * 0.5f;
        w.z = 0f;
        return w;
    }
}
