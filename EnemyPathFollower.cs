using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// EnemyPathGrid からもらったレールに沿って移動するコンポーネント。
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class EnemyPathFollower : MonoBehaviour
{
    public float moveSpeed = 3f;
    public float waypointReachRadius = 0.05f;

    Rigidbody2D rb;
    List<Vector2> path;
    int pathIndex;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    void FixedUpdate()
    {
        if (path == null || path.Count == 0 || pathIndex >= path.Count)
        {
            rb.linearVelocity = Vector2.zero;
            return;
        }

        Vector2 target = path[pathIndex];
        Vector2 pos = rb.position;
        Vector2 dir = (target - pos);

        float dist = dir.magnitude;
        if (dist < waypointReachRadius)
        {
            pathIndex++;
            if (pathIndex >= path.Count)
            {
                rb.linearVelocity = Vector2.zero;
                return;
            }
            target = path[pathIndex];
            dir = (target - pos);
        }

        dir.Normalize();
        rb.linearVelocity = dir * moveSpeed;
    }

    /// <summary>外部からルートを設定する</summary>
    public void SetPath(List<Vector2> newPath)
    {
        path = newPath;
        pathIndex = 0;
    }
}
