using System.Collections.Generic;
using UnityEngine;

public class FlowField025 : MonoBehaviour
{
    [Header("Grid")]
    public float cellSize = 0.25f;
    public int gridW = 200;
    public int gridH = 200;

    [Header("Target (optional)")]
    public Transform baseTransform; // ← Baseをドラッグしておく

    // 配列の中央を (0,0) にする
    int centerX;
    int centerY;

    int[,] dist;
    Vector2[,] flow;
    bool[,] blocked;

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

    // HexPerTileFineGrid から呼ばれる：「ここは歩ける」
    public void MarkWalkable(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;
        blocked[gx, gy] = false;
    }

    // 建物を置いたときなどに使う：「ここはふさぐ」
    public void MarkBlocked(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;
        blocked[gx, gy] = true;
    }

    // 敵が読むとき用
    public Vector2 GetFlowDir(Vector2 worldPos)
    {
        // Baseを(0,0)に見立てる補正
        if (baseTransform != null)
            worldPos -= (Vector2)baseTransform.position;

        if (!WorldToCell(worldPos, out int gx, out int gy))
            return Vector2.zero;
        return flow[gx, gy];
    }

    // Hex 側で「全部並べたから再計算して」と呼ぶ
    public void Rebuild()
    {
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

        // Base は (0,0) → 中央セル
        dist[centerX, centerY] = 0;
        q.Enqueue(new Vector2Int(centerX, centerY));

        // 4近傍（必要なら8近傍でもOK）
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
        // 8方向を見て「自分より距離が小さい方向」を向く
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
