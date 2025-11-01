using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Transform))]
public class SpawnPathPreview : MonoBehaviour
{
    public FlowField025 flow;
    public LineRenderer line;

    // BuildPlacementからもらったBaseの位置
    Vector3 _baseWorld;
    bool _hasBaseWorld;

    [Header("Path settings")]
    public int maxSteps = 200;
    public float stepLen = 0.22f;
    public float arriveDistance = 0.25f;
    public float lineWidth = 0.055f;

    readonly List<Vector3> _pts = new List<Vector3>(256);

    public void SetBaseWorld(Vector3 baseWorld, bool hasBaseWorld)
    {
        _baseWorld = baseWorld;
        _hasBaseWorld = hasBaseWorld;
    }

    void Awake()
    {
        if (line == null)
        {
            line = gameObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.widthMultiplier = lineWidth;
            line.numCapVertices = 2;
            line.sortingOrder = 2000;
            line.startColor = new Color(1f, 0f, 0f, 0.85f);
            line.endColor = new Color(1f, 0.5f, 0f, 0.05f);
        }
    }

    void Update()
    {
        RebuildLineEveryFrame();
    }

    void RebuildLineEveryFrame()
    {
        if (line == null)
            return;

        _pts.Clear();

        Vector3 start = transform.position;
        start.z = 0f;
        _pts.Add(start);

        Vector3 p = start;
        bool reachedByFlow = false;

        for (int i = 0; i < maxSteps; i++)
        {
            Vector2 dir = Vector2.zero;
            if (flow != null)
                dir = flow.GetFlowDir(p);

            if (dir.sqrMagnitude < 0.0001f)
                break;

            p += (Vector3)(dir.normalized * stepLen);
            p.z = 0f;
            _pts.Add(p);

            if (_hasBaseWorld)
            {
                if ((p - _baseWorld).sqrMagnitude < arriveDistance * arriveDistance)
                {
                    reachedByFlow = true;
                    break;
                }
            }
        }

        // FlowでBaseまで行けなかったら最後に必ずBaseをくっつける
        if (_hasBaseWorld && !reachedByFlow)
        {
            _pts.Add(_baseWorld);
        }

        line.positionCount = _pts.Count;
        line.SetPositions(_pts.ToArray());
    }
}
