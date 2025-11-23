using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using TMPro;
using Unity.Cinemachine;

public class BuildBarUI : MonoBehaviour
{
    [Header("Data")]
    // 通常ズーム(遠いとき)に使うビルド
    public List<BuildingDef> items = new();
    // ズームイン(近いとき)に使うビルド
    public List<BuildingDef> zoomedInDefs = new();

    [Header("Refs")]
    public RectTransform container;
    public Button buttonPrefab;
    public Color selectedColor = new(0.9f, 0.9f, 1f, 1f);
    public Color normalColor = new(1f, 1f, 1f, 1f);

    [Header("Placement")]
    public BuildPlacement placement;

    [Header("Camera / zoom")]
    public CinemachineCamera vcam;
    public float fineGridThreshold = 3f;

    public bool autoSelectFirst = false;

    readonly List<Button> _buttons = new();
    int _selectedIndex = -1;
    bool _lastIsFine = false;

    void Awake()
    {
        if (!container) container = (RectTransform)transform;

        // ここでは「短い方を長い方に合わせる」だけやっておく
        NormalizeLists();

        // ボタンは「どちらか多い方の数」で作るのがポイント
        BuildButtonsForMaxCount();

        if (vcam == null)
            vcam = FindFirstObjectByType<CinemachineCamera>();

        _lastIsFine = ShouldUseFineGridNow();

        if (autoSelectFirst && GetMaxCount() > 0)
            Select(0);
        else
            Deselect();

        RefreshButtonIcons(_lastIsFine);
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.digit1Key.wasPressedThisFrame) ToggleHotkey(1);
            if (kb.digit2Key.wasPressedThisFrame) ToggleHotkey(2);
            if (kb.digit3Key.wasPressedThisFrame) ToggleHotkey(3);
            if (kb.digit4Key.wasPressedThisFrame) ToggleHotkey(4);
            if (kb.digit5Key.wasPressedThisFrame) ToggleHotkey(5);
            if (kb.digit6Key.wasPressedThisFrame) ToggleHotkey(6);
            if (kb.digit7Key.wasPressedThisFrame) ToggleHotkey(7);
            if (kb.digit8Key.wasPressedThisFrame) ToggleHotkey(8);
            if (kb.digit9Key.wasPressedThisFrame) ToggleHotkey(9);
            if (kb.escapeKey.wasPressedThisFrame) Deselect();
        }
#endif

        bool nowFine = ShouldUseFineGridNow();
        if (nowFine != _lastIsFine)
        {
            Deselect();
            // ズーム状態が変わったら見た目を再バインド
            RefreshButtonIcons(nowFine);
        }
        _lastIsFine = nowFine;
    }

    // ================== 核心1：リストの長さをそろえる ==================
    void NormalizeLists()
    {
        if (items == null) items = new List<BuildingDef>();
        if (zoomedInDefs == null) zoomedInDefs = new List<BuildingDef>();

        int max = GetMaxCount();

        while (items.Count < max) items.Add(null);
        while (zoomedInDefs.Count < max) zoomedInDefs.Add(null);
    }

    int GetMaxCount()
    {
        int a = items != null ? items.Count : 0;
        int b = zoomedInDefs != null ? zoomedInDefs.Count : 0;
        return (a > b) ? a : b;
    }

    // ================== 核心2：「多い方の数」でボタンを作る ==================
    void BuildButtonsForMaxCount()
    {
        _buttons.Clear();

        int max = GetMaxCount();

        // もしすでに子にボタンがあるならそれを拾う
        foreach (Transform child in container)
        {
            var btn = child.GetComponent<Button>();
            if (btn) _buttons.Add(btn);
        }

        // 足りなければ増やす
        while (_buttons.Count < max)
        {
            if (buttonPrefab == null) break;
            var btn = Instantiate(buttonPrefab, container);
            btn.name = $"BuildButton_{_buttons.Count + 1}";
            _buttons.Add(btn);
        }

        // 多すぎる分は使わないけど、ここでは消さないでOK

        // 各ボタンにクリック処理をつける
        for (int i = 0; i < _buttons.Count; i++)
        {
            int index = i;
            var btn = _buttons[i];
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() =>
            {
                if (_selectedIndex == index) Deselect();
                else Select(index);
            });
        }
    }

    // ================== ホットキー (1〜9) ==================
    void ToggleHotkey(int n)
    {
        // ズーム状態によって見てるリストが違うので、まずは両方見る
        bool isFine = ShouldUseFineGridNow();
        int target = -1;
        int max = GetMaxCount();

        for (int i = 0; i < max; i++)
        {
            BuildingDef def = isFine ? zoomedInDefs[i] : items[i];
            if (def != null && def.hotkey == n)
            {
                target = i;
                break;
            }
        }
        if (target < 0) return;

        if (_selectedIndex == target) Deselect();
        else Select(target);
    }

    bool ShouldUseFineGridNow()
    {
        if (vcam == null) return false;
        return vcam.Lens.OrthographicSize <= fineGridThreshold;
    }

    void Select(int index)
    {
        int max = GetMaxCount();
        if (index < 0 || index >= max) return;

        if (_selectedIndex >= 0 && _selectedIndex < _buttons.Count)
            SetButtonColor(_buttons[_selectedIndex], normalColor);

        _selectedIndex = index;
        if (index < _buttons.Count)
            SetButtonColor(_buttons[index], selectedColor);

        bool isFine = ShouldUseFineGridNow();
        BuildingDef defToUse = isFine ? zoomedInDefs[index] : items[index];

        if (defToUse == null)
            defToUse = items[index];

        if (placement)
        {
            // ★ ここで SelectionBoxDrawer に「建築モード開始」を知らせる
            SelectionBoxDrawer.NotifyBuildModeStartedFromOutside();

            placement.useFineGrid = isFine;
            placement.SetSelected(defToUse);
        }
    }

    void Deselect()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _buttons.Count)
            SetButtonColor(_buttons[_selectedIndex], normalColor);

        _selectedIndex = -1;
        if (placement) placement.SetSelected(null);
    }

    void SetButtonColor(Button b, Color c)
    {
        var img = b.GetComponent<Image>();
        if (img) img.color = c;
    }

    // ================== 見た目の更新 ==================
    void RefreshButtonIcons(bool isFine)
    {
        // 実行中に中身が変わっても対応できるように
        NormalizeLists();

        int max = GetMaxCount();

        for (int i = 0; i < _buttons.Count; i++)
        {
            var btn = _buttons[i];
            var img = btn.GetComponent<Image>();
            if (!img)
            {
                var iconTr = btn.transform.Find("Image") ?? btn.transform.Find("Icon");
                if (iconTr) img = iconTr.GetComponent<Image>();
            }
            if (img == null) continue;

            if (i >= max)
            {
                // 過剰なボタンは非表示にするならここで
                // btn.gameObject.SetActive(false);
                continue;
            }

            BuildingDef showDef = null;
            if (isFine && zoomedInDefs[i] != null)
                showDef = zoomedInDefs[i];
            else
                showDef = items[i];

            if (showDef != null && showDef.icon != null)
            {
                img.sprite = showDef.icon;
                btn.gameObject.SetActive(true);
            }
            else
            {
                // ここは「データがないスロット」なので
                // ボタンを非表示にしておくと分かりやすい
                btn.gameObject.SetActive(false);
            }
        }
    }
}
