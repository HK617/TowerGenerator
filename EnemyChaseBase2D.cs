using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class EnemyChaseBase2D : MonoBehaviour   // ← そのまま置き換えてOK
{
    [Header("Flow")]
    public FlowField025 flowField;     // ← インスペクタでシーン上のFlowFieldを割り当てる

    [Header("Move")]
    public float moveSpeed = 3.5f;
    public bool lookAtDir = true;
    public float rotateSpeed = 720f;

    Rigidbody2D _rb;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
    }

    void FixedUpdate()
    {
        if (flowField == null)
        {
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        Vector2 dir = flowField.GetFlowDir(transform.position);

        // Flowがゼロなら動かない
        if (dir.sqrMagnitude < 0.0001f)
        {
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        // 移動
        _rb.linearVelocity = dir.normalized * moveSpeed;

        // 向きを合わせるなら
        if (lookAtDir)
        {
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            float current = transform.eulerAngles.z;
            float next = Mathf.MoveTowardsAngle(current, ang, rotateSpeed * Time.fixedDeltaTime);
            transform.eulerAngles = new Vector3(0, 0, next);
        }
    }
}
