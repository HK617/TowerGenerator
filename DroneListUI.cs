using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

// 新InputSystem対応
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

public class DroneListUI : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("現在稼働しているドローンを管理するマネージャ")]
    public DroneBuildManager manager;
    [Tooltip("DroneListItem を並べるコンテナ")]
    public RectTransform content;
    [Tooltip("1行分のドローン表示用プレハブ")]
    public DroneListItemUI itemPrefab;

    [Header("Slide Panel")]
    [Tooltip("パネルが表示されているときのX(ローカル座標)")]
    public float shownX = 0f;
    [Tooltip("パネルが隠れているときのX(ローカル座標)")]
    public float hiddenX = -220f;
    [Tooltip("スライドにかける秒数（大きいほどゆっくり）")]
    public float slideDuration = 0.25f;

    [Header("Handle Button")]
    [Tooltip("バーにくっついて動くボタン(<< とか >> を表示するやつ)")]
    public RectTransform handle;
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

    [Header("Detail Panel (右側のメニュー)")]
    [Tooltip("ドローンをクリックしたときに表示する詳細メニューパネル")]
    public RectTransform detailPanel;
    [Tooltip("詳細メニューのタイトル (ドローン名など)")]
    public TMP_Text detailTitleText;
    [Tooltip("詳細メニューのサブテキスト (状態など)")]
    public TMP_Text detailSubText;

    [Header("Detail Job UI")]
    public TMP_Text detailJobText;     // 現在の Job 表示用
    public Button builderJobButton;    // Builder ボタン
    public Button minerJobButton;      // Miner ボタン

    [Header("Start")]
    [Tooltip("ゲーム開始時にパネルを表示した状態にするか")]
    public bool startShown = true;

    // 内部
    readonly List<DroneListItemUI> _slots = new();
    RectTransform _rt;
    bool _isShown;
    float _slideT;      // 0…hidden, 1…shown
    float _slideVel;

    // 現在詳細メニューで選択中のアイテム
    DroneListItemUI _currentSelectedItem;

    void Awake()
    {
        _rt = GetComponent<RectTransform>();
        if (!manager)
            manager = FindFirstObjectByType<DroneBuildManager>();

        // 初期表示状態
        _isShown = startShown;
        _slideT = _isShown ? 1f : 0f;

        // 初期位置反映
        float x = Mathf.Lerp(hiddenX, shownX, _slideT);
        SetPanelX(x);
        UpdateHandle(_slideT);

        // 詳細メニューは最初は閉じておく
        if (detailPanel != null)
            detailPanel.gameObject.SetActive(false);
        _currentSelectedItem = null;

        if (builderJobButton != null)
            builderJobButton.onClick.AddListener(OnBuilderJobButtonClicked);
        if (minerJobButton != null)
            minerJobButton.onClick.AddListener(OnMinerJobButtonClicked);
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
        // ① キー入力でトグル(Tab キー)
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

        // ★ 詳細メニューが開いている間は毎フレーム内容を更新（採掘ログをリアルタイム反映）
        if (detailPanel != null && detailPanel.gameObject.activeSelf)
        {
            UpdateDetailPanel();
        }
    }

    // =========================================================
    // 外からボタンの OnClick で呼ぶ用
    // =========================================================
    public void Toggle()
    {
        SetShown(!_isShown);
    }

    public void Show()
    {
        SetShown(true);
    }

    public void Hide()
    {
        SetShown(false);
    }

    void SetShown(bool show)
    {
        if (_isShown == show)
            return;

        _isShown = show;

        // 押した瞬間に文字だけは最新にしておく
        UpdateHandle(_isShown ? 1f : 0f);

        // パネルを隠すときは詳細メニューも閉じる
        if (!_isShown && detailPanel != null)
        {
            detailPanel.gameObject.SetActive(false);
            _currentSelectedItem = null;
        }
    }

    // =========================================================
    // Droneの状態反映
    // =========================================================
    void HandleState(List<DroneWorker> drones, int waitingCount)
    {
        // スロット不足なら増やす
        while (_slots.Count < drones.Count)
        {
            var it = Instantiate(itemPrefab, content);
            // 生成時にクリックイベント登録
            it.onClick = OnItemClicked;
            _slots.Add(it);
        }

        // 各ドローンの状態を1行ずつ反映
        for (int i = 0; i < drones.Count; i++)
        {
            var d = drones[i];
            var slot = _slots[i];
            slot.gameObject.SetActive(true);

            // このスロットがどのドローンに対応しているか覚えさせる
            slot.boundDrone = d;
            slot.onClick = OnItemClicked; // 念のため毎フレーム設定

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

                default:
                    sub = "";
                    prog = 0f;
                    slot.SetColor(new Color(1f, 1f, 1f, 1f));
                    break;
            }

            slot.SetTitle(title);
            slot.SetSub(sub);
            slot.SetProgress(prog);
        }

        // 余ったスロットは非表示
        for (int i = drones.Count; i < _slots.Count; i++)
        {
            _slots[i].gameObject.SetActive(false);
        }

        // ドローンの状態が変わって、選択中のドローンがいなくなった場合はメニューを閉じる
        if (_currentSelectedItem != null &&
            (!_currentSelectedItem.gameObject.activeInHierarchy || _currentSelectedItem.boundDrone == null))
        {
            if (detailPanel != null)
                detailPanel.gameObject.SetActive(false);
            _currentSelectedItem = null;
        }
    }

    void OnBuilderJobButtonClicked()
    {
        if (_currentSelectedItem == null) return;
        if (_currentSelectedItem.boundDrone == null) return;

        _currentSelectedItem.boundDrone.job = DroneWorker.JobType.Builder;
        UpdateDetailPanel(); // 表示も更新
    }

    void OnMinerJobButtonClicked()
    {
        if (_currentSelectedItem == null) return;
        if (_currentSelectedItem.boundDrone == null) return;

        _currentSelectedItem.boundDrone.job = DroneWorker.JobType.Miner;
        UpdateDetailPanel();
    }


    // =========================================================
    // 詳細メニューのトグル表示（クリックされたとき）
    // =========================================================
    void OnItemClicked(DroneListItemUI item)
    {
        if (detailPanel == null)
            return;

        // すでにこの行が選択中 & メニューが開いている → クリックで閉じる
        if (_currentSelectedItem == item && detailPanel.gameObject.activeSelf)
        {
            detailPanel.gameObject.SetActive(false);
            _currentSelectedItem = null;
            return;
        }

        // 新しく選択
        _currentSelectedItem = item;

        // メニューを開く（位置は固定。RectTransform の位置はシーン側で調整）
        detailPanel.gameObject.SetActive(true);
    }

    // =========================================================
    // 詳細パネルの内容更新（状態 + 採掘ログ）
    // =========================================================
    void UpdateDetailPanel()
    {
        if (detailPanel == null || !detailPanel.gameObject.activeSelf)
            return;

        if (_currentSelectedItem == null)
            return;

        var item = _currentSelectedItem;

        // タイトル
        if (detailTitleText != null)
        {
            if (item.boundDrone != null)
                detailTitleText.text = item.boundDrone.name;
            else if (item.titleText != null)
                detailTitleText.text = item.titleText.text;
            else
                detailTitleText.text = string.Empty;
        }

        // サブテキスト（状態 + 採掘ログ）
        if (detailSubText != null)
        {
            if (item.boundDrone == null)
            {
                detailSubText.text = string.Empty;
            }
            else
            {
                // まず状態テキスト
                string stateLine;
                switch (item.boundDrone.State)
                {
                    case DroneWorker.DroneState.Idle:
                        stateLine = "状態: 待機中";
                        break;
                    case DroneWorker.DroneState.MovingToTarget:
                        stateLine = "状態: 移動中";
                        break;
                    case DroneWorker.DroneState.Working:
                        stateLine = "状態: 作業中";
                        break;
                    default:
                        stateLine = string.Empty;
                        break;
                }

                // 採掘ログを取得
                string miningSummary = item.boundDrone.GetMinedItemSummary();

                if (string.IsNullOrEmpty(miningSummary))
                {
                    // 採掘ログがなければ状態だけ
                    detailSubText.text = stateLine;
                }
                else
                {
                    // 状態 + 空行 + 採掘ログ
                    detailSubText.text = stateLine + "\n\n" + miningSummary;
                }
            }
        }

        // ★ Job 表示
        if (detailJobText != null)
        {
            if (item.boundDrone == null)
            {
                detailJobText.text = "";
            }
            else
            {
                switch (item.boundDrone.job)
                {
                    case DroneWorker.JobType.Builder:
                        detailJobText.text = "Job: Builder（建築担当）";
                        break;
                    case DroneWorker.JobType.Miner:
                        detailJobText.text = "Job: Miner（採掘担当）";
                        break;
                }
            }
        }
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
        if (handle == null)
            return;

        // ボタンの位置をパネルのローカル空間で少し右側に固定しておく
        // （表示/非表示で多少ずらしたいとき用に2つの値を用意している）
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
