using System.Collections.Generic;
using UnityEngine;
using TMPro;

// 新InputSystem対応
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

public class DroneListUI : MonoBehaviour
{
    [Header("Refs")]
    public DroneBuildManager manager;
    public RectTransform content;          // DroneListItem を並べるところ
    public DroneListItemUI itemPrefab;

    [Header("Slide Panel")]
    [Tooltip("表示時のX(ローカル)")]
    public float shownX = 0f;
    [Tooltip("非表示時のX(ローカル)")]
    public float hiddenX = -220f;
    [Tooltip("スライドにかける秒数 (滑らかさ)")]
    public float slideDuration = 0.25f;

    [Header("Handle Button")]
    [Tooltip("バーにくっついて動くボタン(<<とか>>を表示するやつ)")]
    public RectTransform handle;           // ← ボタンのRectTransformをここに入れる
    [Tooltip("ボタン上の文字(TMP)")]
    public TMP_Text handleLabel;
    [Tooltip("パネルが表示されているときのボタン文字")]
    public string shownLabel = "<<";
    [Tooltip("パネルが隠れているときのボタン文字")]
    public string hiddenLabel = ">>";
    [Tooltip("表示時のハンドルのX(パネルのローカル座標系で)")]
    public float handleShownX = 200f;
    [Tooltip("非表示時のハンドルのX(パネルのローカル座標系で)")]
    public float handleHiddenX = 200f;

    [Header("Start")]
    public bool startShown = true;

    // 内部
    readonly List<DroneListItemUI> _slots = new();
    RectTransform _rt;
    bool _isShown;
    float _slideT;      // 0…hidden, 1…shown
    float _slideVel;

    void Awake()
    {
        _rt = GetComponent<RectTransform>();
        if (!manager)
            manager = FindFirstObjectByType<DroneBuildManager>();

        _isShown = startShown;
        _slideT = _isShown ? 1f : 0f;

        // 初期位置反映
        float x = Mathf.Lerp(hiddenX, shownX, _slideT);
        SetPanelX(x);
        UpdateHandle(_slideT);
    }

    void OnEnable()
    {
        if (manager != null)
            manager.OnDroneStateChanged += HandleState;
    }

    void OnDisable()
    {
        if (manager != null)
            manager.OnDroneStateChanged -= HandleState;
    }

    void Update()
    {
        // ① キー入力でトグル
        bool toggleRequested = false;

#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.Tab))
            toggleRequested = true;
#endif

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
            toggleRequested = true;
#endif

        if (toggleRequested)
            Toggle();

        // ② スライドアニメ（パネルとボタン両方）
        float target = _isShown ? 1f : 0f;
        _slideT = Mathf.SmoothDamp(_slideT, target, ref _slideVel, slideDuration);
        float x = Mathf.Lerp(hiddenX, shownX, _slideT);
        SetPanelX(x);
        UpdateHandle(_slideT);
    }

    // =========================================================
    // 外からボタンの OnClick で呼ぶ用
    // =========================================================
    public void Toggle()
    {
        _isShown = !_isShown;
        // 押した瞬間に文字だけは最新にしておく
        UpdateHandle(_isShown ? 1f : 0f);
    }

    public void Show()
    {
        _isShown = true;
        UpdateHandle(1f);
    }

    public void Hide()
    {
        _isShown = false;
        UpdateHandle(0f);
    }

    // =========================================================
    // Droneの状態反映（前と同じ）
    // =========================================================
    void HandleState(List<DroneWorker> drones, int waitingCount)
    {
        while (_slots.Count < drones.Count)
        {
            var it = Instantiate(itemPrefab, content);
            _slots.Add(it);
        }

        for (int i = 0; i < drones.Count; i++)
        {
            var d = drones[i];
            var slot = _slots[i];
            slot.gameObject.SetActive(true);

            string title = d.name;
            string sub = "";
            float prog = 0f;

            switch (d.State)
            {
                case DroneWorker.DroneState.Idle:
                    sub = waitingCount > 0 ? $"待機中 ({waitingCount}件待ち)" : "待機中";
                    prog = 0f;
                    slot.SetColor(new Color(1f, 1f, 1f, 0.6f));
                    break;

                case DroneWorker.DroneState.MovingToTarget:
                    sub = "移動中…";
                    prog = 0.1f;
                    slot.SetColor(new Color(0.8f, 1f, 0.8f, 1f));
                    break;

                case DroneWorker.DroneState.Working:
                    sub = d.CurrentTask != null && d.CurrentTask.def != null
                        ? $"建築中: {d.CurrentTask.def.displayName}"
                        : "建築中…";
                    prog = d.CurrentProgress01;
                    slot.SetColor(new Color(0.7f, 1f, 0.7f, 1f));
                    break;
            }

            slot.SetTitle(title);
            slot.SetSub(sub);
            slot.SetProgress(prog);
        }

        for (int i = drones.Count; i < _slots.Count; i++)
            _slots[i].gameObject.SetActive(false);
    }

    // =========================================================
    // helper
    // =========================================================
    void SetPanelX(float x)
    {
        var p = _rt.anchoredPosition;
        p.x = x;
        _rt.anchoredPosition = p;
    }

    void UpdateHandle(float t)
    {
        if (handle == null) return;

        // ボタンの位置をパネルのローカル空間で少し右側に固定しておく
        // （表示/非表示で多少ずらしたいとき用に2つの値を用意してる）
        float hx = Mathf.Lerp(handleHiddenX, handleShownX, t);
        var hp = handle.anchoredPosition;
        hp.x = hx;
        handle.anchoredPosition = hp;

        // ラベルも更新
        if (handleLabel != null)
        {
            handleLabel.text = (t > 0.5f) ? shownLabel : hiddenLabel;
        }
    }
}
