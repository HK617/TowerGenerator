using UnityEngine;

public class TurretShooter2D : MonoBehaviour
{
    [Header("Targeting")]
    public float range = 6f;
    public float fireRate = 2f;                // 発/秒
    public LayerMask enemyLayer;               // Enemy レイヤー
    public bool rotateToTarget = true;
    public float rotateSpeedDegPerSec = 720f;  // 角速度[deg/秒]

    [Header("Bullet")]
    public Projectile2D bulletPrefab;
    public Transform firePoint;
    public bool bulletHoming = false;
    public float bulletDamage = 5f;

    [Header("Visual")]
    public Transform graphics;                 // 見た目を回すトランスフォーム

    [Header("Build state (ゴースト判定)")]
    [Tooltip("コライダーが有効かどうかをチェックする間隔（秒）")]
    public float builtCheckInterval = 0.2f;

    float _cooldown;
    bool _isBuilt;
    float _nextBuiltCheckTime;

    Collider2D[] _cols2D;
    Collider[] _cols3D;

    void Awake()
    {
        if (!firePoint) firePoint = transform;
        if (!graphics) graphics = transform;

        // 自分＋子どもの Collider 一覧をキャッシュ
        _cols2D = GetComponentsInChildren<Collider2D>(true);
        _cols3D = GetComponentsInChildren<Collider>(true);
    }

    void Update()
    {
        // 1) まだ「完成していない建物」なら何もしない（プレビュー・ゴースト含む）
        if (!CheckBuiltState()) return;

        // 2) 敵を探す
        var target = FindNearestEnemyInRange();
        if (!target)
        {
            _cooldown = 0f;
            return;
        }

        Vector3 from = firePoint ? firePoint.position : transform.position;
        Vector3 to = target.position;
        Vector2 dir = (to - from).normalized;

        // 3) 見た目の回転（毎フレーム、角度/秒ベース）
        if (rotateToTarget && graphics)
        {
            float cur = graphics.eulerAngles.z;
            float tgt = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            float maxStep = rotateSpeedDegPerSec * Time.deltaTime; // ← ここが「角度で計算」
            float next = Mathf.MoveTowardsAngle(cur, tgt, maxStep);
            graphics.rotation = Quaternion.Euler(0f, 0f, next);
        }

        // 4) 射撃クールダウン
        _cooldown -= Time.deltaTime;
        if (_cooldown <= 0f)
        {
            Shoot(dir, target);
            _cooldown = (fireRate > 0f) ? (1f / fireRate) : 0.5f;
        }
    }

    // ==== ゴースト判定 ====
    bool CheckBuiltState()
    {
        if (Time.time >= _nextBuiltCheckTime)
        {
            _nextBuiltCheckTime = Time.time + builtCheckInterval;
            _isBuilt = HasAnyEnabledCollider();
        }
        return _isBuilt;
    }

    bool HasAnyEnabledCollider()
    {
        if (_cols2D != null)
        {
            foreach (var c in _cols2D)
                if (c && c.enabled) return true;
        }
        if (_cols3D != null)
        {
            foreach (var c in _cols3D)
                if (c && c.enabled) return true;
        }
        // 1つも有効コライダーが無ければ「まだゴースト」
        return false;
    }

    // ==== 敵探索 ====
    Transform FindNearestEnemyInRange()
    {
        var hits = Physics2D.OverlapCircleAll(transform.position, range, enemyLayer);
        Transform best = null;
        float bestSqr = float.PositiveInfinity;

        foreach (var h in hits)
        {
            if (!h || !h.gameObject.activeInHierarchy) continue;

            var enemy = h.GetComponent<EnemyChaseBase2D>();
            if (!enemy || enemy.IsDead) continue;

            float d2 = (h.transform.position - transform.position).sqrMagnitude;
            if (d2 < bestSqr)
            {
                bestSqr = d2;
                best = h.transform;
            }
        }

        return best;
    }

    // ==== 射撃 ====
    void Shoot(Vector2 dir, Transform target)
    {
        if (!bulletPrefab) return;

        var spawnPos = firePoint ? firePoint.position : transform.position;
        var proj = Instantiate(bulletPrefab, spawnPos, Quaternion.identity);

        proj.damage = bulletDamage;
        // 追尾も不要なので false 固定
        proj.destroyOnHit = true;

        // ★ タレットの今の向きに沿って飛ばす
        proj.FireInCurrentForward(graphics ? graphics : transform);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, range);
    }
}
