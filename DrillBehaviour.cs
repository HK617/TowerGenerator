using System.Collections;
using UnityEngine;

/// <summary>
/// ドリルの挙動:
/// ・足元に ResourceMarker があるときだけ稼働
/// ・前方に ConveyorBelt が接続されているときだけ生産開始
/// ・接続ベルトにアイテムがある間は生産しない（詰まり防止）
/// ・アイテムはドリル中心からスポーンし、コンベアブロック中心まで移動してからベルトに乗る
/// ・ゴースト状態（Collider2D がまだ有効化されていない間）は完全に停止
/// </summary>
[DisallowMultipleComponent]
public class DrillBehaviour : MonoBehaviour
{
    [Header("Production")]
    [Tooltip("生産間隔（秒）")]
    public float produceInterval = 3f;

    [Tooltip("生産するアイテムのプレハブ")]
    public GameObject productPrefab;

    [Header("Search")]
    [Tooltip("足元の ResourceMarker を探す半径")]
    public float resourceSearchRadius = 0.35f;

    [Tooltip("前方コンベアを探す半径")]
    public float beltSearchRadius = 0.25f;

    [Tooltip("資源判定用マスク")]
    public LayerMask resourceMask = ~0;

    [Tooltip("ベルト判定用マスク")]
    public LayerMask beltMask = ~0;

    [Header("Output")]
    [Tooltip("ドリルの前方方向（transform.up）にどれだけ離れた位置をコンベア接続位置とみなすか")]
    public float outputOffset = 0.25f;

    [Header("Move To Belt")]
    [Tooltip("ドリル中心 → ベルト中心 への移動速度（ワールド単位/秒）")]
    public float moveToBeltSpeed = 6f;

    ResourceMarker _linkedResource;
    float _timer;

    Collider2D[] _cols;
    bool _isPlaced;

    void Awake()
    {
        _cols = GetComponentsInChildren<Collider2D>(true);
    }

    void Start()
    {
        _isPlaced = HasAnyEnabledCollider();
        if (_isPlaced)
        {
            FindResourceBelow();
        }
    }

    void Update()
    {
        // ゴースト中は何もしない
        if (!_isPlaced)
        {
            if (HasAnyEnabledCollider())
            {
                _isPlaced = true;
                _timer = 0f;
                FindResourceBelow();
            }
            else
            {
                return;
            }
        }

        if (productPrefab == null) return;

        // 資源チェック
        if (_linkedResource == null)
        {
            FindResourceBelow();
            if (_linkedResource == null) return;
        }

        // 前方にベルトが無ければ生産しない
        Vector3 outPos = GetOutputProbePosition();
        ConveyorBelt frontBelt = FindFrontBelt(outPos);
        if (frontBelt == null) return;

        // そのベルト上にすでにアイテムがあるなら「詰まっている」とみなして生産停止
        if (IsBeltOccupied(frontBelt))
            return;

        _timer += Time.deltaTime;
        if (_timer < produceInterval) return;
        _timer = 0f;

        // ベルトが空いているときだけ、生産→ドリル中心からベルト中心へ移動→ベルトに乗せる
        StartCoroutine(ProduceAndMoveToBelt(frontBelt));
    }

    // ─────────────────────────────
    // ゴースト判定
    // ─────────────────────────────
    bool HasAnyEnabledCollider()
    {
        if (_cols == null) return false;
        foreach (var c in _cols)
        {
            if (c != null && c.enabled) return true;
        }
        return false;
    }

    // ─────────────────────────────
    // 足元の ResourceMarker 探し
    // ─────────────────────────────
    void FindResourceBelow()
    {
        _linkedResource = null;

        Vector2 center = transform.position;
        var hits = Physics2D.OverlapCircleAll(center, resourceSearchRadius, resourceMask);
        if (hits == null) return;

        foreach (var h in hits)
        {
            if (!h) continue;
            var marker = h.GetComponentInParent<ResourceMarker>();
            if (marker != null)
            {
                _linkedResource = marker;
                break;
            }
        }
    }

    // ─────────────────────────────
    // ドリル前方の「接続位置」（探査用）を取得
    // ─────────────────────────────
    Vector3 GetOutputProbePosition()
    {
        Vector2 outDir = transform.up.normalized;
        return transform.position + (Vector3)(outDir * outputOffset);
    }

    // ─────────────────────────────
    // 前方にある ConveyorBelt を探す
    // ─────────────────────────────
    ConveyorBelt FindFrontBelt(Vector3 pos)
    {
        var hits = Physics2D.OverlapCircleAll(pos, beltSearchRadius, beltMask);
        if (hits == null) return null;

        foreach (var h in hits)
        {
            if (!h) continue;
            var belt = h.GetComponentInParent<ConveyorBelt>();
            if (belt != null)
                return belt;
        }

        return null;
    }

    // ─────────────────────────────
    // 接続しているベルト上にアイテムが存在するか？
    // ─────────────────────────────
    bool IsBeltOccupied(ConveyorBelt belt)
    {
        if (belt == null) return false;

        // チェック位置: ベルトの itemSpawnPoint があればそこ、なければベルト中心
        Vector3 checkPos = belt.itemSpawnPoint != null
            ? belt.itemSpawnPoint.position
            : belt.transform.position;

        // 半径は「ベルト長さの1マス分」くらいを想定して少し広めに
        float r = Mathf.Max(beltSearchRadius, 0.15f);

        var hits = Physics2D.OverlapCircleAll(checkPos, r);
        if (hits == null || hits.Length == 0) return false;

        foreach (var h in hits)
        {
            if (!h) continue;
            // ItemOnBeltMover を持っているものを「ベルト上のアイテム」とみなす
            var mover = h.GetComponentInParent<ItemOnBeltMover>();
            if (mover != null)
            {
                // 「このベルトの上に乗っているアイテム」なら詰まり状態とみなす
                // （_currentBelt は private なので、近くにいるだけでOKとする）
                return true;
            }
        }

        return false;
    }

    // ─────────────────────────────
    // ドリル中心からベルト中心へ動かしてからベルトに乗せる
    // ─────────────────────────────
    IEnumerator ProduceAndMoveToBelt(ConveyorBelt belt)
    {
        if (productPrefab == null || belt == null) yield break;

        // 1) アイテムを「ドリルブロックの中心」にスポーン
        Vector3 start = transform.position;

        // 2) 目標位置は「コンベアブロックの中心 or itemSpawnPoint」
        Vector3 targetPos = belt.itemSpawnPoint != null
            ? belt.itemSpawnPoint.position
            : belt.transform.position;

        GameObject item = Instantiate(productPrefab, start, Quaternion.identity);

        // 3) ドリル中心 → ベルト中心 まで等速移動
        float dist = Vector3.Distance(start, targetPos);
        float speed = Mathf.Max(0.01f, moveToBeltSpeed);
        float duration = (dist <= 0.0001f) ? 0f : dist / speed;

        if (duration > 0f)
        {
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                if (t > 1f) t = 1f;
                item.transform.position = Vector3.Lerp(start, targetPos, t);
                yield return null;
            }
        }
        else
        {
            item.transform.position = targetPos;
        }

        // 4) ここでベルトアイテムとして初期化
        var mover = item.GetComponent<ItemOnBeltMover>();
        if (mover == null)
            mover = item.AddComponent<ItemOnBeltMover>();
        mover.Init(belt);

        // 5) 分配ベルト用の監視も付けておく
        var splitterWatcher = item.GetComponent<BeltItemSplitterWatcher>();
        if (splitterWatcher == null)
            splitterWatcher = item.AddComponent<BeltItemSplitterWatcher>();
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        // 資源検出範囲
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, resourceSearchRadius);

        // 前方のコンベア検出範囲
        Vector3 probePos = GetOutputProbePosition();
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(probePos, beltSearchRadius);

        // ドリル中心 → コンベア探査位置
        Gizmos.color = Color.green;
        Gizmos.DrawLine(transform.position, probePos);
    }
#endif
}