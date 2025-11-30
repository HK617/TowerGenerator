using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 建築中ゴーストの「必要素材 / 納品済み素材 / 進捗可視化」を管理するコンポーネント。
/// 
/// ・DroneWorker から AddDelivery() で素材が納品される
/// ・delivered の合計 / required の合計 で進捗(0～1)を計算
/// ・一度でも納品されたら、メインスプライトの透明度を変化させて進捗を表現
/// </summary>
[DisallowMultipleComponent]
public class ConstructionState : MonoBehaviour
{
    [Header("Definition")]
    [Tooltip("このゴーストに対応する BuildingDef（空なら最初の EnsureInitialized で設定される）")]
    public BuildingDef def;

    [Serializable]
    public class Entry
    {
        public string itemName;
        public int required;
        public int delivered;
    }

    [Header("Cost Entries (runtime)")]
    [Tooltip("必要素材 / 納品済み素材のリスト。通常は EnsureInitialized(def) で自動初期化される。")]
    public List<Entry> entries = new List<Entry>();

    [Header("Visual Progress (Alpha)")]
    [Tooltip("true のとき、素材が一度でも納品されるまではゴーストの透明度を変更しない")]
    public bool changeAlphaOnlyAfterStarted = true;

    [Tooltip("建築進捗 0%（素材ゼロ）のときのアルファ値")]
    [Range(0f, 1f)]
    public float minAlpha = 0.25f;

    [Tooltip("建築進捗 100%（全素材納品）のときのアルファ値")]
    [Range(0f, 1f)]
    public float maxAlpha = 1.0f;

    [Tooltip("子階層から SpriteRenderer を自動で探すかどうか")]
    public bool autoFindRenderers = true;

    SpriteRenderer[] _renderers;

    // =========================
    // ライフサイクル
    // =========================

    void Awake()
    {
        EnsureRenderersCached();
        // def があれば entries を初期化（シーンに直置きしたとき用）
        if (def != null && (entries == null || entries.Count == 0))
        {
            EnsureInitialized(def);
        }

        // 一応、起動時にもアルファを反映しておく
        UpdateVisualAlpha();
    }

    void OnValidate()
    {
        EnsureRenderersCached();
        UpdateVisualAlpha();
    }

    void EnsureRenderersCached()
    {
        if (!autoFindRenderers) return;

        if (_renderers == null || _renderers.Length == 0)
        {
            _renderers = GetComponentsInChildren<SpriteRenderer>(true);
        }
    }

    public int GetDeliveredAmount(string itemName)
    {
        if (entries == null) return 0;

        int total = 0;
        foreach (var e in entries)
        {
            if (e == null) continue;
            if (e.itemName != itemName) continue;
            total += Mathf.Max(0, e.delivered);
        }
        return total;
    }


    // =========================
    // 状態系プロパティ
    // =========================

    public bool IsInitialized
    {
        get { return entries != null && entries.Count > 0; }
    }

    /// <summary>一度でも素材が納品されていれば true</summary>
    public bool HasStartedBuild
    {
        get { return GetTotalDelivered() > 0; }
    }

    /// <summary>全ての required を満たしていれば true</summary>
    public bool IsCompleted
    {
        get
        {
            if (entries == null) return false;
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (e.required > 0 && e.delivered < e.required)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 素材進捗 (0～1)。
    /// (全 delivered 合計) / (全 required 合計) で計算。
    /// </summary>
    public float MaterialProgress01
    {
        get
        {
            int required = GetTotalRequired();
            if (required <= 0) return 0f;
            int delivered = GetTotalDelivered();
            return Mathf.Clamp01((float)delivered / required);
        }
    }

    public int GetTotalRequired()
    {
        if (entries == null) return 0;
        int sum = 0;
        foreach (var e in entries)
        {
            if (e == null) continue;
            if (e.required > 0)
                sum += e.required;
        }
        return sum;
    }

    public int GetTotalDelivered()
    {
        if (entries == null) return 0;
        int sum = 0;
        foreach (var e in entries)
        {
            if (e == null) continue;
            if (e.delivered > 0)
                sum += e.delivered;
        }
        return sum;
    }

    // =========================
    // 初期化
    // =========================

    /// <summary>
    /// BuildingDef から必要素材リスト entries を初期化する。
    /// 既に entries が入っている場合は何もしない。
    /// </summary>
    public void EnsureInitialized(BuildingDef def)
    {
        if (def == null) return;
        this.def = def;

        if (entries != null && entries.Count > 0)
            return;

        if (entries == null)
            entries = new List<Entry>();
        else
            entries.Clear();

        if (def.buildCosts != null)
        {
            foreach (var cost in def.buildCosts)
            {
                if (cost == null) continue;
                if (string.IsNullOrEmpty(cost.itemName)) continue;
                if (cost.amount <= 0) continue;

                var e = new Entry
                {
                    itemName = cost.itemName,
                    required = cost.amount,
                    delivered = 0
                };
                entries.Add(e);
            }
        }

        UpdateVisualAlpha();
    }

    // =========================
    // 納品ロジック
    // =========================

    /// <summary>
    /// 指定アイテムをこの建物に納品する。
    /// 実際に使われた個数を返す（足りない分だけ使われる）。
    /// </summary>
    /// <param name="itemName">アイテム名</param>
    /// <param name="amount">ドローンが持ってきた個数</param>
    /// <returns>この建物が実際に消費した個数</returns>
    public int AddDelivery(string itemName, int amount)
    {
        if (string.IsNullOrEmpty(itemName)) return 0;
        if (amount <= 0) return 0;
        if (entries == null || entries.Count == 0) return 0;

        int used = 0;

        foreach (var e in entries)
        {
            if (e == null) continue;
            if (e.itemName != itemName) continue;

            int remain = e.required - e.delivered;
            if (remain <= 0) continue;

            int add = Mathf.Min(remain, amount);
            e.delivered += add;
            used += add;
            amount -= add;

            if (amount <= 0)
                break;
        }

        if (used > 0)
        {
            // ★ 納品されたので見た目を更新
            UpdateVisualAlpha();
        }

        return used;
    }

    // =========================
    // 見た目の更新（進捗をアルファで表現）
    // =========================

    void UpdateVisualAlpha()
    {
        EnsureRenderersCached();
        if (_renderers == null || _renderers.Length == 0)
            return;

        // Q1: 建築開始後だけアルファを変えたい
        if (changeAlphaOnlyAfterStarted && !HasStartedBuild)
        {
            // まだ何も納品されていない場合は、元のゴースト色を保持
            return;
        }

        float t = MaterialProgress01;
        float a = Mathf.Lerp(minAlpha, maxAlpha, t);

        foreach (var r in _renderers)
        {
            if (r == null) continue;
            var c = r.color;
            c.a = a;
            r.color = c;
        }
    }
}
