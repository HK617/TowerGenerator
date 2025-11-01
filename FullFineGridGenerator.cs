using UnityEngine;

/// <summary>
/// �V�[���J�n���Ɂu���̂�����͑S���������v�� FlowField025 �ɋ����邾���̃X�N���v�g�B
/// �S�[��(Base)�͂����ł͌��߂Ȃ��BBase��u�����Ƃ��� BuildPlacement ����
/// flowField.SetTargetWorld(...) ���Ă΂�Č��܂�B
/// </summary>
public class FullFineGridGenerator : MonoBehaviour
{
    [Header("FlowField target")]
    public FlowField025 flowField;

    [Header("Grid settings")]
    [Tooltip("FlowField 1�}�X�̑傫���ɍ��킹��")]
    public float cellSize = 0.25f;

    [Tooltip("X�������E�������ɉ��}�X�Ԃ��邩")]
    public int halfCellsX = 200;

    [Tooltip("Y�������E�������ɉ��}�X�Ԃ��邩")]
    public int halfCellsY = 200;

    void Start()
    {
        if (flowField == null) return;

        // ���_��^�񒆂ɂ��� -half �` +half �܂ł��u������v�Ƃ��ēo�^
        for (int gx = -halfCellsX; gx <= halfCellsX; gx++)
        {
            for (int gy = -halfCellsY; gy <= halfCellsY; gy++)
            {
                float wx = gx * cellSize + cellSize * 0.5f;
                float wy = gy * cellSize + cellSize * 0.5f;
                flowField.MarkWalkable(wx, wy);
            }
        }

        // �������ł̓S�[�������߂Ȃ���
        // Base���������Ƃ��� BuildPlacement ����
        //     flowField.SetTargetWorld(basePos);
        // ���Ă΂�āA�����ŏ��߂ăS�[�������܂�

        flowField.Rebuild();
    }
}
