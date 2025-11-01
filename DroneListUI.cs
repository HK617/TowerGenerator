using System.Collections.Generic;
using UnityEngine;
using TMPro;

// �VInputSystem�Ή�
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

public class DroneListUI : MonoBehaviour
{
    [Header("Refs")]
    public DroneBuildManager manager;
    public RectTransform content;          // DroneListItem ����ׂ�Ƃ���
    public DroneListItemUI itemPrefab;

    [Header("Slide Panel")]
    [Tooltip("�\������X(���[�J��)")]
    public float shownX = 0f;
    [Tooltip("��\������X(���[�J��)")]
    public float hiddenX = -220f;
    [Tooltip("�X���C�h�ɂ�����b�� (���炩��)")]
    public float slideDuration = 0.25f;

    [Header("Handle Button")]
    [Tooltip("�o�[�ɂ������ē����{�^��(<<�Ƃ�>>��\��������)")]
    public RectTransform handle;           // �� �{�^����RectTransform�������ɓ����
    [Tooltip("�{�^����̕���(TMP)")]
    public TMP_Text handleLabel;
    [Tooltip("�p�l�����\������Ă���Ƃ��̃{�^������")]
    public string shownLabel = "<<";
    [Tooltip("�p�l�����B��Ă���Ƃ��̃{�^������")]
    public string hiddenLabel = ">>";
    [Tooltip("�\�����̃n���h����X(�p�l���̃��[�J�����W�n��)")]
    public float handleShownX = 200f;
    [Tooltip("��\�����̃n���h����X(�p�l���̃��[�J�����W�n��)")]
    public float handleHiddenX = 200f;

    [Header("Start")]
    public bool startShown = true;

    // ����
    readonly List<DroneListItemUI> _slots = new();
    RectTransform _rt;
    bool _isShown;
    float _slideT;      // 0�chidden, 1�cshown
    float _slideVel;

    void Awake()
    {
        _rt = GetComponent<RectTransform>();
        if (!manager)
            manager = FindFirstObjectByType<DroneBuildManager>();

        _isShown = startShown;
        _slideT = _isShown ? 1f : 0f;

        // �����ʒu���f
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
        // �@ �L�[���͂Ńg�O��
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

        // �A �X���C�h�A�j���i�p�l���ƃ{�^�������j
        float target = _isShown ? 1f : 0f;
        _slideT = Mathf.SmoothDamp(_slideT, target, ref _slideVel, slideDuration);
        float x = Mathf.Lerp(hiddenX, shownX, _slideT);
        SetPanelX(x);
        UpdateHandle(_slideT);
    }

    // =========================================================
    // �O����{�^���� OnClick �ŌĂԗp
    // =========================================================
    public void Toggle()
    {
        _isShown = !_isShown;
        // �������u�Ԃɕ��������͍ŐV�ɂ��Ă���
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
    // Drone�̏�Ԕ��f�i�O�Ɠ����j
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
                    sub = waitingCount > 0 ? $"�ҋ@�� ({waitingCount}���҂�)" : "�ҋ@��";
                    prog = 0f;
                    slot.SetColor(new Color(1f, 1f, 1f, 0.6f));
                    break;

                case DroneWorker.DroneState.MovingToTarget:
                    sub = "�ړ����c";
                    prog = 0.1f;
                    slot.SetColor(new Color(0.8f, 1f, 0.8f, 1f));
                    break;

                case DroneWorker.DroneState.Working:
                    sub = d.CurrentTask != null && d.CurrentTask.def != null
                        ? $"���z��: {d.CurrentTask.def.displayName}"
                        : "���z���c";
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

        // �{�^���̈ʒu���p�l���̃��[�J����Ԃŏ����E���ɌŒ肵�Ă���
        // �i�\��/��\���ő������炵�����Ƃ��p��2�̒l��p�ӂ��Ă�j
        float hx = Mathf.Lerp(handleHiddenX, handleShownX, t);
        var hp = handle.anchoredPosition;
        hp.x = hx;
        handle.anchoredPosition = hp;

        // ���x�����X�V
        if (handleLabel != null)
        {
            handleLabel.text = (t > 0.5f) ? shownLabel : hiddenLabel;
        }
    }
}
