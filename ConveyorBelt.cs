using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 完全ロジック制御のコンベアーベルト（中心円チェックなし）
///
/// ・Rigidbody / Collider の衝突には依存しない
/// ・各アイテムは「ベルト上の距離 distance (0～beltLength)」で管理
/// ・minItemSpacing 以上の間隔を必ず保つ（距離ベース）
/// ・outputs に登録された次ベルトへラウンドロビンで押し出す
/// ・isCornerBelt のときは 入口→ItemSpawnPoint→出口 を通る 2次 Bézier 曲線で曲げる
/// ・アイテムの回転は変更しない（見た目の角度そのまま）
/// ・アイテムごとに inDirection（どの方向から入ってきたか）を記録し、
///   左から来たアイテムは「左→中心→上」、右から来たアイテムは「右→中心→上」のように曲がる
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider2D))]
public class ConveyorBelt : MonoBehaviour
{
    [Header("Move Settings")]
    [Tooltip("アイテムを流すワールド方向（出口方向）。通常は AutoConnector が設定。")]
    public Vector2 moveDirection = Vector2.up;

    [Tooltip("このベルトタイルの中心から見て、入口中心～出口中心までの距離。")]
    public float beltLength = 0.25f;

    [Tooltip("1秒間に何枚ぶん進むか（1 なら1秒で1枚）。")]
    public float speedTilesPerSecond = 4f;

    [Header("Spacing")]
    [Tooltip("ベルト方向の距離で、アイテム同士がこれ未満には近づかない。")]
    public float minItemSpacing = 0.20f;

    [Header("Item Spawn / Visual")]
    [Tooltip("ベルト中心（カーブの中心にも使う）。null の場合は transform.position。")]
    public Transform itemSpawnPoint;

    [Tooltip("生成したアイテムをこのベルトの子にするか。")]
    public bool parentItemsToBelt = true;

    [Header("Connections")]
    [Tooltip("このベルトから出ていく先のベルト。複数あればラウンドロビンで分配。")]
    public ConveyorBelt[] outputs;

    [Header("Debug / Neighbor Info (AutoConnector 用)")]
    [Tooltip("AutoConnector が設定する、本線の入口方向（ワールド）。")]
    public Vector2 mainInDirectionWorld;
    [Tooltip("AutoConnector が設定する、本線の出口方向（ワールド）。通常は moveDirection と一致。")]
    public Vector2 mainOutDirectionWorld;
    [Tooltip("AutoConnector が設定するコーナーベルトフラグ。")]
    public bool isCornerBelt;

    // =====================================================================
    // 内部データ
    // =====================================================================

    class LaneItem
    {
        public GameObject go;
        public float distance;      // 入口側からの距離（0 ～ beltLength）
        public Vector2 inDirection; // このベルトに入ってきた方向
    }

    readonly List<LaneItem> _items = new List<LaneItem>();
    int _nextOutputIndex = 0;

    float WorldSpeed => Mathf.Max(0f, speedTilesPerSecond) * beltLength;

    // =====================================================================
    // ライフサイクル
    // =====================================================================

    void OnEnable()
    {
        FixDirections();
        BeltLogicSystem.Register(this);
    }

    void OnDisable()
    {
        BeltLogicSystem.Unregister(this);
    }

    void Reset()
    {
        moveDirection = Vector2.up;
        mainOutDirectionWorld = moveDirection;
        beltLength = 0.25f;
        speedTilesPerSecond = 4f;
        minItemSpacing = 0.20f;
    }

    void FixDirections()
    {
        if (moveDirection.sqrMagnitude < 0.0001f)
            moveDirection = Vector2.up;
        moveDirection.Normalize();

        if (mainOutDirectionWorld.sqrMagnitude < 0.0001f)
            mainOutDirectionWorld = moveDirection;

        if (mainInDirectionWorld.sqrMagnitude < 0.0001f)
            mainInDirectionWorld = -mainOutDirectionWorld;

        // 入口と出口が90度近いときは「コーナー」とみなす保険
        float dot = Vector2.Dot(mainInDirectionWorld.normalized, mainOutDirectionWorld.normalized);
        if (Mathf.Abs(dot) < 0.1f)
        {
            isCornerBelt = true;
        }
    }

    // =====================================================================
    // 外部 API
    // =====================================================================

    /// <summary>
    /// Prefab を生成してベルトに載せる（ドリルなどから使用）。
    /// </summary>
    public bool TryAddItemPrefab(GameObject itemPrefab)
    {
        if (itemPrefab == null) return false;
        if (!HasFreeInputSpace()) return false;

        Vector2 inDir = mainInDirectionWorld.sqrMagnitude > 0.0001f
            ? mainInDirectionWorld.normalized
            : (-moveDirection);

        Vector3 spawnPos = itemSpawnPoint != null ? itemSpawnPoint.position : transform.position;
        var go = Instantiate(itemPrefab, spawnPos, Quaternion.identity);
        return InternalAccept(go, 0f, inDir);
    }

    /// <summary>
    /// 他コンポーネントから既存 GameObject を受け取る従来 API。
    /// </summary>
    public bool TryAcceptFromUpstream(GameObject item)
    {
        if (item == null) return false;
        if (!HasFreeInputSpace()) return false;

        Vector2 inDir = mainInDirectionWorld.sqrMagnitude > 0.0001f
            ? mainInDirectionWorld.normalized
            : (-moveDirection);

        return InternalAccept(item, 0f, inDir);
    }

    /// <summary>
    /// 上流ベルトが分かっている場合はこちら（合流時など）。
    /// </summary>
    public bool TryAcceptFromUpstream(GameObject item, ConveyorBelt fromBelt)
    {
        if (item == null) return false;
        if (!HasFreeInputSpace()) return false;

        Vector2 inDir;
        if (fromBelt != null)
        {
            Vector2 diff = (Vector2)(transform.position - fromBelt.transform.position);
            inDir = (diff.sqrMagnitude < 0.0001f)
                ? (mainInDirectionWorld.sqrMagnitude > 0.0001f
                    ? mainInDirectionWorld.normalized
                    : -moveDirection)
                : diff.normalized;
        }
        else
        {
            inDir = mainInDirectionWorld.sqrMagnitude > 0.0001f
                ? mainInDirectionWorld.normalized
                : (-moveDirection);
        }

        return InternalAccept(item, 0f, inDir);
    }

    /// <summary>
    /// ベルト入口付近に新しいアイテムを置けるだけの距離的な空きがあるか。
    /// </summary>
    public bool HasFreeInputSpace()
    {
        if (_items.Count == 0) return true;

        float nearest = float.MaxValue;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].distance < nearest)
                nearest = _items[i].distance;
        }

        return nearest >= minItemSpacing;
    }

    /// <summary>
    /// 先頭アイテムを取り出す（クラフター用など）。
    /// </summary>
    public bool TryExtractLast(out GameObject item)
    {
        item = null;
        if (_items.Count == 0) return false;

        int frontIndex = -1;
        float max = float.MinValue;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].distance > max)
            {
                max = _items[i].distance;
                frontIndex = i;
            }
        }

        if (frontIndex < 0) return false;

        var front = _items[frontIndex];
        item = front.go;
        _items.RemoveAt(frontIndex);

        if (parentItemsToBelt && item != null && item.transform.parent == transform)
            item.transform.SetParent(null, true);

        return true;
    }

    // =====================================================================
    // 内部ヘルパー
    // =====================================================================

    bool InternalAccept(GameObject item, float distance, Vector2 inDir)
    {
        // ベルトに乗ったら物理は殺す
        var rb = item.GetComponent<Rigidbody2D>();
        if (rb != null) Destroy(rb);
        var cols = item.GetComponentsInChildren<Collider2D>();
        foreach (var c in cols) c.enabled = false;

        if (parentItemsToBelt)
            item.transform.SetParent(transform, true);

        LaneItem li = new LaneItem
        {
            go = item,
            distance = Mathf.Clamp(distance, 0f, beltLength),
            inDirection = inDir.sqrMagnitude > 0.0001f ? inDir.normalized : (-mainOutDirectionWorld.normalized)
        };
        _items.Add(li);

        UpdateItemTransform(li);
        return true;
    }

    // =====================================================================
    // ベルトロジック更新（BeltLogicSystem から呼ばれる）
    // =====================================================================

    public void TickLogic(float dt)
    {
        FixDirections();
        if (_items.Count == 0) return;

        float speed = WorldSpeed;
        if (speed <= 0f)
        {
            // 動かないベルト：見た目だけ整える
            for (int i = 0; i < _items.Count; i++)
                UpdateItemTransform(_items[i]);
            return;
        }

        // 先頭→後ろの順で distance の大きい順にソート
        _items.Sort((a, b) => b.distance.CompareTo(a.distance));

        // 先頭から順に移動（前との距離を minItemSpacing 以上に保つ）
        for (int i = 0; i < _items.Count; i++)
        {
            LaneItem li = _items[i];

            float maxPos = beltLength;

            if (i > 0)
            {
                float frontPos = _items[i - 1].distance;
                maxPos = Mathf.Min(maxPos, frontPos - minItemSpacing);
            }

            if (maxPos < 0f) maxPos = 0f;

            float target = li.distance + speed * dt;
            li.distance = Mathf.Clamp(target, 0f, maxPos);

            // 誤差で minItemSpacing を割り込んだ場合の補正
            if (i > 0)
            {
                float frontPos = _items[i - 1].distance;
                if (frontPos - li.distance < minItemSpacing)
                {
                    li.distance = frontPos - minItemSpacing;
                    if (li.distance < 0f) li.distance = 0f;
                }
            }
        }

        // 先頭が端まで来ていたら、次のベルトへ押し出す
        if (_items.Count > 0)
        {
            LaneItem front = _items[0];
            if (front.distance >= beltLength - 1e-4f)
            {
                if (TryPushToOutputs(front))
                {
                    _items.RemoveAt(0);
                }
                else
                {
                    // 下流が詰まっている場合、端で待機
                    front.distance = beltLength - 1e-4f;
                }
            }
        }

        // 見た目更新
        for (int i = 0; i < _items.Count; i++)
            UpdateItemTransform(_items[i]);
    }

    bool TryPushToOutputs(LaneItem laneItem)
    {
        if (laneItem == null || laneItem.go == null) return false;
        if (outputs == null || outputs.Length == 0) return false;

        int count = outputs.Length;
        int start = _nextOutputIndex;

        for (int i = 0; i < count; i++)
        {
            int idx = (start + i) % count;
            ConveyorBelt outBelt = outputs[idx];
            if (outBelt == null) continue;

            // 下流ベルトの入口方向 = 下流中心 - 自分の中心
            Vector2 inDir = (Vector2)(outBelt.transform.position - transform.position);
            if (inDir.sqrMagnitude < 0.0001f)
                inDir = -outBelt.mainOutDirectionWorld.normalized;
            else
                inDir = inDir.normalized;

            if (!outBelt.HasFreeInputSpace()) continue;

            if (outBelt.InternalAccept(laneItem.go, 0f, inDir))
            {
                _nextOutputIndex = (idx + 1) % count;
                return true;
            }
        }

        return false;
    }

    // =====================================================================
    // 見た目（パス上の位置計算）
    // =====================================================================

    void UpdateItemTransform(LaneItem li)
    {
        if (li == null || li.go == null) return;

        Vector3 pos = GetPositionOnPath(li.distance, li.inDirection);
        li.go.transform.position = pos;

        // ★ 回転は一切いじらない（角度そのまま）
    }

    Vector3 GetPositionOnPath(float distance, Vector2 inDir)
    {
        float t = 0f;
        if (beltLength > 1e-5f)
            t = Mathf.Clamp01(distance / beltLength);

        // このアイテム専用の入口方向
        Vector2 dirIn = inDir.sqrMagnitude > 0.0001f
            ? inDir.normalized
            : mainInDirectionWorld.normalized;

        Vector3 pIn = transform.position - (Vector3)(dirIn * (beltLength * 0.5f));

        // 出口方向はベルト共通
        Vector2 dirOut = mainOutDirectionWorld.sqrMagnitude > 0.0001f
            ? mainOutDirectionWorld.normalized
            : moveDirection.normalized;
        Vector3 pOut = transform.position + (Vector3)(dirOut * (beltLength * 0.5f));

        if (!isCornerBelt)
        {
            // 直線：入口→出口を線形補間
            return Vector3.Lerp(pIn, pOut, t);
        }
        else
        {
            // コーナー：入口→中心→出口 を通る 2次 Bézier
            Vector3 center = itemSpawnPoint != null ? itemSpawnPoint.position : transform.position;
            return QuadraticBezier(pIn, center, pOut, t);
        }
    }

    static Vector3 QuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }
}

/// <summary>
/// シーン内のすべての ConveyorBelt をまとめて更新するマネージャ。
/// </summary>
public class BeltLogicSystem : MonoBehaviour
{
    static BeltLogicSystem _instance;
    readonly List<ConveyorBelt> _belts = new List<ConveyorBelt>();

    public static void Register(ConveyorBelt belt)
    {
        if (belt == null) return;
        if (_instance == null)
        {
            var go = new GameObject("BeltLogicSystem");
            _instance = go.AddComponent<BeltLogicSystem>();
        }
        if (!_instance._belts.Contains(belt))
            _instance._belts.Add(belt);
    }

    public static void Unregister(ConveyorBelt belt)
    {
        if (_instance == null || belt == null) return;
        _instance._belts.Remove(belt);
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Update()
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < _belts.Count; i++)
        {
            ConveyorBelt belt = _belts[i];
            if (belt == null) continue;
            belt.TickLogic(dt);
        }
    }
}
