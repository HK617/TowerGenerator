using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class PathPreviewRunner : MonoBehaviour
{
    public FlowField025 flow;
    public LayerMask obstacleMask;

    [Header("Move")]
    public float speed = 8f;
    public float stepLen = 0.15f;
    public int maxSteps = 400;
    public float goalRadius = 0.5f;

    Rigidbody2D rb;
    LineRenderer line;
    readonly List<Vector3> points = new List<Vector3>();

    // ������e�ɒm�点�邾���̃R�[���o�b�N�i�������߂ł͂Ȃ��j
    System.Action<PathPreviewRunner> _onFinished;
    bool _stopped = false;

    public void Init(FlowField025 flow, LayerMask obstacleMask, System.Action<PathPreviewRunner> onFinished)
    {
        this.flow = flow;
        this.obstacleMask = obstacleMask;
        this._onFinished = onFinished;
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        rb.gravityScale = 0f;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;

        line = GetComponent<LineRenderer>();
        if (line == null)
            line = gameObject.AddComponent<LineRenderer>();

        line.useWorldSpace = true;
        line.widthMultiplier = 0.06f;
        line.positionCount = 0;

        // �Ƃ肠����������悤�ɂ���
        line.material = new Material(Shader.Find("Sprites/Default"));
        line.startColor = Color.red;
        line.endColor = Color.red;
        line.sortingOrder = 200;
    }

    void Start()
    {
        AddPoint(transform.position);
    }

    void FixedUpdate()
    {
        if (_stopped) return;

        if (flow == null)
        {
            StopRunner();
            return;
        }

        Vector2 dir = flow.GetFlowDir(transform.position);
        if (dir.sqrMagnitude < 0.0001f)
        {
            StopRunner();
            return;
        }

        dir.Normalize();
        float moveDist = speed * Time.fixedDeltaTime;
        Vector2 curr = rb.position;
        Vector2 next = curr + dir * moveDist;

        // ��Q���`�F�b�N
        bool blocked = Physics2D.Raycast(curr, dir, moveDist + 0.05f, obstacleMask);
        if (blocked)
        {
            Vector2 rightDir = new Vector2(dir.y, -dir.x);
            Vector2 leftDir = new Vector2(-dir.y, dir.x);

            if (!Physics2D.Raycast(curr, rightDir, moveDist + 0.05f, obstacleMask))
                next = curr + rightDir * moveDist;
            else if (!Physics2D.Raycast(curr, leftDir, moveDist + 0.05f, obstacleMask))
                next = curr + leftDir * moveDist;
            else
            {
                // ���S�ɋl�܂����炻���܂ł̃��C�����c���ďI��
                StopRunner();
                return;
            }
        }

        rb.MovePosition(next);

        // ��苗�����ƂɃ��C����L�΂�
        if ((next - (Vector2)points[points.Count - 1]).sqrMagnitude >= stepLen * stepLen)
        {
            AddPoint(next);
            if (points.Count >= maxSteps)
            {
                StopRunner();
                return;
            }
        }

        // �S�[���ɏ\���߂Â����炻���ŏI���i���C���͎c���j
        if (flow.IsGoalNear(next, goalRadius))
        {
            AddPoint(next);
            StopRunner();
        }
    }

    void AddPoint(Vector3 p)
    {
        p.z = -0.1f;  // �O�ɏo���Ă���
        points.Add(p);
        line.positionCount = points.Count;
        line.SetPositions(points.ToArray());
    }

    void StopRunner()
    {
        if (_stopped) return;
        _stopped = true;
        _onFinished?.Invoke(this);
        // �� Destroy���Ȃ� �� ���C���͎c��
    }
}
