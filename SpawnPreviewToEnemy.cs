using UnityEngine;

public class SpawnPreviewToEnemy : MonoBehaviour
{
    GameObject _enemyPrefab;
    int _count;
    float _jitter;
    float _life;
    FlowField025 _flow;
    bool _drawPath;

    Vector3 _baseWorld;
    bool _hasBaseWorld;

    float _timer;

    public void Init(GameObject enemyPrefab,
                     int count,
                     float jitter,
                     float life,
                     FlowField025 flow,
                     bool drawPath,
                     Vector3 baseWorld,
                     bool hasBaseWorld)
    {
        _enemyPrefab = enemyPrefab;
        _count = count;
        _jitter = jitter;
        _life = life;
        _flow = flow;
        _drawPath = drawPath;
        _baseWorld = baseWorld;
        _hasBaseWorld = hasBaseWorld;

        _timer = life;

        if (_drawPath)
        {
            var path = gameObject.AddComponent<SpawnPathPreview>();
            path.flow = _flow;
            path.SetBaseWorld(_baseWorld, _hasBaseWorld);   // ÅöÇ±Ç±Ç≈ìnÇ∑
        }
    }

    void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer <= 0f)
        {
            SpawnEnemiesThenDie();
        }
        else
        {
            var sr = GetComponentInChildren<SpriteRenderer>();
            if (sr)
            {
                float t = Mathf.PingPong(Time.time * 5f, 1f);
                sr.color = new Color(1f, 0.3f, 0.3f, Mathf.Lerp(0.25f, 1f, t));
            }
        }
    }

    void SpawnEnemiesThenDie()
    {
        if (_enemyPrefab != null)
        {
            for (int i = 0; i < _count; i++)
            {
                Vector2 j = Random.insideUnitCircle * _jitter;
                var pos = new Vector3(transform.position.x + j.x,
                                      transform.position.y + j.y,
                                      0f);
                Instantiate(_enemyPrefab, pos, Quaternion.identity);
            }
        }

        Destroy(gameObject);
    }
}
