using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class Projectile2D : MonoBehaviour
{
    [Header("Movement")]
    public float speed = 16f;
    public float maxTravelDistance = 30f;   // 寿命ではなく距離で消える

    [Header("Damage")]
    public float damage = 5f;
    public bool destroyOnHit = true;

    Rigidbody2D _rb;
    Vector2 _spawnPos;
    Vector2 _dir;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 0f;
        _rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        _spawnPos = transform.position;
    }

    /// <summary>
    /// タレットが撃つ瞬間に「どの方向を向いているか」を渡す
    /// </summary>
    public void FireInCurrentForward(Transform shooter)
    {
        // タレットのZ回転角度から進行方向を算出
        float angle = shooter.eulerAngles.z * Mathf.Deg2Rad;
        _dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)).normalized;
    }

    void FixedUpdate()
    {
        // 一定距離を超えたら削除
        if ((_rb.position - _spawnPos).sqrMagnitude > maxTravelDistance * maxTravelDistance)
        {
            Destroy(gameObject);
            return;
        }

        // 移動
        _rb.MovePosition(_rb.position + _dir * speed * Time.fixedDeltaTime);
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        var enemy = other.GetComponent<EnemyChaseBase2D>();
        if (enemy && !enemy.IsDead)
        {
            enemy.TakeDamage(damage, _rb.position);
            if (destroyOnHit) Destroy(gameObject);
        }
    }
}
