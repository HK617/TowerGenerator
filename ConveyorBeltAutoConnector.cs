using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// コンベアーの周囲を調べて、
/// 隣接する ConveyorBelt / DrillBehaviour を検出し、
/// 13種類のスプライトパターンと moveDirection を自動設定する。
/// 
/// NeighborInfo 方式 + 「本線」優先 + 「in 向きは1本だけ」ルール:
/// - 1方向ごとに NeighborInfo を計算して、
///   ・平行な横並びなら接続しない（最初の横並び問題対策）
///   ・向きが90°違う場合:
///       - 相手の out が自分に向いていれば接続
///       - そうでなくても、自分に前後の本線があれば接続 (T字の枝)
///       - それ以外（本線なし＋outTowardMe false）は接続しない
///   ・それ以外（直列/普通のカーブなど）は接続
/// - ドリルもコンベアーと同じルールで、transform.up を出力方向として扱う
/// - 自分に out を向けている隣接コンベアーが複数ある場合は、
///   InstanceID が最大（＝あとに生成されたとみなせる）ものだけを有効にし、
///   それ以外は「存在しない」扱いにして、
///   「自分に向かう out ラインは常に 1 本だけ」にする
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ConveyorBelt))]
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

    // 隣方向ごとの情報
    struct NeighborInfo
    {
        public bool exists;
        public bool isDrill;

        public bool isSideParallel;
        public bool isOrthogonal;
        public bool isParallelMain;

        public bool outTowardMe;   // 相手の out が自分に向いている (自分の in 候補)
        public bool inTowardMe;    // 相手の in が自分に向いている (自分の out 候補)

        public int instanceId;
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
            return (v.x > 0f) ? Vector2.right : Vector2.left;
        else
            return (v.y > 0f) ? Vector2.up : Vector2.down;
    }

    Vector2 Opposite(Vector2 d) => -d;

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
        if (hits == null || hits.Length == 0) return info;   // exists=false のまま

        Vector2 selfDir = QuantizeDir(transform.up);  // 自分の out 方向
        Vector2 offsetDir = QuantizeDir(dirWorld);      // 自分→相手 方向（グリッド方向）

        foreach (var h in hits)
        {
            if (!h) continue;

            var drill = h.GetComponentInParent<DrillBehaviour>();
            var belt = h.GetComponentInParent<ConveyorBelt>();

            if (drill == null && (belt == null || belt == _belt))
                continue;

            info.exists = true;

            GameObject rootGO;
            Vector2 otherDir;
            if (drill != null)
            {
                info.isDrill = true;
                rootGO = drill.gameObject;
                otherDir = QuantizeDir(drill.transform.up);   // ドリルの出力方向
            }
            else
            {
                info.isDrill = false;
                rootGO = belt.gameObject;
                otherDir = QuantizeDir(belt.transform.up);    // ベルトの出力方向
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

            // 本線(前後)かどうか
            info.isParallelMain = parallel && !info.isSideParallel;

            // 相手の out が自分に向いているか？
            // 自分→相手 が offsetDir なので、その逆が「自分の方向」
            // 相手の out が自分に向いているか？（= 相手から自分へ流れてくる）
            info.outTowardMe = (otherDir == Opposite(offsetDir));

            // 相手の in が自分に向いているか？（= 自分から相手へ流せる）
            info.inTowardMe = (otherDir == offsetDir);

            break; // 最初に見つけた1つだけ見る
        }

        return info;
    }

    // -----------------------
    // メイン処理
    // -----------------------

    /// <summary>
    /// 周囲を調べて接続パターンを更新。
    /// propagateToNeighbors = true にすると近隣ベルトにも再計算を伝播する。
    /// </summary>
    public void RecalculatePattern(bool propagateToNeighbors)
    {
        if (_belt == null || _sr == null) return;

        Vector3 pos = transform.position;

        // 1) ワールド座標の上下左右で NeighborInfo を取得
        NeighborInfo neighborUp = GetNeighborInfo(pos, Vector2.up);
        NeighborInfo neighborRight = GetNeighborInfo(pos, Vector2.right);
        NeighborInfo neighborDown = GetNeighborInfo(pos, Vector2.down);
        NeighborInfo neighborLeft = GetNeighborInfo(pos, Vector2.left);

        // 「自分に in を向けている隣」（= 自分の out 先になり得る隣）が
        // 複数ある場合は、最後に置かれた（InstanceID が最大）の 1 本だけ残し、
        // それ以外は存在しない扱いにする → 自分の out 方向は必ず 1 本だけになる
        {
            NeighborInfo[] arr = { neighborUp, neighborRight, neighborDown, neighborLeft };
            int bestIndex = -1;
            int bestId = int.MinValue;

            for (int i = 0; i < 4; i++)
            {
                if (!arr[i].exists) continue;
                if (!arr[i].inTowardMe) continue;

                if (arr[i].instanceId > bestId)
                {
                    bestId = arr[i].instanceId;
                    bestIndex = i;
                }
            }

            if (bestIndex != -1)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!arr[i].exists) continue;
                    if (!arr[i].inTowardMe) continue;
                    if (i == bestIndex) continue;

                    // それ以外の「自分に in を向けている隣」は無視する
                    arr[i].exists = false;
                }

                neighborUp = arr[0];
                neighborRight = arr[1];
                neighborDown = arr[2];
                neighborLeft = arr[3];
            }
        }


        // このコンベアーにとって「本線（前後方向）の接続」が存在するか？
        bool hasParallelMain =
            (neighborUp.exists && neighborUp.isParallelMain) ||
            (neighborRight.exists && neighborRight.isParallelMain) ||
            (neighborDown.exists && neighborDown.isParallelMain) ||
            (neighborLeft.exists && neighborLeft.isParallelMain);

        // NeighborInfo から「接続しているか？」を決めるローカル関数
        bool Connected(NeighborInfo n)
        {
            if (!n.exists) return false;
            if (n.isSideParallel) return false;   // 平行な横並びは必ず接続しない

            if (n.isOrthogonal)
            {
                // 90°違う向き:
                // 1) 相手の out が自分に向いているなら接続
                if (n.outTowardMe) return true;

                // 2) 自分に前後の本線があるなら、
                //    そこに枝としてつなぐことを許可（T字, 3本交差など）
                if (hasParallelMain) return true;

                // 3) 本線もなく、outTowardMe も false のときだけ接続しない
                return false;
            }

            // それ以外（直列や普通のカーブなど）は接続扱い
            return true;
        }

        bool hasWorldUp = Connected(neighborUp);
        bool hasWorldRight = Connected(neighborRight);
        bool hasWorldDown = Connected(neighborDown);
        bool hasWorldLeft = Connected(neighborLeft);

        // 2) ワールド方向を「このコンベアーのローカル U/R/D/L」に変換
        bool localUpHas = false;
        bool localRightHas = false;
        bool localDownHas = false;
        bool localLeftHas = false;

        if (hasWorldUp)
            MarkLocalSide(Vector2.up, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldRight)
            MarkLocalSide(Vector2.right, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldDown)
            MarkLocalSide(Vector2.down, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldLeft)
            MarkLocalSide(Vector2.left, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);

        // 3) in / out を決める
        bool inU = false, inR = false, inD = false, inL = false;
        bool outU = false, outR = false, outD = false, outL = false;

        int connectedCount =
            (localUpHas ? 1 : 0) +
            (localRightHas ? 1 : 0) +
            (localDownHas ? 1 : 0) +
            (localLeftHas ? 1 : 0);

        if (connectedCount == 0)
        {
            // どこともつながっていない → とりあえずローカル上向きに流すだけ
            outU = true;
        }
        else if (connectedCount == 1)
        {
            // 1方向だけつながっている:
            // その1方向を in とみなし、out は必ずローカル上(=transform.up)にする
            if (localUpHas) inU = true;
            if (localRightHas) inR = true;
            if (localDownHas) inD = true;
            if (localLeftHas) inL = true;

            outU = true;
        }
        else
        {
            // 2方向以上つながっている:
            // out は「前向き（transform.up）」に一番近い方向を選ぶ
            Vector2 forward = transform.up.normalized;
            float bestDot = -999f;

            // Up
            if (localUpHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.up).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outU = true; outR = outD = outL = false;
                }
            }
            // Right
            if (localRightHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.right).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outR = true; outU = outD = outL = false;
                }
            }
            // Down
            if (localDownHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.down).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outD = true; outU = outR = outL = false;
                }
            }
            // Left
            if (localLeftHas)
            {
                float dot = Vector2.Dot(LocalToWorldDir(Vector2.left).normalized, forward);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    outL = true; outU = outR = outD = false;
                }
            }

            // out 以外でつながっている方向は全部 in
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
            float dot = Vector2.Dot(localInMove.normalized, localOutMove.normalized);
            if (Mathf.Abs(dot) < 0.1f)
                isCorner = true;
        }
        _belt.isCornerBelt = isCorner;

        // moveDirection は出口方向
        _belt.moveDirection = worldOutMove.normalized;

        // ★ 追加：出口方向にいるベルトを outputs に登録
        UpdateOutputsFromMoveDirection(pos);

        // 6) 近隣ベルトにも伝播（必要なら）
        if (propagateToNeighbors)
        {
            PropagateToNeighbors(pos);
        }
    }

    // -----------------------
    // ローカル方向関連
    // -----------------------

    /// <summary>
    /// ワールド方向を、このコンベアーのローカル U/R/D/L のどれに一番近いかで分類する。
    /// </summary>
    void MarkLocalSide(
        Vector2 worldDir,
        ref bool hasU, ref bool hasR, ref bool hasD, ref bool hasL)
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

        switch (side)
        {
            case 0: hasU = true; break;
            case 1: hasR = true; break;
            case 2: hasD = true; break;
            case 3: hasL = true; break;
        }
    }

    Vector2 LocalToWorldDir(Vector2 local)
    {
        return (Vector2)(transform.right * local.x + transform.up * local.y);
    }

    void PropagateToNeighbors(Vector3 basePos)
    {
        Vector2[] dirs = { Vector2.up, Vector2.right, Vector2.down, Vector2.left };
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

    /// <summary>
    /// このベルトの moveDirection 方向にある隣接ベルトを探して
    /// ConveyorBelt.outputs に登録する。
    /// （とりあえず「前方1マス」のみ。直線・カーブはこれでOK）
    /// </summary>
    void UpdateOutputsFromMoveDirection(Vector3 basePos)
    {
        if (_belt == null) return;

        Vector2 dir = _belt.moveDirection;
        if (dir.sqrMagnitude < 0.0001f) return;
        dir.Normalize();

        // 前方1マスの中心を調べる
        Vector2 center = (Vector2)basePos + dir * cellSize;
        var hits = Physics2D.OverlapCircleAll(center, probeRadius, searchMask);
        if (hits == null || hits.Length == 0)
        {
            _belt.outputs = null;
            return;
        }

        List<ConveyorBelt> list = new List<ConveyorBelt>();

        foreach (var h in hits)
        {
            if (!h) continue;
            var belt = h.GetComponentInParent<ConveyorBelt>();
            if (belt != null && belt != _belt && !list.Contains(belt))
            {
                list.Add(belt);
            }
        }

        _belt.outputs = list.Count > 0 ? list.ToArray() : null;
    }

    // -----------------------
    // スプライト選択
    // -----------------------

    Sprite GetPatternSprite(bool inU, bool inR, bool inD, bool inL,
                            bool outU, bool outR, bool outD, bool outL)
    {
        // out が複数立つケースは基本無い想定

        if (inR && outU && !inL && !inU && !inD) return sprite_Rin_Uout;
        if (inL && outU && !inR && !inU && !inD) return sprite_Lin_Uout;
        if (inL && inR && outU && !inU && !inD) return sprite_LRin_Uout;
        if (inD && outU && !inL && !inR && !inU) return sprite_Din_Uout;
        if (inL && inR && inD && outU) return sprite_LRDin_Uout;
        if (inR && inD && outU) return sprite_RDin_Uout;
        if (inL && inD && outU) return sprite_LDin_Uout;
        if (inD && outR) return sprite_Din_Rout;
        if (inD && outL) return sprite_Din_Lout;

        // 以下は out が複数立つ想定のパターン（通常は使用されない）
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
