using System.Collections.Generic;
using UnityEngine;

public class FlowField025 : MonoBehaviour
{
    [Header("Grid")]
    public float cellSize = 0.25f;
    public int gridW = 200;
    public int gridH = 200;

    // --- 以前はここに baseTransform があって、それを(0,0)に見立てていましたが ---
    // --- 今回は「あとからBaseの位置をもらう」方式に変えます                ---

    // 配列の中央を (0,0) にする
    int centerX;
    int centerY;

    int[,] dist;
    Vector2[,] flow;
    bool[,] blocked;

    // ★追加：現在のターゲット（=Base）のグリッド座標
    bool hasTarget = false;
    int targetGX;
    int targetGY;

    void Awake()
    {
        centerX = gridW / 2;
        centerY = gridH / 2;

        dist = new int[gridW, gridH];
        flow = new Vector2[gridW, gridH];
        blocked = new bool[gridW, gridH];

        // 最初は全部ふさいでおく（HexPerTileFineGrid が開けていく）
        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
                blocked[x, y] = true;
    }

    // ---------- 外から呼ぶAPI ----------

    // 「ここは歩ける」
    public void MarkWalkable(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;
        blocked[gx, gy] = false;
    }

    // 「ここはふさぐ」
    public void MarkBlocked(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;
        blocked[gx, gy] = true;
    }

    // ★追加：Baseが建ったときにここを呼んでもらう
    public void SetTargetWorld(Vector2 worldPos)
    {
        if (!WorldToCell(worldPos, out int gx, out int gy))
        {
            Debug.LogWarning($"FlowField025: target out of range {worldPos}");
            return;
        }

        targetGX = gx;
        targetGY = gy;
        hasTarget = true;

        // もらったらすぐ再計算
        Rebuild();
    }

    // 敵が読むとき用
    public Vector2 GetFlowDir(Vector2 worldPos)
    {
        // もう「Baseを(0,0)に見立てる」補正はしない
        if (!WorldToCell(worldPos, out int gx, out int gy))
            return Vector2.zero;
        return flow[gx, gy];
    }

    // 再計算
    public void Rebuild()
    {
        // ターゲットがまだ決まってないなら何もしない
        if (!hasTarget)
        {
            // flowをゼロで埋めておく
            for (int x = 0; x < gridW; x++)
                for (int y = 0; y < gridH; y++)
                    flow[x, y] = Vector2.zero;
            return;
        }

        BuildDistanceField();
        BuildDirectionField();
    }

    // ワールド → グリッドindex
    public bool WorldToCell(Vector2 world, out int gx, out int gy)
    {
        gx = Mathf.FloorToInt(world.x / cellSize) + centerX;
        gy = Mathf.FloorToInt(world.y / cellSize) + centerY;
        if (gx < 0 || gy < 0 || gx >= gridW || gy >= gridH) return false;
        return true;
    }

    // ---------- 中身 ----------

    void BuildDistanceField()
    {
        const int INF = 999999;

        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
                dist[x, y] = INF;

        var q = new Queue<Vector2Int>();

        // ★ここが今回一番大事なところ
        // これまでは「中央セルをゴールにしていた」が、
        // これからは「SetTargetWorld で渡されたセル」をゴールにする
        dist[targetGX, targetGY] = 0;
        q.Enqueue(new Vector2Int(targetGX, targetGY));

        // 4近傍
        Vector2Int[] dirs = {
            new Vector2Int( 1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int( 0, 1),
            new Vector2Int( 0,-1),
        };

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            int cd = dist[c.x, c.y];

            foreach (var d in dirs)
            {
                int nx = c.x + d.x;
                int ny = c.y + d.y;
                if (nx < 0 || ny < 0 || nx >= gridW || ny >= gridH) continue;
                if (blocked[nx, ny]) continue;
                if (dist[nx, ny] <= cd + 1) continue;

                dist[nx, ny] = cd + 1;
                q.Enqueue(new Vector2Int(nx, ny));
            }
        }
    }

    void BuildDirectionField()
    {
        Vector2Int[] dirs8 = {
            new Vector2Int( 1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int( 0, 1),
            new Vector2Int( 0,-1),
            new Vector2Int( 1, 1),
            new Vector2Int( 1,-1),
            new Vector2Int(-1, 1),
            new Vector2Int(-1,-1),
        };

        for (int x = 0; x < gridW; x++)
        {
            for (int y = 0; y < gridH; y++)
            {
                if (blocked[x, y])
                {
                    flow[x, y] = Vector2.zero;
                    continue;
                }

                int best = dist[x, y];
                Vector2 bestDir = Vector2.zero;

                foreach (var d in dirs8)
                {
                    int nx = x + d.x;
                    int ny = y + d.y;
                    if (nx < 0 || ny < 0 || nx >= gridW || ny >= gridH) continue;
                    if (dist[nx, ny] >= best) continue;

                    best = dist[nx, ny];
                    bestDir = new Vector2(d.x, d.y).normalized;
                }

                flow[x, y] = bestDir;
            }
        }
    }
}
