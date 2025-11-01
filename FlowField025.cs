using System.Collections.Generic;
using UnityEngine;

public class FlowField025 : MonoBehaviour
{
    [Header("Grid")]
    public float cellSize = 0.25f;
    public int gridW = 200;
    public int gridH = 200;

    [Header("Target (optional)")]
    public Transform baseTransform; // �� Base���h���b�O���Ă���

    // �z��̒����� (0,0) �ɂ���
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

        // �ŏ��͑S���ӂ����ł����iHexPerTileFineGrid ���J���Ă����j
        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
                blocked[x, y] = true;
    }

    // ---------- �O����Ă�API ----------

    // HexPerTileFineGrid ����Ă΂��F�u�����͕�����v
    public void MarkWalkable(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;
        blocked[gx, gy] = false;
    }

    // ������u�����Ƃ��ȂǂɎg���F�u�����͂ӂ����v
    public void MarkBlocked(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;
        blocked[gx, gy] = true;
    }

    // �G���ǂނƂ��p
    public Vector2 GetFlowDir(Vector2 worldPos)
    {
        // Base��(0,0)�Ɍ����Ă�␳
        if (baseTransform != null)
            worldPos -= (Vector2)baseTransform.position;

        if (!WorldToCell(worldPos, out int gx, out int gy))
            return Vector2.zero;
        return flow[gx, gy];
    }

    // Hex ���Łu�S�����ׂ�����Čv�Z���āv�ƌĂ�
    public void Rebuild()
    {
        BuildDistanceField();
        BuildDirectionField();
    }

    // ���[���h �� �O���b�hindex
    public bool WorldToCell(Vector2 world, out int gx, out int gy)
    {
        gx = Mathf.FloorToInt(world.x / cellSize) + centerX;
        gy = Mathf.FloorToInt(world.y / cellSize) + centerY;
        if (gx < 0 || gy < 0 || gx >= gridW || gy >= gridH) return false;
        return true;
    }

    // ---------- ���g ----------

    void BuildDistanceField()
    {
        const int INF = 999999;

        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
                dist[x, y] = INF;

        var q = new Queue<Vector2Int>();

        // Base �� (0,0) �� �����Z��
        dist[centerX, centerY] = 0;
        q.Enqueue(new Vector2Int(centerX, centerY));

        // 4�ߖT�i�K�v�Ȃ�8�ߖT�ł�OK�j
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
        // 8���������āu������苗���������������v������
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
