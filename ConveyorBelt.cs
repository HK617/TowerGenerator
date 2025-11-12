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

    // ベルト検出
    const float BeltDetectRadius = 0.08f;

    // Cast 用のワーク領域（GC回避）
    readonly RaycastHit2D[] _castHits = new RaycastHit2D[8];

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
            c.isTrigger = true; // 物理押し出しを使わない
            c.radius = Mathf.Max(minGap * 0.5f, 0.10f);
            _col = c;
        }
        else
        {
            _col.isTrigger = true;
        }

        if (itemMask.value == 0)
        {
            int idx = LayerMask.NameToLayer("Item");
            if (idx >= 0) itemMask = 1 << idx;
        }

        // 初期重なりを解消（スポーンや合流直後のめり込み対策）
        ResolveInitialOverlap();
    }

    void FixedUpdate()
    {
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

        Vector2 dir = GetMoveDirection();
        if (dir.sqrMagnitude < 1e-6f)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector2.zero;
#else
            _rb.velocity = Vector2.zero;
#endif
            return;
        }

        float speed = (_currentBelt.moveSpeed > 0f) ? _currentBelt.moveSpeed : 2f;

        // 1ステップで進む予定距離
        float step = speed * Time.fixedDeltaTime;

        // ★ 前方スイープ：進行予定距離 + minGap をキャストして、最短ヒットまでで停止
        float allowed = step;
        float castDist = step + minGap;

        int hitCount = CastAhead(dir, castDist, _castHits);
        if (hitCount > 0)
        {
            float nearest = float.MaxValue;
            for (int i = 0; i < hitCount; i++)
            {
                var h = _castHits[i];
                if (!h.collider) continue;

                // 自分自身は無視
                if (_col != null && h.collider == _col) continue;

                // 同じベルト上のアイテムを優先的にブロック扱い
                var other = h.collider.GetComponentInParent<ItemOnBeltMover>();
                if (other != null)
                {
                    // 同一ベルト or 不明 → ブロック
                    if (other._currentBelt == _currentBelt || other._currentBelt == null)
                    {
                        nearest = Mathf.Min(nearest, h.distance);
                    }
                }
                else
                {
                    // アイテム以外（万一）にも近づきすぎない
                    nearest = Mathf.Min(nearest, h.distance);
                }
            }

            if (nearest < float.MaxValue)
            {
                // nearest はキャスト開始点からの距離。minGap 分はクリアに必要なので引く
                allowed = Mathf.Max(0f, nearest - safety);
                if (allowed > step) allowed = step; // 念のためクランプ
            }
        }

        if (allowed <= 1e-4f)
        {
            // 停止（見た目スムーズ用に速度を0）
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = Vector2.zero;
#else
            _rb.velocity = Vector2.zero;
#endif
            return;
        }

        Vector2 delta = dir * allowed;

        // MovePositionで正確に移動（Interpolateで見た目なめらか）
        _rb.MovePosition(_rb.position + delta);

        // 速度は任意（UI等で使うなら設定）
#if UNITY_6000_0_OR_NEWER
        _rb.linearVelocity = dir * speed;
#else
        _rb.velocity = dir * speed;
#endif
    }

    // 進行方向（カーブは“中心を跨いだら出口方向”）
    Vector2 GetMoveDirection()
    {
        if (_currentBelt.isCornerBelt)
        {
            Vector2 center = _currentBelt.transform.position;
            Vector2 inMove = _currentBelt.mainInDirectionWorld;
            Vector2 outMove = _currentBelt.mainOutDirectionWorld;
            if (inMove.sqrMagnitude < 1e-6f) inMove = -outMove;
            inMove.Normalize(); outMove.Normalize();

            Vector2 toPos = (Vector2)transform.position - center;
            float dotIn = Vector2.Dot(toPos, inMove);
            return (dotIn < 0f) ? inMove : outMove;
        }
        else
        {
            Vector2 dir = _currentBelt.mainOutDirectionWorld;
            if (dir.sqrMagnitude < 1e-6f) dir = _currentBelt.moveDirection;
            return dir.normalized;
        }
    }

    // Collider/Rigidbody Cast（Trigger 同士でも可視）
    int CastAhead(Vector2 dir, float distance, RaycastHit2D[] results)
    {
        // Collider2D.Cast の方が形状を正しく使える
        if (_col != null)
        {
            var filter = new ContactFilter2D
            {
                useLayerMask = true,
                layerMask = itemMask,
                useTriggers = true
            };
            return _col.Cast(dir.normalized, filter, results, distance);
        }

        // 予備（Colliderが無い場合）
        return Physics2D.RaycastNonAlloc(_rb.position, dir.normalized, results, distance, itemMask);
    }

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

                if (b == _currentBelt) onCurrent = true;

                float d2 = ((Vector2)b.transform.position - pos).sqrMagnitude;
                if (d2 < nearestSqr)
                {
                    nearestSqr = d2;
                    nearest = b;
                }
            }
        }

        if (!onCurrent) _currentBelt = nearest;
    }

    // スポーンや合流で既に重なっていたら、進行方向の反対へわずかに退避
    void ResolveInitialOverlap()
    {
        if (_col == null) return;

        var filter = new ContactFilter2D
        {
            useLayerMask = true,
            layerMask = itemMask,
            useTriggers = true
        };

        Collider2D[] cols = new Collider2D[8];
        int count = _col.Overlap(filter, cols);
        if (count <= 0) return;

        // 現在の進行方向がわからなければ軽くランダム退避
        Vector2 dir = GetMoveDirection();
        if (dir.sqrMagnitude < 1e-6f)
            dir = Random.insideUnitCircle.normalized;

        const int maxIters = 6;
        float step = Mathf.Max(minGap * 0.5f, 0.10f);

        for (int i = 0; i < maxIters; i++)
        {
            int c = _col.Overlap(filter, cols);
            if (c <= 0) break;
            _rb.position -= dir * (step * 0.5f); // 少し後退
        }
    }
}
