using UnityEngine;

/// <summary>
/// 単純なコンベアーベルト:
/// ・AddItem でアイテムを受け取り、ItemOnBeltMover にベルト情報を渡す
/// ・moveDirection は「このベルト上でのアイテムの進行方向」（ワールド座標）
///   ConveyorBeltAutoConnector から上書きされます。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider2D))]
public class ConveyorBelt : MonoBehaviour
{
    [Header("Move")]
    [Tooltip("アイテムを流すワールド方向。Awake で transform.up が入ります。")]
    public Vector2 moveDirection = Vector2.up;

    [Tooltip("アイテムの移動速度")]
    public float moveSpeed = 2f;

    [Header("Spawn")]
    [Tooltip("アイテムを出す位置。未指定ならベルトの中心")]
    public Transform itemSpawnPoint;

    [Tooltip("生成したアイテムをこのオブジェクトの子にするか")]
    public bool parentItemsToBelt = false;

    [Header("Belt Area")]
    [Tooltip("コンベアーとして判定するレイヤー（このレイヤーの Collider2D の上だけ動く）")]
    public LayerMask conveyorLayerMask;

    [HideInInspector]
    public Vector2 mainInDirectionWorld = Vector2.up;   // このベルトに入ってくる向き（中心に向かう移動方向）

    [HideInInspector]
    public Vector2 mainOutDirectionWorld = Vector2.up;  // このベルトから出ていく向き

    [HideInInspector]
    public bool isCornerBelt = false;                   // 入口と出口が90度に曲がっているベルトかどうか

    Collider2D _col;

    void Awake()
    {
        _col = GetComponent<Collider2D>();

        if (moveDirection == Vector2.zero)
            moveDirection = transform.up;

        if (conveyorLayerMask.value == 0)
            conveyorLayerMask = 1 << gameObject.layer;
    }

    void Reset()
    {
        moveDirection = Vector2.up;
        moveSpeed = 2f;
        conveyorLayerMask = 1 << gameObject.layer;
    }

    /// <summary>
    /// ドリルなどからアイテムを受け取る。
    /// </summary>
    public void AddItem(GameObject itemPrefab)
    {
        if (itemPrefab == null)
        {
            Debug.LogWarning("[ConveyorBelt] AddItem: itemPrefab が null", this);
            return;
        }

        Vector3 spawnPos = itemSpawnPoint != null
            ? itemSpawnPoint.position
            : transform.position;

        var item = Instantiate(itemPrefab, spawnPos, Quaternion.identity);

        if (parentItemsToBelt)
            item.transform.SetParent(transform, true);

        // ベルト移動制御
        var mover = item.GetComponent<ItemOnBeltMover>();
        if (mover == null)
            mover = item.AddComponent<ItemOnBeltMover>();
        mover.Init(this);

        // 分岐検知（分割ベルトの上に来たら 3 方向に流す）
        var splitterWatcher = item.GetComponent<BeltItemSplitterWatcher>();
        if (splitterWatcher == null)
            splitterWatcher = item.AddComponent<BeltItemSplitterWatcher>();
    }
}

/// <summary>
/// 「常に足元のコンベアーを調べて、そのコンベアーの向きに合わせて動く」
/// アイテム側の移動制御。
/// ベルトが変わった瞬間に、そのベルトの中心に一度スナップしてから進行方向を切り替えるので、
/// 曲がり角の真ん中で方向転換しているように見えます。
/// </summary>
public class ItemOnBeltMover : MonoBehaviour
{
    ConveyorBelt _currentBelt;
    LayerMask _beltMask;
    Rigidbody2D _rb;
    Collider2D _col;

    [Header("Anti-collision")]
    [Tooltip("前のアイテムから最低これだけ離れて停止（見た目サイズに合わせる）")]
    public float minGap = 0.18f;

    [Tooltip("キャスト時の安全マージン")]
    public float safety = 0.02f;

    [Tooltip("アイテム検出レイヤー（未設定なら自動で Item）")]
    public LayerMask itemMask = 0;

    // 足元のベルト検出用
    const float BeltDetectRadius = 0.08f;

    // 前方キャストで使うワーク配列
    readonly RaycastHit2D[] _castHits = new RaycastHit2D[16];

    // ConveyorBelt.AddItem / ConveyorDistributor から最初に呼ばれる
    public void Init(ConveyorBelt firstBelt)
    {
        _currentBelt = firstBelt;
        _beltMask = (firstBelt != null) ? firstBelt.conveyorLayerMask : ~0;

        _rb = GetComponent<Rigidbody2D>();
        if (_rb == null) _rb = gameObject.AddComponent<Rigidbody2D>();

        _rb.gravityScale = 0f;
        _rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

        _col = GetComponent<Collider2D>();
        if (_col == null)
        {
            var c = gameObject.AddComponent<CircleCollider2D>();
            c.isTrigger = true; // 物理押し出しは使わない
            c.radius = Mathf.Max(minGap * 0.5f, 0.10f);
            _col = c;
        }
        else
        {
            // アイテム同士は trigger にして自前ロジックで止める
            _col.isTrigger = true;
        }

        // itemMask が 0 のときは "Item" レイヤーを自動設定
        if (itemMask.value == 0)
        {
            int idx = LayerMask.NameToLayer("Item");
            if (idx >= 0) itemMask = 1 << idx;
        }

        // スポーン直後に重なっていた場合、少しだけ後ろへずらす
        ResolveInitialOverlap();
    }

    void FixedUpdate()
    {
        if (_rb == null) return;

        // まず「今どのベルトの上か」を更新
        UpdateCurrentBelt();

        if (_currentBelt == null)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector2.zero;
#else
            _rb.velocity = Vector2.zero;
#endif
            return;
        }

        // このフレームの進行方向（カーブベルトにも対応）
        Vector2 moveDir = GetMoveDirection();
        if (moveDir.sqrMagnitude < 1e-6f)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector2.zero;
#else
            _rb.velocity = Vector2.zero;
#endif
            return;
        }
        moveDir.Normalize();

        float speed = (_currentBelt.moveSpeed > 0f) ? _currentBelt.moveSpeed : 2f;
        float step = speed * Time.fixedDeltaTime;          // 予定移動距離
        float castDist = step + minGap;                    // ちょっと先までキャスト
        float allowed = step;                              // 実際に動ける距離

        // === 前方に他アイテムがあるかチェック ===
        int hitCount = CastAhead(moveDir, castDist);
        if (hitCount > 0)
        {
            float nearest = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                var h = _castHits[i];
                if (h.collider == null) continue;

                // 自分自身は無視
                if (h.rigidbody != null && h.rigidbody == _rb)
                    continue;

                var other = h.collider.GetComponentInParent<ItemOnBeltMover>();
                if (other == null || other == this)
                    continue;

                // ★ 同じベルト上のアイテムは、方向に関係なくブロック扱い
                if (other._currentBelt != null && _currentBelt != null &&
                    other._currentBelt == _currentBelt)
                {
                    float dSameBelt = h.distance;
                    if (dSameBelt < nearest)
                        nearest = dSameBelt;
                    continue; // 方向チェックはスキップ
                }

                // ↑以外（別ベルトなど）は「ほぼ同じ方向に動いているときだけ」ブロック
                Vector2 otherDir = other.GetMoveDirection();
                if (otherDir.sqrMagnitude > 0f)
                {
                    otherDir.Normalize();
                    float dot = Vector2.Dot(moveDir, otherDir);
                    if (dot < 0.2f)
                        continue;
                }

                float d = h.distance;
                if (d < nearest)
                    nearest = d;
            }

            if (nearest < float.MaxValue)
            {
                // 前のアイテムの safety 手前までしか動かない
                allowed = Mathf.Max(0f, nearest - safety);
                if (allowed > step) allowed = step;
            }
        }

        // === 実際に移動 ===
        Vector2 delta = moveDir * allowed;
        Vector2 newPos = _rb.position + delta;

#if UNITY_6000_0_OR_NEWER
        _rb.linearVelocity = delta / Time.fixedDeltaTime;
#else
        _rb.velocity = delta / Time.fixedDeltaTime;
#endif
        _rb.MovePosition(newPos);
    }

    // カーブベルト対応の進行方向取得
    public Vector2 GetMoveDirection()
    {
        if (_currentBelt == null) return Vector2.zero;

        if (_currentBelt.isCornerBelt)
        {
            Vector2 center = _currentBelt.transform.position;
            Vector2 inMove = _currentBelt.mainInDirectionWorld;
            Vector2 outMove = _currentBelt.mainOutDirectionWorld;

            if (outMove.sqrMagnitude < 1e-6f)
                outMove = _currentBelt.moveDirection;
            if (inMove.sqrMagnitude < 1e-6f)
                inMove = -outMove;

            inMove.Normalize();
            outMove.Normalize();

            // ベルトの中心より「入口側」にいるうちは inMove、「出口側」に来たら outMove
            Vector2 toPos = (Vector2)transform.position - center;
            float dotIn = Vector2.Dot(toPos, inMove);
            return (dotIn < 0f) ? inMove : outMove;
        }
        else
        {
            Vector2 dir = _currentBelt.mainOutDirectionWorld;
            if (dir.sqrMagnitude < 1e-6f)
                dir = _currentBelt.moveDirection;
            return dir.normalized;
        }
    }

    // 自分のコライダーを進行方向に Cast して、前方のアイテムを拾う
    int CastAhead(Vector2 dir, float distance)
    {
        if (_col != null)
        {
            var filter = new ContactFilter2D
            {
                useLayerMask = true,
                layerMask = itemMask,
                useTriggers = true
            };
            return _col.Cast(dir.normalized, filter, _castHits, distance);
        }

        // コライダーが無い場合の保険
        return Physics2D.RaycastNonAlloc(_rb.position, dir.normalized, _castHits, distance, itemMask);
    }

    // 「今足元にあるベルト」を決める
    void UpdateCurrentBelt()
    {
        Vector2 pos = transform.position;
        Collider2D[] hits = Physics2D.OverlapCircleAll(pos, BeltDetectRadius, _beltMask);

        bool onCurrent = false;
        ConveyorBelt nearest = null;
        float nearestSqr = float.MaxValue;

        if (hits != null)
        {
            foreach (var h in hits)
            {
                if (!h) continue;
                var b = h.GetComponentInParent<ConveyorBelt>();
                if (!b) continue;

                if (b == _currentBelt)
                    onCurrent = true;

                float d2 = ((Vector2)b.transform.position - pos).sqrMagnitude;
                if (d2 < nearestSqr)
                {
                    nearestSqr = d2;
                    nearest = b;
                }
            }
        }

        // 足元のベルトが変わったときだけ差し替え
        if (!onCurrent)
            _currentBelt = nearest;
    }

    // スポーン／合流直後で既に重なっている場合、進行方向の逆へ少しずつ退避
    void ResolveInitialOverlap()
    {
        if (_col == null) return;

        var filter = new ContactFilter2D
        {
            useLayerMask = true,
            layerMask = itemMask,
            useTriggers = true
        };

        Collider2D[] cols = new Collider2D[16];
        int count = _col.Overlap(filter, cols);
        if (count <= 0) return;

        Vector2 dir = GetMoveDirection();
        if (dir.sqrMagnitude < 1e-6f)
            dir = Random.insideUnitCircle.normalized;

        float step = Mathf.Max(minGap * 0.5f, 0.10f);

        const int maxIters = 8;
        for (int i = 0; i < maxIters; i++)
        {
            int c = _col.Overlap(filter, cols);
            if (c <= 0) break;

            // 少しだけ後ろに下がる
            _rb.position -= dir * (step * 0.5f);
        }
    }
}
