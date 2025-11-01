using System.Collections.Generic;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

public class FlowField025 : MonoBehaviour
{
    [Header("Grid")]
    public float cellSize = 0.25f;
    public int gridW = 200;
    public int gridH = 200;

    [Header("Job / Burst")]
    public bool useJobs = true;
    public float checkJobInterval = 0.02f;

    int centerX;
    int centerY;

    int[,] dist;
    Vector2[,] flow;
    bool[,] blocked;

    bool hasTarget = false;
    int targetGX;
    int targetGY;

    NativeArray<byte> nBlocked;
    NativeArray<int> nDist;
    NativeArray<float2> nFlow;

    JobHandle rebuildHandle;
    bool jobRunning = false;
    bool rebuildQueued = false;
    float nextJobCheckTime = 0f;

    public bool IsGoalNear(Vector2 worldPos, float radius)
    {
        if (!hasTarget) return false;

        // targetGX / targetGY はグリッド上のゴールなので、ワールドに戻す
        float goalWx = (targetGX - centerX + 0.5f) * cellSize;
        float goalWy = (targetGY - centerY + 0.5f) * cellSize;
        Vector2 goalWorld = new Vector2(goalWx, goalWy);

        return (worldPos - goalWorld).sqrMagnitude <= radius * radius;
    }


    // ★ ジョブ中の書き込みを貯めておくバッファ
    struct PendingWrite
    {
        public int gx, gy;
        public byte value; // 0 walkable, 1 blocked
    }
    List<PendingWrite> pendingWrites = new List<PendingWrite>(256);
    bool pendingWritesNeedRebuild = false;

    const int INF = 999999;

    void Awake()
    {
        centerX = gridW / 2;
        centerY = gridH / 2;

        dist = new int[gridW, gridH];
        flow = new Vector2[gridW, gridH];
        blocked = new bool[gridW, gridH];

        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
                blocked[x, y] = true;   // 初期はふさいでおく

        int len = gridW * gridH;
        nBlocked = new NativeArray<byte>(len, Allocator.Persistent);
        nDist = new NativeArray<int>(len, Allocator.Persistent);
        nFlow = new NativeArray<float2>(len, Allocator.Persistent);

        for (int i = 0; i < len; i++)
        {
            nBlocked[i] = 1;
            nDist[i] = INF;
            nFlow[i] = float2.zero;
        }
    }

    void OnDestroy()
    {
        if (jobRunning)
        {
            rebuildHandle.Complete();
            jobRunning = false;
        }

        if (nBlocked.IsCreated) nBlocked.Dispose();
        if (nDist.IsCreated) nDist.Dispose();
        if (nFlow.IsCreated) nFlow.Dispose();
    }

    void Update()
    {
        // ジョブが動いているなら、たまに様子を見る
        if (jobRunning && Time.unscaledTime >= nextJobCheckTime)
        {
            if (rebuildHandle.IsCompleted)
            {
                rebuildHandle.Complete();
                jobRunning = false;

                // ジョブの結果をマネージド側にコピー
                CopyNativeToManaged();

                // ★ 溜まってた書き込みをいま適用
                ApplyPendingWrites();

                // ★ バッファ書き込みで「もう一回組み直したい」ならここで1回だけRebuild
                if (pendingWritesNeedRebuild || rebuildQueued)
                {
                    pendingWritesNeedRebuild = false;
                    rebuildQueued = false;
                    Rebuild();   // ここではjobRunning=falseなので素直に走る
                }
            }

            nextJobCheckTime = Time.unscaledTime + checkJobInterval;
        }
    }

    // ───────────────── 外部API ─────────────────

    public void MarkWalkable(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;

        if (jobRunning)
        {
            // ★ いまは触れないのでキューに積むだけ
            pendingWrites.Add(new PendingWrite { gx = gx, gy = gy, value = 0 });
            pendingWritesNeedRebuild = true; // これを適用したら再計算したい
            return;
        }

        // ジョブが走っていないなら即時反映
        blocked[gx, gy] = false;
        nBlocked[ToIndex(gx, gy)] = 0;

        if (useJobs && hasTarget)
            Rebuild();
    }

    public void MarkBlocked(float wx, float wy)
    {
        if (!WorldToCell(new Vector2(wx, wy), out int gx, out int gy)) return;

        if (jobRunning)
        {
            pendingWrites.Add(new PendingWrite { gx = gx, gy = gy, value = 1 });
            pendingWritesNeedRebuild = true;
            return;
        }

        blocked[gx, gy] = true;
        nBlocked[ToIndex(gx, gy)] = 1;

        if (useJobs && hasTarget)
            Rebuild();
    }

    public void SetTargetWorld(Vector2 world)
    {
        if (!WorldToCell(world, out int gx, out int gy))
        {
            hasTarget = false;
            return;
        }

        // ★ ゴール変更は優先度高いので、ジョブ中なら一回終わるのを待ってから反映する
        if (jobRunning)
        {
            // 終わったあとにもう一度呼んでほしいのでフラグを立てるだけでもいいが、
            // ここではシンプルに後でRebuildさせる
            rebuildQueued = true;
            targetGX = gx;
            targetGY = gy;
            hasTarget = true;
            return;
        }

        targetGX = gx;
        targetGY = gy;
        hasTarget = true;

        Rebuild();
    }

    public Vector2 GetFlowDir(Vector2 worldPos)
    {
        if (!WorldToCell(worldPos, out int gx, out int gy))
            return Vector2.zero;
        return flow[gx, gy];
    }

    public void Rebuild()
    {
        if (!hasTarget)
        {
            // ターゲット無いなら全部ゼロ
            for (int x = 0; x < gridW; x++)
                for (int y = 0; y < gridH; y++)
                    flow[x, y] = Vector2.zero;
            return;
        }

        if (!useJobs)
        {
            BuildDistanceFieldSync();
            BuildDirectionFieldSync();
            return;
        }

        // すでに走ってたら「終わったらやって」でOK
        if (jobRunning)
        {
            rebuildQueued = true;
            return;
        }

        // ★いま書き込みバッファが残ってたら先に適用してから走る
        if (pendingWrites.Count > 0)
            ApplyPendingWrites();

        // ジョブをスケジュール
        var distJob = new DistanceFieldJob
        {
            width = gridW,
            height = gridH,
            targetX = targetGX,
            targetY = targetGY,
            blocked = nBlocked,
            dist = nDist
        };
        JobHandle h1 = distJob.Schedule();

        var dirJob = new DirectionFieldJob
        {
            width = gridW,
            height = gridH,
            blocked = nBlocked,
            dist = nDist,
            flow = nFlow
        };
        JobHandle h2 = dirJob.Schedule(h1);

        rebuildHandle = h2;
        jobRunning = true;
        nextJobCheckTime = Time.unscaledTime + checkJobInterval;
    }

    // ───────────────── 同期版 ─────────────────

    void BuildDistanceFieldSync()
    {
        for (int x = 0; x < gridW; x++)
            for (int y = 0; y < gridH; y++)
                dist[x, y] = INF;

        var q = new Queue<Vector2Int>();
        dist[targetGX, targetGY] = 0;
        q.Enqueue(new Vector2Int(targetGX, targetGY));

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

        // nativeにも反映
        for (int y = 0; y < gridH; y++)
            for (int x = 0; x < gridW; x++)
                nDist[ToIndex(x, y)] = dist[x, y];
    }

    void BuildDirectionFieldSync()
    {
        for (int x = 0; x < gridW; x++)
        {
            for (int y = 0; y < gridH; y++)
            {
                if (blocked[x, y])
                {
                    flow[x, y] = Vector2.zero;
                    nFlow[ToIndex(x, y)] = float2.zero;
                    continue;
                }

                int best = dist[x, y];
                Vector2 bestDir = Vector2.zero;

                CheckDirSync(x, y, 1, 0, ref best, ref bestDir);
                CheckDirSync(x, y, -1, 0, ref best, ref bestDir);
                CheckDirSync(x, y, 0, 1, ref best, ref bestDir);
                CheckDirSync(x, y, 0, -1, ref best, ref bestDir);
                CheckDirSync(x, y, 1, 1, ref best, ref bestDir);
                CheckDirSync(x, y, 1, -1, ref best, ref bestDir);
                CheckDirSync(x, y, -1, 1, ref best, ref bestDir);
                CheckDirSync(x, y, -1, -1, ref best, ref bestDir);

                flow[x, y] = bestDir;
                nFlow[ToIndex(x, y)] = new float2(bestDir.x, bestDir.y);
            }
        }
    }

    void CheckDirSync(int x, int y, int dx, int dy, ref int best, ref Vector2 bestDir)
    {
        int nx = x + dx;
        int ny = y + dy;
        if (nx < 0 || ny < 0 || nx >= gridW || ny >= gridH) return;
        int nd = dist[nx, ny];
        if (nd >= best) return;
        best = nd;
        bestDir = new Vector2(dx, dy).normalized;
    }

    // ───────────────── ユーティリティ ─────────────────

    public bool WorldToCell(Vector2 world, out int gx, out int gy)
    {
        gx = Mathf.FloorToInt(world.x / cellSize) + centerX;
        gy = Mathf.FloorToInt(world.y / cellSize) + centerY;
        if (gx < 0 || gy < 0 || gx >= gridW || gy >= gridH) return false;
        return true;
    }

    int ToIndex(int x, int y) => y * gridW + x;

    void CopyNativeToManaged()
    {
        for (int y = 0; y < gridH; y++)
        {
            for (int x = 0; x < gridW; x++)
            {
                int idx = ToIndex(x, y);
                dist[x, y] = nDist[idx];
                var f = nFlow[idx];
                flow[x, y] = new Vector2(f.x, f.y);
            }
        }
    }

    void ApplyPendingWrites()
    {
        if (pendingWrites.Count == 0) return;

        foreach (var pw in pendingWrites)
        {
            // 範囲チェックだけ一応
            if (pw.gx < 0 || pw.gx >= gridW || pw.gy < 0 || pw.gy >= gridH)
                continue;

            blocked[pw.gx, pw.gy] = (pw.value == 1);
            nBlocked[ToIndex(pw.gx, pw.gy)] = pw.value;
        }

        pendingWrites.Clear();
    }

    // ───────────────── Job定義 ─────────────────

    [BurstCompile]
    struct DistanceFieldJob : IJob
    {
        public int width;
        public int height;
        public int targetX;
        public int targetY;

        [ReadOnly] public NativeArray<byte> blocked;
        public NativeArray<int> dist;

        public void Execute()
        {
            int total = width * height;
            const int INF_LOCAL = 999999;
            for (int i = 0; i < total; i++)
                dist[i] = INF_LOCAL;

            var q = new NativeQueue<int2>(Allocator.Temp);
            dist[targetY * width + targetX] = 0;
            q.Enqueue(new int2(targetX, targetY));

            while (q.Count > 0)
            {
                var c = q.Dequeue();
                int baseIdx = c.y * width + c.x;
                int cd = dist[baseIdx];

                // 右
                if (c.x + 1 < width) TryVisit(c.x + 1, c.y, cd, ref q);
                // 左
                if (c.x - 1 >= 0) TryVisit(c.x - 1, c.y, cd, ref q);
                // 上
                if (c.y + 1 < height) TryVisit(c.x, c.y + 1, cd, ref q);
                // 下
                if (c.y - 1 >= 0) TryVisit(c.x, c.y - 1, cd, ref q);
            }

            q.Dispose();
        }

        void TryVisit(int x, int y, int cd, ref NativeQueue<int2> q)
        {
            int idx = y * width + x;
            if (blocked[idx] == 1) return;
            int nd = cd + 1;
            if (dist[idx] <= nd) return;
            dist[idx] = nd;
            q.Enqueue(new int2(x, y));
        }
    }

    [BurstCompile]
    struct DirectionFieldJob : IJob
    {
        public int width;
        public int height;

        [ReadOnly] public NativeArray<byte> blocked;
        [ReadOnly] public NativeArray<int> dist;
        public NativeArray<float2> flow;

        public void Execute()
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    if (blocked[idx] == 1)
                    {
                        flow[idx] = float2.zero;
                        continue;
                    }

                    int best = dist[idx];
                    float2 bestDir = float2.zero;

                    CheckDir(x + 1, y, 1, 0, ref best, ref bestDir);
                    CheckDir(x - 1, y, -1, 0, ref best, ref bestDir);
                    CheckDir(x, y + 1, 0, 1, ref best, ref bestDir);
                    CheckDir(x, y - 1, 0, -1, ref best, ref bestDir);
                    CheckDir(x + 1, y + 1, 1, 1, ref best, ref bestDir);
                    CheckDir(x + 1, y - 1, 1, -1, ref best, ref bestDir);
                    CheckDir(x - 1, y + 1, -1, 1, ref best, ref bestDir);
                    CheckDir(x - 1, y - 1, -1, -1, ref best, ref bestDir);

                    flow[idx] = bestDir;
                }
            }
        }

        void CheckDir(int nx, int ny, int dx, int dy, ref int best, ref float2 bestDir)
        {
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;

            int idx = ny * width + nx;
            int nd = dist[idx];
            if (nd >= best) return;

            best = nd;
            float invLen = math.rsqrt(dx * dx + dy * dy);
            bestDir = new float2(dx * invLen, dy * invLen);
        }
    }
}
