using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

public class Cell : MonoBehaviour, IConvertGameObjectToEntity
{
    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        dstManager.AddComponent<CellData>(entity);
        dstManager.AddComponent<CellState>(entity);
    }
}

[MaterialProperty("_Data", MaterialPropertyFormat.Float3)]
public struct CellData : IComponentData
{
    public float3 Value;
}

public struct CellState : IComponentData
{
    public bool Alive;
}