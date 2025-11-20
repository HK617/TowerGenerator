using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// コンベアーの周囲を調べて、
/// 隣接する ConveyorBelt / DrillBehaviour を検出し、
/// 13種類のスプライトパターンと moveDirection を自動設定する。
/// 
/// NeighborInfo 方式 + 「本線」優先:
/// - 1方向ごとに NeighborInfo を計算して、
///   ・平行な横並びなら接続しない（最初の横並び問題対策）
///   ・向きが90°違う場合:
///       - 相手の out が自分に向いていれば接続
///       - そうでなくても、自分に前後の本線があれば接続 (T字の枝)
///       - それ以外（本線なし＋outTowardMe false）は接続しない
///   ・それ以外（直列/普通のカーブなど）は接続
/// 
/// ★ 出力方向について:
///   - 「自分に in を向けている隣のベルト」は、すべて出力候補とみなして
///     ConveyorBelt.outputs に全登録する
///   - ConveyorBelt 側で outputs をラウンドロビンしてアイテムを分配する
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class ConveyorBeltAutoConnector : MonoBehaviour
{
    // -----------------------
    // 設定項目
    // -----------------------
    [Header("Search Settings")]
    [Tooltip("隣を調べる距離（Fineグリッド1マスが0.25なら0.25推奨）")]
    public float cellSize = 0.25f;

    [Tooltip("探索の円半径。少し小さめで斜め誤ヒット防止")]
    public float probeRadius = 0.12f;

    [Tooltip("探索対象のレイヤー（Machine レイヤーなど）")]
    public LayerMask searchMask = ~0;

    [Header("13 Pattern Sprites")]
    public Sprite sprite_Rin_Uout;
    public Sprite sprite_Lin_Uout;
    public Sprite sprite_LRin_Uout;
    public Sprite sprite_Din_Uout;
    public Sprite sprite_LRDin_Uout;
    public Sprite sprite_RDin_Uout;
    public Sprite sprite_LDin_Uout;
    public Sprite sprite_Din_Rout;
    public Sprite sprite_Din_Lout;
    public Sprite sprite_Din_LRout;
    public Sprite sprite_Din_LRUout;
    public Sprite sprite_Din_URout;
    public Sprite sprite_Din_ULout;

    [Header("Fallback Sprite")]
    public Sprite defaultSprite;

    [Header("Auto Refresh")]
    [Tooltip("true のとき、一定時間ごとに周囲を再チェックして見た目を更新します")]
    public bool autoRefresh = true;

    [Tooltip("自動再計算の間隔（秒）")]
    public float refreshInterval = 0.3f;

    // -----------------------
    // 内部状態
    // -----------------------
    ConveyorBelt _belt;
    SpriteRenderer _sr;
    float _timer;

    struct NeighborInfo
    {
        public bool exists;
        public bool isBelt;
        public bool isDrill;

        public bool isParallelMain; // 自分にとって前後方向にあるか？

        public bool outTowardMe;   // 相手の out が自分に向いている (= 相手から自分への流れ)
        public bool inTowardMe;    // 相手の in が自分に向いている (= 自分から相手への流れ)

        public bool isSideParallel; // 横並びの平行かどうか
        public bool isOrthogonal;   // 直交しているかどうか

        public int instanceId;      // どっちが「新しい」かを比較するため

        public ConveyorBelt beltRef; // 隣が ConveyorBelt のときの参照
    }

    void Awake()
    {
        _belt = GetComponent<ConveyorBelt>();
        _sr = GetComponent<SpriteRenderer>();
    }

    void Start()
    {
        RecalculatePattern(false);
        _timer = Random.Range(0f, refreshInterval);
    }

    void Update()
    {
        if (!autoRefresh) return;

        _timer += Time.deltaTime;
        if (_timer >= refreshInterval)
        {
            _timer = 0f;
            RecalculatePattern(false);
        }
    }

    // -----------------------
    // 方向ユーティリティ
    // -----------------------

    // 4方向に量子化（上下左右のどれかに丸める）
    Vector2 QuantizeDir(Vector2 v)
    {
        if (v.sqrMagnitude < 1e-6f) return Vector2.up;
        v.Normalize();
        float ax = Mathf.Abs(v.x);
        float ay = Mathf.Abs(v.y);
        if (ax > ay)
            return v.x >= 0 ? Vector2.right : Vector2.left;
        else
            return v.y >= 0 ? Vector2.up : Vector2.down;
    }

    Vector2 Opposite(Vector2 d) => -d;

    /// <summary>世界方向をローカル U/R/D/L のどれに一番近いかで 0..3 (U,R,D,L) に変換</summary>
    int WorldDirToLocalIndex(Vector2 worldDir)
    {
        worldDir.Normalize();
        Vector2 u = transform.up.normalized;
        Vector2 r = transform.right.normalized;
        Vector2 d = -u;
        Vector2 l = -r;

        float du = Vector2.Dot(worldDir, u);
        float dr = Vector2.Dot(worldDir, r);
        float dd = Vector2.Dot(worldDir, d);
        float dl = Vector2.Dot(worldDir, l);

        float max = du;
        int side = 0; // 0:U 1:R 2:D 3:L

        if (dr > max) { max = dr; side = 1; }
        if (dd > max) { max = dd; side = 2; }
        if (dl > max) { max = dl; side = 3; }

        return side;
    }

    // -----------------------
    // NeighborInfo の取得
    // -----------------------

    /// <summary>
    /// basePos から dirWorld 方向に 1 マス先を調べて NeighborInfo を返す。
    /// Drill も ConveyorBelt も同じルールで扱う。
    /// </summary>
    NeighborInfo GetNeighborInfo(Vector3 basePos, Vector2 dirWorld)
    {
        NeighborInfo info = new NeighborInfo();

        Vector2 center = (Vector2)basePos + dirWorld.normalized * cellSize;
        var hits = Physics2D.OverlapCircleAll(center, probeRadius, searchMask);
        if (hits == null || hits.Length == 0) return info; // exists=false のまま

        // 自分から見た向き
        Vector2 selfDir = QuantizeDir(transform.up);
        Vector2 offsetDir = QuantizeDir(dirWorld);

        // 何か見つかるまでループ
        foreach (var h in hits)
        {
            if (!h) continue;

            var belt = h.GetComponentInParent<ConveyorBelt>();
            var drill = h.GetComponentInParent<DrillBehaviour>();

            if (belt == null && drill == null) continue;
            if (belt != null && belt == _belt) continue; // 自分自身は除外

            info.exists = true;

            GameObject rootGO;
            Vector2 otherDir;

            if (belt != null)
            {
                info.isBelt = true;
                info.isDrill = false;
                rootGO = belt.gameObject;
                otherDir = QuantizeDir(belt.transform.up);
                info.beltRef = belt;
            }
            else
            {
                info.isDrill = true;
                info.isBelt = false;
                rootGO = drill.gameObject;
                otherDir = QuantizeDir(drill.transform.up);
                info.beltRef = null;
            }

            info.instanceId = rootGO.GetInstanceID();

            // 自分と相手の向きが平行か？
            bool parallel = (otherDir == selfDir) || (otherDir == Opposite(selfDir));
            // 直交か？（Quantize 済みなので dot は -1,0,1 のどれか）
            bool orth = !parallel && (Vector2.Dot(otherDir, selfDir) == 0f);
            info.isOrthogonal = orth;

            // 平行な横並びか？（最初の「横並び問題」用）
            if (parallel)
            {
                // offsetDir が自分の前後方向なら直列、それ以外なら横並び
                bool offsetIsForwardOrBack =
                    (offsetDir == selfDir) || (offsetDir == Opposite(selfDir));
                info.isSideParallel = !offsetIsForwardOrBack;
            }
            else
            {
                info.isSideParallel = false;
            }

            // この隣が「本線（前後方向）」かどうか
            info.isParallelMain =
                (offsetDir == selfDir) || (offsetDir == Opposite(selfDir));

            // 相手の out が自分に向いているか？（= 相手からこちらへ流れて来る）
            // out 方向は otherDir、N→S が otherDir のとき S は自分、S-N は -offsetDir
            info.outTowardMe = (otherDir == Opposite(offsetDir));

            // 相手の in が自分に向いているか？（= 自分から相手へ流せる）
            // in 方向は -otherDir。N→S が -otherDir のとき、S は自分、S-N は -offsetDir
            // → -otherDir == -offsetDir → otherDir == offsetDir
            info.inTowardMe = (otherDir == offsetDir);

            break;
        }

        return info;
    }

    // -----------------------
    // メイン処理
    // -----------------------

    public void RecalculatePattern(bool propagateToNeighbors)
    {
        if (_belt == null) _belt = GetComponent<ConveyorBelt>();
        if (_sr == null) _sr = GetComponent<SpriteRenderer>();

        Vector3 pos = transform.position;

        // 1) 4方向の NeighborInfo 取得
        NeighborInfo neighborUp = GetNeighborInfo(pos, Vector2.up);
        NeighborInfo neighborRight = GetNeighborInfo(pos, Vector2.right);
        NeighborInfo neighborDown = GetNeighborInfo(pos, Vector2.down);
        NeighborInfo neighborLeft = GetNeighborInfo(pos, Vector2.left);

        // このコンベアーにとって「本線（前後方向）の接続」が存在するか？
        bool hasParallelMain =
            (neighborUp.exists && neighborUp.isParallelMain) ||
            (neighborRight.exists && neighborRight.isParallelMain) ||
            (neighborDown.exists && neighborDown.isParallelMain) ||
            (neighborLeft.exists && neighborLeft.isParallelMain);

        // 「見た目として接続するか？」ロジック
        bool Connected(NeighborInfo n)
        {
            if (!n.exists) return false;
            if (n.isSideParallel) return false;

            if (n.isOrthogonal)
            {
                if (n.outTowardMe) return true;
                if (hasParallelMain) return true;
                return false;
            }

            return true;
        }

        bool hasWorldUp = Connected(neighborUp);
        bool hasWorldRight = Connected(neighborRight);
        bool hasWorldDown = Connected(neighborDown);
        bool hasWorldLeft = Connected(neighborLeft);

        // 2) 世界座標の U/R/D/L を、このベルトのローカルU/R/D/Lにマッピング
        bool localUpHas = false, localRightHas = false, localDownHas = false, localLeftHas = false;

        void MarkLocalSide(Vector2 worldDir, ref bool hasU, ref bool hasR, ref bool hasD, ref bool hasL)
        {
            int idx = WorldDirToLocalIndex(worldDir);
            switch (idx)
            {
                case 0: hasU = true; break;
                case 1: hasR = true; break;
                case 2: hasD = true; break;
                case 3: hasL = true; break;
            }
        }

        if (hasWorldUp) MarkLocalSide(Vector2.up, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldRight) MarkLocalSide(Vector2.right, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldDown) MarkLocalSide(Vector2.down, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldLeft) MarkLocalSide(Vector2.left, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);

        // 3) ローカル接続から in/out を決める
        bool inU = false, inR = false, inD = false, inL = false;
        bool outU = false, outR = false, outD = false, outL = false;

        int connectionCount =
            (localUpHas ? 1 : 0) +
            (localRightHas ? 1 : 0) +
            (localDownHas ? 1 : 0) +
            (localLeftHas ? 1 : 0);

        if (connectionCount == 0)
        {
            // 単独ベルト
            outU = true;
        }
        else if (connectionCount == 1)
        {
            // デッドエンド: 1本から入ってきて上に抜ける
            if (localUpHas) inU = true;
            if (localRightHas) inR = true;
            if (localDownHas) inD = true;
            if (localLeftHas) inL = true;

            outU = true;
        }
        else
        {
            // 2方向以上つながっている:
            // out は「前向き（transform.up）」に一番近い方向をメイン out とする
            Vector2 forward = transform.up;
            float bestDot = -999f;

            if (localUpHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.up).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outU = true; outR = outD = outL = false;
                }
            }
            if (localRightHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.right).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outR = true; outU = outD = outL = false;
                }
            }
            if (localDownHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.down).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outD = true; outU = outR = outL = false;
                }
            }
            if (localLeftHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.left).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outL = true; outU = outR = outD = false;
                }
            }

            // in は「out 以外でつながっている方向」を全部 in とする
            if (localUpHas && !outU) inU = true;
            if (localRightHas && !outR) inR = true;
            if (localDownHas && !outD) inD = true;
            if (localLeftHas && !outL) inL = true;
        }

        // 4) スプライトを決定
        Sprite chosen = GetPatternSprite(inU, inR, inD, inL, outU, outR, outD, outL);
        _sr.sprite = chosen ?? defaultSprite;

        // 5) ローカルでの入口/出口ベクトルを決定
        Vector2 localInMove = Vector2.zero;
        Vector2 localOutMove = Vector2.up; // デフォルト

        if (inD) localInMove = Vector2.up;
        else if (inU) localInMove = Vector2.down;
        else if (inR) localInMove = Vector2.left;
        else if (inL) localInMove = Vector2.right;

        if (outU) localOutMove = Vector2.up;
        else if (outD) localOutMove = Vector2.down;
        else if (outR) localOutMove = Vector2.right;
        else if (outL) localOutMove = Vector2.left;

        // ローカル → ワールド
        Vector2 worldInMove = LocalToWorldDir(localInMove);
        Vector2 worldOutMove = LocalToWorldDir(localOutMove);

        _belt.mainInDirectionWorld = worldInMove.normalized;
        _belt.mainOutDirectionWorld = worldOutMove.normalized;

        // コーナー判定（入口と出口が90度ならコーナー）
        bool hasIn = localInMove != Vector2.zero;
        bool hasOut = localOutMove != Vector2.zero;
        bool isCorner = false;
        if (hasIn && hasOut)
        {
            float dot = Vector2.Dot(worldInMove.normalized, worldOutMove.normalized);
            if (Mathf.Abs(dot) < 0.1f)
                isCorner = true;
        }
        _belt.isCornerBelt = isCorner;

        // moveDirection は「メイン出口方向」
        _belt.moveDirection = worldOutMove.normalized;

        // 6) 出力先ベルト(複数)を決定
        List<ConveyorBelt> outputs = new List<ConveyorBelt>();

        void TryAddOutputFromNeighbor(NeighborInfo n)
        {
            if (!n.exists) return;
            if (!n.isBelt || n.beltRef == null) return;
            if (!n.inTowardMe) return;        // 相手の in がこちらを向いていない → こちらからは流せない
            if (!Connected(n)) return;        // 見た目上も接続している方向だけに限定

            if (!outputs.Contains(n.beltRef))
                outputs.Add(n.beltRef);
        }

        TryAddOutputFromNeighbor(neighborUp);
        TryAddOutputFromNeighbor(neighborRight);
        TryAddOutputFromNeighbor(neighborDown);
        TryAddOutputFromNeighbor(neighborLeft);

        _belt.outputs = outputs.Count > 0 ? outputs.ToArray() : null;

        // 7) 近隣ベルトにも伝播（必要なら）
        if (propagateToNeighbors)
        {
            PropagateToNeighbors(pos);
        }
    }

    Vector2 LocalToWorldDir(Vector2 local)
    {
        return (Vector2)(transform.right * local.x + transform.up * local.y);
    }

    // -----------------------
    // 近隣への伝播
    // -----------------------

    void PropagateToNeighbors(Vector3 basePos)
    {
        Vector2[] dirs =
        {
            Vector2.up,
            Vector2.right,
            Vector2.down,
            Vector2.left
        };

        foreach (var d in dirs)
        {
            Vector2 center = (Vector2)basePos + d.normalized * cellSize;
            var hits = Physics2D.OverlapCircleAll(center, probeRadius, searchMask);
            if (hits == null || hits.Length == 0) continue;

            foreach (var h in hits)
            {
                if (!h) continue;
                var belt = h.GetComponentInParent<ConveyorBelt>();
                if (belt == null || belt == _belt) continue;
                var connector = belt.GetComponent<ConveyorBeltAutoConnector>();
                if (connector != null)
                {
                    connector.RecalculatePattern(false);
                }
            }
        }
    }

    // -----------------------
    // スプライト選択
    // -----------------------

    Sprite GetPatternSprite(bool inU, bool inR, bool inD, bool inL,
                        bool outU, bool outR, bool outD, bool outL)
    {
        // ================================
        // ① T字（入力2本＋出力1本）を 最優先で判定
        // ================================

        // 下 + 左 から入ってきて 右 に出る（DinL Rout）
        // → このパターン用のスプライトとして sprite_Din_LRout を使う
        if (inD && inL && outR && !outU && !outD && !outL)
            return sprite_Din_LRout;

        // （必要になったら、下+右→左 などもここに追加できます）

        // ================================
        // ② それ以外は従来通りの判定
        // ================================

        // 上向きに出る系
        if (inR && outU && !inL && !inU && !inD) return sprite_Rin_Uout;
        if (inL && outU && !inR && !inU && !inD) return sprite_Lin_Uout;
        if (inL && inR && outU && !inU && !inD) return sprite_LRin_Uout;
        if (inD && outU && !inL && !inR && !inU) return sprite_Din_Uout;
        if (inL && inR && inD && outU) return sprite_LRDin_Uout;
        if (inR && inD && outU) return sprite_RDin_Uout;
        if (inL && inD && outU) return sprite_LDin_Uout;

        // 横方向に出る（入力1本）系
        if (inD && outR) return sprite_Din_Rout;
        if (inD && outL) return sprite_Din_Lout;

        // 出力が複数の変則パターン
        if (inD && outL && outR) return sprite_Din_LRout;
        if (inD && outL && outR && outU) return sprite_Din_LRUout;
        if (inD && outU && outR) return sprite_Din_URout;
        if (inD && outU && outL) return sprite_Din_ULout;

        return defaultSprite;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Vector2 c = transform.position;
        Gizmos.DrawWireSphere(c + Vector2.up    * cellSize, probeRadius);
        Gizmos.DrawWireSphere(c + Vector2.right * cellSize, probeRadius);
        Gizmos.DrawWireSphere(c + Vector2.down  * cellSize, probeRadius);
        Gizmos.DrawWireSphere(c + Vector2.left  * cellSize, probeRadius);
    }
#endif
}
