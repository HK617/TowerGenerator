using UnityEngine;

/// <summary>
/// シーン開始時に「このあたりは全部歩けるよ」と FlowField025 に教えるだけのスクリプト。
/// ゴール(Base)はここでは決めない。Baseを置いたときに BuildPlacement から
/// flowField.SetTargetWorld(...) が呼ばれて決まる。
/// </summary>
public class FullFineGridGenerator : MonoBehaviour
{
    [Header("FlowField target")]
    public FlowField025 flowField;

    [Header("Grid settings")]
    [Tooltip("FlowField 1マスの大きさに合わせる")]
    public float cellSize = 0.25f;

    [Tooltip("X正方向・負方向に何マスぶん作るか")]
    public int halfCellsX = 200;

    [Tooltip("Y正方向・負方向に何マスぶん作るか")]
    public int halfCellsY = 200;

    void Start()
    {
        if (flowField == null) return;

        // 原点を真ん中にして -half ～ +half までを「歩ける」として登録
        for (int gx = -halfCellsX; gx <= halfCellsX; gx++)
        {
            for (int gy = -halfCellsY; gy <= halfCellsY; gy++)
            {
                float wx = gx * cellSize + cellSize * 0.5f;
                float wy = gy * cellSize + cellSize * 0.5f;
                flowField.MarkWalkable(wx, wy);
            }
        }

        // ★ここではゴールを決めない★
        // Baseが建ったときに BuildPlacement から
        //     flowField.SetTargetWorld(basePos);
        // が呼ばれて、そこで初めてゴールが決まる

        flowField.Rebuild();
    }
}
