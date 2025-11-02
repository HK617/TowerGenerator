using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class SaveData
{
    // ゲーム全体
    public bool baseBuilt;

    // シーン上に置かれている建物（完成・ゴースト問わず）
    public List<PlacedBuildingData> buildings = new();

    // キューにたまっているドローンタスク
    public List<DroneTaskData> queuedTasks = new();

    // いま存在している各ドローンの状態
    public List<DroneRuntimeData> drones = new();
}

[Serializable]
public class PlacedBuildingData
{
    public string defName;
    public Vector3 position;
    public bool fine;
    public bool isBase;
}

[Serializable]
public class DroneTaskData
{
    public string kind;      // "Big" or "Fine"
    public string defName;
    public Vector3 worldPos;
    public Vector3Int bigCell;
    public Vector2Int fineCell;
    public bool ghost;       // trueならロード時にゴーストとして生成
}

[Serializable]
public class DroneRuntimeData
{
    public string name;
    public Vector3 position;
    public string state;         // "Idle", "MovingToTarget", "Working"
    public float workProgress;   // 0〜1
    public float workTimer;      // ある程度元に戻す用
    public DroneTaskData task;   // いま持ってるタスク（nullのこともある）
}
