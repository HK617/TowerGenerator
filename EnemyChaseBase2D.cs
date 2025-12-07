using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class EnemyChaseBase2D : MonoBehaviour
{
    [Header("Flow")]
    public FlowField025 flowField;     // シーン上の FlowField を割り当て

    [Header("Move")]
    public float moveSpeed = 3.5f;
    public bool lookAtDir = true;
    public float rotateSpeed = 720f;   // [deg/秒]

    [Header("HP")]
    public float maxHP = 20f;
    public bool destroyOnDeath = true;

    [Header("Hit / FX")]
    public float knockbackForce = 0f;          // >0 なら被弾ノックバック
    public GameObject deathVfxPrefab;          // 死亡エフェクト（任意）
    public float deathVfxLife = 2f;

    [Header("Attack")]
    [Tooltip("1 回の攻撃で与えるダメージ")]
    public float attackDamage = 5f;

    [Tooltip("攻撃間隔（秒）")]
    public float attackInterval = 1.0f;

    [Tooltip("攻撃対象として認識する Base の Tag 名")]
    public string baseTag = "Base";

    float _attackTimer = 0f;

    Rigidbody2D _rb;
    float _hp;
    bool _dead;

    public bool IsDead => _dead;
    public float HP => _hp;
    public float MaxHP => maxHP;
    public float HPRatio => maxHP > 0f ? Mathf.Clamp01(_hp / maxHP) : 0f;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _hp = Mathf.Max(1f, maxHP);
    }

    void FixedUpdate()
    {
        if (_dead)
        {
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        if (!flowField)
        {
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        Vector2 dir = flowField.GetFlowDir(transform.position);

        // Flow がゼロなら停止
        if (dir.sqrMagnitude < 0.0001f)
        {
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        // 進行方向に移動
        _rb.linearVelocity = dir.normalized * moveSpeed;

        // 向きも進行方向へ回転（角度/秒ベース）
        if (lookAtDir)
        {
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            float current = transform.eulerAngles.z;
            float maxStep = rotateSpeed * Time.fixedDeltaTime; // ← 1秒あたり rotateSpeed 度
            float next = Mathf.MoveTowardsAngle(current, ang, maxStep);
            transform.eulerAngles = new Vector3(0, 0, next);
        }
    }

    /// <summary>弾などから呼ぶダメージ関数</summary>
    public void TakeDamage(float dmg, Vector2? hitFromWorld = null)
    {
        if (_dead) return;

        float d = Mathf.Max(0f, dmg);
        if (d <= 0f) return;

        _hp -= d;

        // ノックバック
        if (knockbackForce > 0f && hitFromWorld.HasValue)
        {
            Vector2 pushDir = ((Vector2)transform.position - hitFromWorld.Value).normalized;
            _rb.AddForce(pushDir * knockbackForce, ForceMode2D.Impulse);
        }

        if (_hp <= 0f)
        {
            Die();
        }
    }

    void Die()
    {
        if (_dead) return;
        _dead = true;

        // 死亡エフェクト
        if (deathVfxPrefab)
        {
            var v = Instantiate(deathVfxPrefab, transform.position, Quaternion.identity);
            if (deathVfxLife > 0f) Destroy(v, deathVfxLife);
        }

        if (destroyOnDeath)
        {
            Destroy(gameObject);
        }
        else
        {
            _rb.linearVelocity = Vector2.zero;
            enabled = false;
        }
    }

    void OnTriggerStay2D(Collider2D other)
    {
        // Base 以外は無視
        if (!other.CompareTag(baseTag))
            return;

        // すでに死亡している敵は攻撃しない
        if (_dead) return;

        // Base に触れている間はその場で止まる
        if (_rb != null)
        {
            _rb.linearVelocity = Vector2.zero;
        }

        // 攻撃タイマー進行
        _attackTimer += Time.deltaTime;
        if (_attackTimer < attackInterval)
            return;

        _attackTimer = 0f;

        // BaseHealth を探してダメージを与える
        var baseHp = other.GetComponent<BaseHealth>();
        if (baseHp != null)
        {
            baseHp.TakeDamage(attackDamage);
        }
    }

    void OnTriggerExit2D(Collider2D other)
    {
        // Base から離れたら攻撃タイマーをリセット
        if (!other.CompareTag(baseTag))
            return;

        _attackTimer = 0f;
    }
}
