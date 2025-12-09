using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敵専用の「レール用グリッド」.
/// - ワールドを矩形グリッドに量子化
/// - Base セルをゴールとして Dijkstra で全セルの最小コスト距離を計算
/// - 建物が完成 / 解体されたら SetBuildingAtWorld でセルにコストを設定
/// - EnemySpawnerRaid / EnemyPathFollower から利用する
/// </summary>
public class EnemyPathGrid : MonoBehaviour
{
    // --------- シングルトン ---------
    public static EnemyPathGrid Instance { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        InitGrid();
    }

    // --------- 設定 ---------
    [Header("Grid Settings")]
    [Tooltip("セルの X 方向数")]
    public int width = 200;

    [Tooltip("セルの Y 方向数")]
    public int height = 200;

    [Tooltip("1 セルのワールドサイズ（FineGrid のセルと同じにする）")]
    public float cellSize = 0.5f;

    [Tooltip("セル (0,0) のワールド座標（左下隅）。\nマップ全体が収まるように設定してください。")]
    public Vector2 originWorld = new Vector2(-50f, -50f);

    [Header("Cost Settings")]
    [Tooltip("何もないセルの基本移動コスト")]
    public int emptyMoveCost = 1;

    [Tooltip("建物があるセルの追加コスト倍率")]
    public int buildingMoveCost = 100;

    // --------- 内部状態 ---------

    enum CellType
    {
        Empty,
        Solid,          // 破壊不可
        Destructible    // 壊すと通れる
    }

    struct CellInfo
    {
        public CellType type;
        public int moveCost;   // 移動コスト（Empty なら emptyMoveCost）
        public int breakCost;  // 壊して通る場合の追加コスト
    }

    CellInfo[,] cells;     // [x,y]
    int[,] dist;           // Dijkstra の距離
    bool dirty = true;     // 変更されたら true → 次回 BuildPathFrom で Rebuild

    // Base のセル
    Vector2Int goalCell;
    bool goalSet = false;

    // --------- 優先度付きキュー（簡易実装） ---------
    class SimplePriorityQueue<T>
    {
        readonly List<(T item, int priority)> _heap = new();

        public int Count => _heap.Count;

        int Compare(int a, int b) => a.CompareTo(b);

        public void Enqueue(T item, int priority)
        {
            _heap.Add((item, priority));
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (Compare(_heap[i].priority, _heap[p].priority) >= 0) break;
                (_heap[i], _heap[p]) = (_heap[p], _heap[i]);
                i = p;
            }
        }

        public T Dequeue()
        {
            var root = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);

            int i = 0;
            while (true)
            {
                int l = i * 2 + 1;
                int r = l + 1;
                if (l >= _heap.Count) break;

                int best = l;
                if (r < _heap.Count && Compare(_heap[r].priority, _heap[l].priority) < 0)
                    best = r;

                if (Compare(_heap[best].priority, _heap[i].priority) >= 0) break;
                (_heap[i], _heap[best]) = (_heap[best], _heap[i]);
                i = best;
            }

            return root.item;
        }
    }

    // --------- 初期化 ---------
    void InitGrid()
    {
        cells = new CellInfo[width, height];
        dist = new int[width, height];

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                cells[x, y].type = CellType.Empty;
                cells[x, y].moveCost = emptyMoveCost;
                cells[x, y].breakCost = 0;
            }

        dirty = true;
    }

    // --------- 座標変換 ---------

    /// <summary>ワールド座標 → セル座標</summary>
    public Vector2Int WorldToCell(Vector3 world)
    {
        Vector2 local = (Vector2)world - originWorld;
        int x = Mathf.FloorToInt(local.x / cellSize);
        int y = Mathf.FloorToInt(local.y / cellSize);
        return new Vector2Int(x, y);
    }

    /// <summary>セル座標 → ワールド座標（セル中心）</summary>
    public Vector2 CellToWorld(Vector2Int cell)
    {
        float wx = originWorld.x + (cell.x + 0.5f) * cellSize;
        float wy = originWorld.y + (cell.y + 0.5f) * cellSize;
        return new Vector2(wx, wy);
    }

    bool InBounds(Vector2Int c)
        => c.x >= 0 && c.y >= 0 && c.x < width && c.y < height;

    void MarkDirty() => dirty = true;

    // --------- Base ゴール設定 ---------

    /// <summary>Base の中心ワールド座標を渡してゴールセルを決める</summary>
    public void SetGoalByBaseWorld(Vector3 baseWorldPos)
    {
        goalCell = WorldToCell(baseWorldPos);

        if (!InBounds(goalCell))
        {
            Debug.LogWarning($"[EnemyPathGrid] goalCell {goalCell} がグリッド範囲外です。originWorld/width/height/cellSize を調整してください。");
            goalSet = false;
            return;
        }

        goalSet = true;
        MarkDirty();
    }

    // --------- 建物登録 ---------

    /// <summary>
    /// worldPos にある建物を「このセルは def で埋まっている」として登録する。
    /// def == null なら空セルとして登録。
    /// </summary>
    public void SetBuildingAtWorld(Vector3 worldPos, BuildingDef def)
    {
        if (cells == null) InitGrid();

        Vector2Int cell = WorldToCell(worldPos);
        if (!InBounds(cell))
        {
            // マップ外の建物は無視
            return;
        }

        if (def == null)
        {
            // 空白
            cells[cell.x, cell.y].type = CellType.Empty;
            cells[cell.x, cell.y].moveCost = emptyMoveCost;
            cells[cell.x, cell.y].breakCost = 0;
            MarkDirty();
            return;
        }

        // destructibleForEnemy などの設定を BuildingDef 側に追加してある前提
        if (!def.destructibleForEnemy)
        {
            // 絶対壊さない壁
            cells[cell.x, cell.y].type = CellType.Solid;
            cells[cell.x, cell.y].moveCost = int.MaxValue;
            cells[cell.x, cell.y].breakCost = int.MaxValue;
        }
        else
        {
            cells[cell.x, cell.y].type = CellType.Destructible;
            cells[cell.x, cell.y].moveCost = buildingMoveCost;
            cells[cell.x, cell.y].breakCost = def.GetEnemyBreakCost();
        }

        MarkDirty();
    }

    int StepCost(CellInfo info)
    {
        if (info.type == CellType.Solid)
            return int.MaxValue;

        // 壊さないで通る前提のシンプル版
        long total = (long)info.moveCost + info.breakCost;
        if (total >= int.MaxValue) return int.MaxValue;
        return (int)total;
    }

    // --------- Dijkstra 本体 ---------
    public void Rebuild()
    {
        if (!goalSet)
            return;

        dirty = false;

        const int INF = int.MaxValue;

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                dist[x, y] = INF;

        var pq = new SimplePriorityQueue<Vector2Int>();
        dist[goalCell.x, goalCell.y] = 0;
        pq.Enqueue(goalCell, 0);

        Vector2Int[] dirs =
        {
            new Vector2Int( 1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int( 0, 1),
            new Vector2Int( 0,-1),
        };

        while (pq.Count > 0)
        {
            var c = pq.Dequeue();
            int cd = dist[c.x, c.y];
            if (cd == INF) continue;

            foreach (var d in dirs)
            {
                Vector2Int n = new Vector2Int(c.x + d.x, c.y + d.y);
                if (!InBounds(n)) continue;

                int sc = StepCost(cells[n.x, n.y]);
                if (sc >= INF) continue;

                int nd = cd + sc;
                if (nd < dist[n.x, n.y])
                {
                    dist[n.x, n.y] = nd;
                    pq.Enqueue(n, nd);
                }
            }
        }
    }

    // --------- ルート復元 ---------

    /// <summary>
    /// startWorld から Base までのレール（ワールド座標のリスト）を生成。
    /// </summary>
    public List<Vector2> BuildPathFromWorld(Vector3 startWorld)
    {
        Vector2Int startCell = WorldToCell(startWorld);
        return BuildPathFromCell(startCell);
    }

    /// <summary>
    /// startCell から Base までのセル中心ワールド座標リスト。
    /// </summary>
    public List<Vector2> BuildPathFromCell(Vector2Int startCell)
    {
        var path = new List<Vector2>();

        if (!goalSet)
            return path;

        if (!InBounds(startCell))
        {
            Debug.LogWarning($"[EnemyPathGrid] startCell {startCell} がグリッド範囲外です");
            return path;
        }

        if (dirty) Rebuild();

        if (dist[startCell.x, startCell.y] >= int.MaxValue)
        {
            // 到達不能
            return path;
        }

        Vector2Int cur = startCell;

        // goalCell には必ず到達できる前提
        while (cur != goalCell)
        {
            path.Add(CellToWorld(cur));

            int bestDist = dist[cur.x, cur.y];
            Vector2Int bestNext = cur;

            // 4 方向のうち、dist が最も小さい方向へ一歩進む
            Vector2Int[] dirs =
            {
                new Vector2Int( 1, 0),
                new Vector2Int(-1, 0),
                new Vector2Int( 0, 1),
                new Vector2Int( 0,-1),
            };

            foreach (var d in dirs)
            {
                Vector2Int n = new Vector2Int(cur.x + d.x, cur.y + d.y);
                if (!InBounds(n)) continue;

                if (dist[n.x, n.y] < bestDist)
                {
                    bestDist = dist[n.x, n.y];
                    bestNext = n;
                }
            }

            if (bestNext == cur)
            {
                // これ以上進めない（ローカルミニマム）→ 終了
                break;
            }

            cur = bestNext;
        }

        // 最後にゴールセルも追加
        path.Add(CellToWorld(goalCell));

        return path;
    }
}
