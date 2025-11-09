using UnityEngine;

/// <summary>
/// コンベアーの周囲を調べて、
/// 隣接する ConveyorBelt / DrillBehaviour を検出し、
/// 13種類のスプライトパターンと moveDirection を自動設定する。
/// 
/// 修正版:
/// - 隣接探索はワールドの上下左右固定（斜め誤検出防止）
/// - ワールド方向→ローカルU/R/D/L の変換を Dot 計算で行い、
///   コンベアーの角度に正しく追従する。
/// - 13個のスプライト構成はそのまま維持。
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

    ConveyorBelt _belt;
    SpriteRenderer _sr;

    void Awake()
    {
        _belt = GetComponent<ConveyorBelt>();
        _sr = GetComponent<SpriteRenderer>();
    }

    void Start()
    {
        // 起動時に1回だけ自分を更新
        RecalculatePattern(false);
    }

    /// <summary>
    /// 周囲を調べて接続パターンを更新。
    /// propagateToNeighbors = true にすると、近隣ベルトにも再計算を伝播する。
    /// </summary>
    public void RecalculatePattern(bool propagateToNeighbors)
    {
        if (_belt == null || _sr == null) return;

        Vector3 pos = transform.position;

        // 1) ワールド座標の上下左右で隣接チェック
        bool hasWorldUp = HasNeighborWorld(pos, Vector2.up);
        bool hasWorldRight = HasNeighborWorld(pos, Vector2.right);
        bool hasWorldDown = HasNeighborWorld(pos, Vector2.down);
        bool hasWorldLeft = HasNeighborWorld(pos, Vector2.left);

        // 2) ワールド方向を「このコンベアーのローカル U/R/D/L」に変換
        bool inU = false, inR = false, inD = false, inL = false;
        bool outU = false, outR = false, outD = false, outL = false;

        // ローカル方向フラグ
        bool localUpHas = false;
        bool localRightHas = false;
        bool localDownHas = false;
        bool localLeftHas = false;

        if (hasWorldUp) MarkLocalSide(Vector2.up, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldRight) MarkLocalSide(Vector2.right, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldDown) MarkLocalSide(Vector2.down, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);
        if (hasWorldLeft) MarkLocalSide(Vector2.left, ref localUpHas, ref localRightHas, ref localDownHas, ref localLeftHas);

        // 3) ローカル接続を in/out に分類
        if (localDownHas)
        {
            inD = true;

            if (localUpHas) outU = true;
            if (localRightHas) outR = true;
            if (localLeftHas) outL = true;
        }
        else if (localUpHas)
        {
            outU = true;
            if (localRightHas) inR = true;
            if (localLeftHas) inL = true;
        }
        else
        {
            if (localRightHas) outR = true;
            if (localLeftHas && !localRightHas) outL = true;
        }

        // 4) 13パターンに応じてスプライト選択
        Sprite chosen = GetPatternSprite(inU, inR, inD, inL, outU, outR, outD, outL);
        _sr.sprite = chosen ?? defaultSprite;

        // 5) ローカルでの「中心に向かう入口方向」と「出口方向」を決める
        //    （inX は「どの辺から入ってくるか」なので、移動方向はその逆になる）
        Vector2 localInMove = Vector2.zero;
        if (inD) localInMove = Vector2.up;    // 下辺から → 上向きに中心へ
        else if (inU) localInMove = Vector2.down;  // 上辺から → 下向きに中心へ
        else if (inR) localInMove = Vector2.left;  // 右辺から → 左向きに中心へ
        else if (inL) localInMove = Vector2.right; // 左辺から → 右向きに中心へ

        Vector2 localOutMove = Vector2.up;         // デフォルトは上向き
        if (outU) localOutMove = Vector2.up;
        else if (outD) localOutMove = Vector2.down;
        else if (outR) localOutMove = Vector2.right;
        else if (outL) localOutMove = Vector2.left;

        // ローカル → ワールドに変換
        Vector2 worldInMove = LocalToWorldDir(localInMove);
        Vector2 worldOutMove = LocalToWorldDir(localOutMove);

        // コンベアーに保存しておく
        _belt.mainInDirectionWorld = worldInMove.normalized;
        _belt.mainOutDirectionWorld = worldOutMove.normalized;

        // 入口と出口が90度なら「コーナー」とみなす（直線は false）
        bool hasIn = localInMove != Vector2.zero;
        bool hasOut = localOutMove != Vector2.zero;
        bool isCorner = false;
        if (hasIn && hasOut)
        {
            float dot = Vector2.Dot(localInMove.normalized, localOutMove.normalized);
            // ほぼ直線(1 or -1)ではなく、90度近い場合だけコーナー扱い
            if (Mathf.Abs(dot) < 0.1f)
                isCorner = true;
        }
        _belt.isCornerBelt = isCorner;

        // 6) moveDirection は「出口方向」で決める
        _belt.moveDirection = worldOutMove.normalized;

        // 6) 近隣ベルトにも伝播（必要なら）
        if (propagateToNeighbors)
        {
            PropagateToNeighbors(pos);
        }
    }

    /// <summary>
    /// ワールド方向 dirWorld で隣にコンベアーorドリルがあるか？
    /// </summary>
    bool HasNeighborWorld(Vector3 basePos, Vector2 dirWorld)
    {
        Vector2 center = (Vector2)basePos + dirWorld.normalized * cellSize;
        var hits = Physics2D.OverlapCircleAll(center, probeRadius, searchMask);
        if (hits == null || hits.Length == 0) return false;

        foreach (var h in hits)
        {
            if (!h) continue;
            if (h.GetComponentInParent<ConveyorBelt>() != null && h.GetComponentInParent<ConveyorBelt>() != _belt)
                return true;
            if (h.GetComponentInParent<DrillBehaviour>() != null)
                return true;
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
        if (inR && outU && !inL && !inU && !inD) return sprite_Rin_Uout;
        if (inL && outU && !inR && !inU && !inD) return sprite_Lin_Uout;
        if (inL && inR && outU && !inU && !inD) return sprite_LRin_Uout;
        if (inD && outU && !inL && !inR && !inU) return sprite_Din_Uout;
        if (inL && inR && inD && outU) return sprite_LRDin_Uout;
        if (inR && inD && outU) return sprite_RDin_Uout;
        if (inL && inD && outU) return sprite_LDin_Uout;
        if (inD && outR) return sprite_Din_Rout;
        if (inD && outL) return sprite_Din_Lout;
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
        Gizmos.DrawWireSphere(c + Vector2.up * cellSize, probeRadius);
        Gizmos.DrawWireSphere(c + Vector2.right * cellSize, probeRadius);
        Gizmos.DrawWireSphere(c + Vector2.down * cellSize, probeRadius);
        Gizmos.DrawWireSphere(c + Vector2.left * cellSize, probeRadius);
    }
#endif
}
