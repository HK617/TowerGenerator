using UnityEngine;

/// <summary>
/// コンベアーの周囲を調べて、
/// 隣接する ConveyorBelt / DrillBehaviour を検出し、
/// 13種類のスプライトパターンと moveDirection を自動設定する。
/// 
/// 変更点:
/// - 隣接探索はワールドの上下左右のみ（斜め誤ヒット防止）
/// - HasNeighborWorld で「ベルトの向き」がその方向とほぼ平行な場合だけ接続とみなす
///   → 平行に並べただけのベルト同士は接続しない
/// - in/out 判定:
///   ・out は基本1方向のみ
///   ・接続が1方向しかない場合は「その1方向を in、out はローカル上方向」にする
///     → Din_Rout / Din_Lout で横を壊しても下→上の直線としてつながりを維持
/// - autoRefresh で一定間隔ごとに再計算
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(ConveyorBelt))]
[RequireComponent(typeof(SpriteRenderer))]
public class ConveyorBeltAutoConnector : MonoBehaviour
{
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

    ConveyorBelt _belt;
    SpriteRenderer _sr;
    float _timer;

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

    /// <summary>
    /// 周囲を調べて接続パターンを更新。
    /// propagateToNeighbors = true にすると近隣ベルトにも再計算を伝播する。
    /// </summary>
    public void RecalculatePattern(bool propagateToNeighbors)
    {
        if (_belt == null || _sr == null) return;

        Vector3 pos = transform.position;

        // 1) ワールド座標の上下左右で隣接チェック（向きも見る）
        bool hasWorldUp = HasNeighborWorld(pos, Vector2.up);
        bool hasWorldRight = HasNeighborWorld(pos, Vector2.right);
        bool hasWorldDown = HasNeighborWorld(pos, Vector2.down);
        bool hasWorldLeft = HasNeighborWorld(pos, Vector2.left);

        // 2) ワールド方向を「このコンベアーのローカル U/R/D/L」に変換
        bool localUpHas = false;
        bool localRightHas = false;
        bool localDownHas = false;
        bool localLeftHas = false;

        if (hasWorldUp) MarkLocalSide(Vector2.up, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldRight) MarkLocalSide(Vector2.right, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldDown) MarkLocalSide(Vector2.down, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldLeft) MarkLocalSide(Vector2.left, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);

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
            // 接続1方向だけ:
            // その1方向を in とみなし、out は必ずローカル上(=transform.up)にする
            if (localUpHas) inU = true;
            if (localRightHas) inR = true;
            if (localDownHas) inD = true;
            if (localLeftHas) inL = true;

            outU = true;
        }
        else
        {
            // 接続2方向以上:
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

        // 6) 近隣ベルトにも伝播（必要なら）
        if (propagateToNeighbors)
        {
            PropagateToNeighbors(pos);
        }
    }

    /// <summary>
    /// ワールド方向 dirWorld で隣にコンベアーorドリルがあるか？
    /// ベルトの場合は「そのベルトの向き」が dirWorld とほぼ平行なときだけ接続とみなす。
    /// （平行に並べただけのベルトは無視）
    /// </summary>
    bool HasNeighborWorld(Vector3 basePos, Vector2 dirWorld)
    {
        Vector2 center = (Vector2)basePos + dirWorld.normalized * cellSize;
        var hits = Physics2D.OverlapCircleAll(center, probeRadius, searchMask);
        if (hits == null || hits.Length == 0) return false;

        Vector2 dir = dirWorld.normalized;

        foreach (var h in hits)
        {
            if (!h) continue;

            // ドリルは向き関係なく常に接続扱い
            if (h.GetComponentInParent<DrillBehaviour>() != null)
                return true;

            var belt = h.GetComponentInParent<ConveyorBelt>();
            if (belt != null && belt != _belt)
            {
                Vector2 otherUp = belt.transform.up;
                otherUp.Normalize();
                float dot = Mathf.Abs(Vector2.Dot(otherUp, dir));

                // 45度以内ぐらいを「平行」とみなす（cos 45° ≒ 0.707）
                if (dot >= 0.7f)
                    return true;
            }
        }
        return false;
    }

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
