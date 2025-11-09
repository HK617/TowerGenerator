using UnityEngine;

/// <summary>
/// ドリルの挙動:
/// ・足元に ResourceMarker があるときだけ稼働
/// ・前方に ConveyorBelt が接続されているときだけ生産開始
/// ・一定間隔ごとに productPrefab を生産し、前方のベルトに直接 AddItem する
/// ・ゴースト状態（Collider2D がまだ有効化されていない間）は完全に停止
/// </summary>
[DisallowMultipleComponent]
public class DrillBehaviour : MonoBehaviour
{
    [Header("Production")]
    public float produceInterval = 3f;
    public GameObject productPrefab;

    [Header("Search")]
    public float resourceSearchRadius = 0.35f;
    public float beltSearchRadius = 0.25f;
    public LayerMask resourceMask = ~0;
    public LayerMask beltMask = ~0;

    [Header("Output")]
    public float outputOffset = 0.25f;

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
        Vector3 spawnPos = GetOutputPosition();
        ConveyorBelt frontBelt = FindFrontBelt(spawnPos);
        if (frontBelt == null) return;

        _timer += Time.deltaTime;
        if (_timer < produceInterval) return;
        _timer = 0f;

        // ベルトがあるときだけ AddItem で流す
        frontBelt.AddItem(productPrefab);
    }

    bool HasAnyEnabledCollider()
    {
        if (_cols == null) return false;
        foreach (var c in _cols)
        {
            if (c != null && c.enabled) return true;
        }
        return false;
    }

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

    Vector3 GetOutputPosition()
    {
        Vector2 outDir = transform.up.normalized;
        return transform.position + (Vector3)(outDir * outputOffset);
    }

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

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, resourceSearchRadius);

        Vector3 spawnPos = GetOutputPosition();
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(spawnPos, beltSearchRadius);

        Gizmos.color = Color.green;
        Gizmos.DrawLine(transform.position, spawnPos);
    }
#endif
}
