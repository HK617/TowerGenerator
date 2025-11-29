using UnityEngine;

public class MiningIconBlinker : MonoBehaviour
{
    [SerializeField] float blinkSpeed = 4f;    // 点滅スピード（大きいほど速い）
    [SerializeField] float minAlpha = 0.3f;  // 一番薄いときの透明度
    [SerializeField] float maxAlpha = 1.0f;  // 一番濃いときの透明度

    SpriteRenderer _sr;
    Color _baseColor;

    [Tooltip("true のときだけ点滅する")]
    public bool isBlinking = false;

    void Awake()
    {
        _sr = GetComponent<SpriteRenderer>();
        if (_sr != null)
            _baseColor = _sr.color;
    }

    void OnEnable()
    {
        if (_sr == null)
            _sr = GetComponent<SpriteRenderer>();
        if (_sr != null)
            _baseColor = _sr.color;
    }

    void Update()
    {
        if (!isBlinking || _sr == null)
            return;

        // 0〜1 を往復する値
        float t = 0.5f + 0.5f * Mathf.Sin(Time.time * blinkSpeed * Mathf.PI * 2f);
        float a = Mathf.Lerp(minAlpha, maxAlpha, t);

        var c = _baseColor;
        c.a = a;
        _sr.color = c;
    }

    /// <summary>外部から点滅ON/OFFするときに呼ぶ</summary>
    public void SetBlinking(bool value)
    {
        isBlinking = value;

        // OFFにしたら色を元に戻す
        if (!isBlinking && _sr != null)
        {
            _sr.color = _baseColor;
        }
    }
}
