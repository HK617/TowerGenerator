
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public class EnemySpawnerRaid : MonoBehaviour
{
    [Header("Scene refs")]
    public Grid grid;
    public Tilemap groundTilemap;

    [Header("Enemy to spawn (real raid)")]
    public GameObject enemyPrefab;

    [Header("Spawn pointer (always visible)")]
    [Tooltip("次のレイドで敵が湧く予定地に常時表示するポインター")]
    public GameObject spawnPointerPrefab;
    [Tooltip("スポーン予定地を決めるときに距離の基準にするPlaceableのレイヤー")]
    public LayerMask placeableLayerMask;
    [Tooltip("1回のレイドで何か所から湧かせるか")]
    public int sitesPerRaid = 4;
    [Tooltip("基地から最低何タイル離すか")]
    public int minDistanceInTiles = 6;
    [Tooltip("スポーン地点を探すときの試行回数上限(1サイトあたり)")]
    public int maxAttemptsPerSite = 60;
    [Tooltip("そのタイルに地面タイルが無いときは候補にしない")]
    public bool requireGroundTile = true;
    [Tooltip("六角に近い距離を使うか")]
    public bool useHexLikeCellDistance = true;
    [Tooltip("スポーン予定地どうしの最小距離(ワールド)")]
    public float minEnemySeparationWorld = 0.8f;

    // 1つの予定地を保持するための小さなクラス
    class SpawnSite
    {
        public Vector3 worldPos;   // この世界座標から湧かせる
        public GameObject pointerGO; // 表示しているポインター
    }
    // 「次のレイドで使う予定地」を常にここに持つ
    readonly List<SpawnSite> _currentSites = new();

    [Header("Preview (flow-field line)")]
    [Tooltip("これに沿ってプレビューランナーを走らせる")]
    public FlowField025 flowField;
    [Tooltip("Collider + LineRenderer がついたプレハブ")]
    public GameObject pathPreviewRunnerPrefab;
    [Tooltip("プレビューランナーが回避すべき障害物のレイヤー")]
    public LayerMask previewObstacleMask;
    [Tooltip("プレビュー用のスピード倍率")]
    public float previewRunnerSpeedMul = 1.5f;

    // プレビュー中に作ったランナーをすべて保持する（ゴール済みも含む）
    readonly List<PathPreviewRunner> _previewRunnersAll = new();
    // 「まだ走っている」ランナーだけ知りたいときに使う（なくてもいい）
    readonly List<PathPreviewRunner> _previewRunnersActive = new();

    bool _previewMode = false;

    [Header("Raid timing (real enemies)")]
    public bool autoRaidEnabled = true;
    public float initialRaidDelaySeconds = 5f;
    public float raidIntervalSeconds = 60f;
    [Tooltip("Baseが設置されてからでなければ自動レイドをしない")]
    public bool requireBaseBeforeRaid = true;

    void Awake()
    {
        if (!grid) grid = FindObjectOfType<Grid>();
    }

    void Start()
    {
        // 起動時に「次のレイドの予定地」を決めておく
        RebuildNextRaidSitesAndPointers();

        // 本物のレイドは時間で発生
        ScheduleAutoRaid();
    }

    void OnDisable()
    {
        CancelInvoke(nameof(TriggerRaid));
    }

    void Update()
    {
        // ここではInputは見ない。PreviewRaidInputやUIボタンから TogglePreview() を呼んでもらう
    }

    // ==========================
    //  公開API: プレビューON/OFF
    // ==========================
    public void TogglePreview()
    {
        if (_previewMode)
            StopPreview();
        else
            StartPreview();
    }

    void StartPreview()
    {
        if (_previewMode) return;

        if (flowField == null || pathPreviewRunnerPrefab == null)
        {
            Debug.LogWarning("[EnemySpawnerRaid] Preview開始できません(flowField or runnerPrefab が未設定)");
            return;
        }

        if (_currentSites.Count == 0)
        {
            Debug.LogWarning("[EnemySpawnerRaid] Preview開始できません(予定地が0)");
            return;
        }

        _previewMode = true;
        // 建築ロックON
        BuildPlacement.s_buildLocked = true;

        _previewRunnersAll.Clear();
        _previewRunnersActive.Clear();

        // いま表示している「次回レイド予定地」からだけ走らせる
        foreach (var site in _currentSites)
        {
            var go = Instantiate(pathPreviewRunnerPrefab, site.worldPos, Quaternion.identity);
            var runner = go.GetComponent<PathPreviewRunner>();
            if (runner != null)
            {
                runner.Init(flowField, previewObstacleMask, OnPreviewRunnerFinished);
                runner.speed *= previewRunnerSpeedMul;

                _previewRunnersAll.Add(runner);
                _previewRunnersActive.Add(runner);
            }
            else
            {
                Debug.LogWarning("[EnemySpawnerRaid] runnerPrefabにPathPreviewRunnerが付いていません");
                Destroy(go);
            }
        }

        // 何も出せなかったらロックを戻す
        if (_previewRunnersAll.Count == 0)
        {
            _previewMode = false;
            BuildPlacement.s_buildLocked = false;
        }
    }

    void StopPreview()
    {
        if (!_previewMode) return;

        // プレビュー中に作ったランナーは「全部」ここで消す
        foreach (var r in _previewRunnersAll)
        {
            if (r) Destroy(r.gameObject);
        }
        _previewRunnersAll.Clear();
        _previewRunnersActive.Clear();

        _previewMode = false;
        BuildPlacement.s_buildLocked = false;
    }

    // ランナーから「ゴールしたよ」と通知が来る
    void OnPreviewRunnerFinished(PathPreviewRunner r)
    {
        // 走り終わっただけならactiveから外すだけ。表示は残す。
        _previewRunnersActive.Remove(r);
        // ここではStopPreviewしない。ユーザーがTogglePreview()したときにだけ消す。
    }

    // ==========================
    //  本物のレイド部分（プレビューとは無関係）
    // ==========================
    void ScheduleAutoRaid()
    {
        CancelInvoke(nameof(TriggerRaid));
        if (autoRaidEnabled && raidIntervalSeconds > 0f)
        {
            float first = Mathf.Max(0f, initialRaidDelaySeconds);
            InvokeRepeating(nameof(TriggerRaid), first, raidIntervalSeconds);
        }
    }

    public void TriggerRaid()
    {
        // Baseがまだなら何もしない
        if (requireBaseBeforeRaid && !BuildPlacement.s_baseBuilt)
            return;

        // 今持ってる予定地から本物の敵を湧かせる
        DoRaidFromCurrentSites();

        // レイドが終わったらすぐに「次のレイド用の場所」を決め直す
        RebuildNextRaidSitesAndPointers();
    }

    void DoRaidFromCurrentSites()
    {
        if (enemyPrefab == null)
        {
            Debug.LogWarning("[EnemySpawnerRaid] enemyPrefab が未設定です。敵は出ません。");
            return;
        }

        foreach (var site in _currentSites)
        {
            // 敵を複数体生成する（従来どおり）
            for (int i = 0; i < 3; i++)
            {
                Vector2 j = Random.insideUnitCircle * 0.25f;
                Vector3 spawnPos = site.worldPos + new Vector3(j.x, j.y, 0f);

                // 実際に敵を生成
                GameObject enemy = Instantiate(enemyPrefab, spawnPos, Quaternion.identity);

                // ★ EnemyPathGrid を正式に使う
                if (EnemyPathGrid.Instance != null)
                {
                    // 敵スタートセル（Fine グリッドではない）
                    Vector2Int startCell = EnemyPathGrid.Instance.WorldToCell(spawnPos);

                    // Base までのレールを生成
                    List<Vector2> path = EnemyPathGrid.Instance.BuildPathFromWorld(spawnPos);

                    // follower に渡す
                    var follower = enemy.GetComponent<EnemyPathFollower>();
                    if (follower != null)
                        follower.SetPath(path);
                }
            }
        }
    }

    // ==========================
    //  「次のレイド地点を決めてポインターを出す」
    // ==========================
    void RebuildNextRaidSitesAndPointers()
    {
        // 今のポインターは全部消す
        foreach (var s in _currentSites)
        {
            if (s.pointerGO) Destroy(s.pointerGO);
        }
        _currentSites.Clear();

        // placeableから「基準になる場所」を集める
        var placeables = CollectPlaceables();
        if (placeables.Count == 0) return;

        int made = 0;
        int safety = sitesPerRaid * maxAttemptsPerSite;

        while (made < sitesPerRaid && safety-- > 0)
        {
            var anchor = placeables[Random.Range(0, placeables.Count)];

            if (TryPickSpawnCellFarFromPlaceables(anchor.position, placeables, out var cell))
            {
                Vector3 center = grid.GetCellCenterWorld(cell);

                // すでに決めたサイトと近すぎたらやり直し
                bool tooClose = false;
                foreach (var s in _currentSites)
                {
                    if ((s.worldPos - center).sqrMagnitude < (minEnemySeparationWorld * minEnemySeparationWorld))
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                GameObject ptr = null;
                if (spawnPointerPrefab != null)
                {
                    ptr = Instantiate(spawnPointerPrefab, center, Quaternion.identity);
                }

                _currentSites.Add(new SpawnSite
                {
                    worldPos = center,
                    pointerGO = ptr
                });

                made++;
            }
        }
    }

    // ==========================
    //  補助: placeable検索 & スポーン候補決定
    // ==========================
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
}
