using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class GameOfLife : MonoBehaviour, IConvertGameObjectToEntity, IDeclareReferencedPrefabs
{
    public Vector2 StartSize;
    [Range(0, 1)]
    public float StartPercentageAlive;
    public bool StartRandomized;
    public float StartSpeed;
    public Vector2 SimulationSpeedRange;

    public TMP_InputField XSizeInput;
    public TMP_InputField YSizeInput;
    public TMP_InputField PercentageAlive;

    public Button Clear;
    public Button Randomize;

    public Button Play;
    public Button Pause;
    public Button Step;
    public Slider Speed;

    public Camera Camera;
    public Cell CellPrefab;
    public Material CellMaterial;

    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        XSizeInput.text = StartSize.x.ToString();
        YSizeInput.text = StartSize.y.ToString();
        PercentageAlive.text = StartPercentageAlive.ToString();

        Speed.value = (StartSpeed - SimulationSpeedRange.x) / (SimulationSpeedRange.y - SimulationSpeedRange.x);

        var startSize = new int2((int)StartSize.x, (int)StartSize.y);
        var input = new InputHook
        {
            Value = new InputData
            {
                Size = startSize,
                PercentageAlive = StartPercentageAlive,
                Clear = StartRandomized == false,
                Randomize = StartRandomized,
            }
        };

        XSizeInput.onValueChanged.AddListener((str) =>
        {
            if (int.TryParse(str, out var X))
                input.Value.Size.x = X;
        });
        YSizeInput.onValueChanged.AddListener((str) =>
        {
            if (int.TryParse(str, out var Y))
                input.Value.Size.y = Y;
        });
        PercentageAlive.onValueChanged.AddListener((str) =>
        {
            if (float.TryParse(str, out var value))
                input.Value.PercentageAlive = value;
        });

        Clear.onClick.AddListener(() => input.Value.Clear = true);
        Randomize.onClick.AddListener(() => input.Value.Randomize = true);

        Play.onClick.AddListener(() =>
        {
            input.Value.Playing = true;
            Play.interactable = false;
            Pause.interactable = true;
        });

        Pause.onClick.AddListener(() =>
        {
            input.Value.Playing = false;
            Play.interactable = true;
            Pause.interactable = false;
        });

        Pause.interactable = false;

        var gridControlPanel = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<GridControlPanelSystem>();
        gridControlPanel.Camera = Camera;
        gridControlPanel.CellMaterial = CellMaterial;
        gridControlPanel.SimulationSpeedRange = SimulationSpeedRange;
        gridControlPanel.Input = input;
        gridControlPanel.SetCellPrefab(conversionSystem.GetPrimaryEntity(CellPrefab));
        gridControlPanel.SetSpeed(Speed.value);

        Step.onClick.AddListener(() =>
        {
            input.Value.Playing = false;
            input.Value.QueStep = true;

            Play.interactable = true;
            Pause.interactable = false;
        });
        Speed.onValueChanged.AddListener((value) => gridControlPanel.SetSpeed(value));

        dstManager.AddComponentData(entity, new GridData
        {
            Size = startSize,
        });
    }

    public void DeclareReferencedPrefabs(List<GameObject> referencedPrefabs)
    {
        referencedPrefabs.Add(CellPrefab.gameObject);
    }
}

public class InputHook
{
    public InputData Value;
}
public struct InputData : IComponentData
{
    public int2 Size;
    public float PercentageAlive;
    public bool Clear;
    public bool Randomize;

    public bool Playing;
    public bool QueStep;
}

public struct GridData : IComponentData
{
    public int2 Size;
    public BlobAssetReference<CellReferences> ReferenceGrid;

    public Entity this[int2 position]
    {
        get
        {
            var x = Size.x * position.x;
            var index = x + position.y;

            if (index < 0 || index >= ReferenceGrid.Value.Entities.Length)
                return Entity.Null;

            return ReferenceGrid.Value.Entities[x + position.y];
        }
    }
}

public struct CellReferences
{
    public BlobArray<Entity> Entities;
}

class GridControlPanelSystem : SystemBase
{
    public Camera Camera;
    public InputHook Input;
    public Material CellMaterial;
    public Vector2 SimulationSpeedRange;

    private EntityQuery _RemoveQuery;
    private Entity _CellPrefab;
    private Plane _ZeroPlane;
    private float _Scale;
    private float _SimDeltaTime;
    private float _NextSimTime;

    public void SetCellPrefab(Entity prefab)
    {
        EntityManager.RemoveComponent<LinkedEntityGroup>(prefab);
        EntityManager.RemoveComponent<Translation>(prefab);
        EntityManager.RemoveComponent<Rotation>(prefab);
        EntityManager.RemoveComponent<Scale>(prefab);

        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_MotionVectorsParams>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_WorldTransformParams>(prefab);

        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHAb>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHAg>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHAr>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHBb>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHBg>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHBr>(prefab);
        EntityManager.RemoveComponent<BuiltinMaterialPropertyUnity_SHC>(prefab);

        EntityManager.RemoveComponent<AmbientProbeTag>(prefab);
        EntityManager.RemoveComponent<BlendProbeTag>(prefab);
        EntityManager.RemoveComponent<CustomProbeTag>(prefab);

        _CellPrefab = prefab;
    }

    public void SetSpeed(float value)
    {
        var speed = Mathf.Lerp(SimulationSpeedRange.x, SimulationSpeedRange.y, value);
        _SimDeltaTime = 1f / speed;
    }

    protected override void OnCreate()
    {
        _ZeroPlane = new Plane(Vector3.forward, 0);

        RequireSingletonForUpdate<GridData>();

        _RemoveQuery = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CellState>());
    }

    protected override void OnDestroy()
    {
        _RemoveQuery.Dispose();
    }

    protected override void OnUpdate()
    {
        var gridData = GetSingleton<GridData>();

        var inputData = Input.Value;
        if (inputData.Clear || inputData.Randomize)
        {
            if (gridData.ReferenceGrid.IsCreated)
                gridData.ReferenceGrid.Dispose();

            EntityManager.DestroyEntity(_RemoveQuery);

            gridData = CreateGrid(inputData);
            SetSingleton(gridData);

            var worldSize = CellMaterial.GetVector("_WorldSize");
            _Scale = gridData.Size.x / worldSize.x;
            CellMaterial.SetVector("_GridSize", new Vector4(gridData.Size.x, gridData.Size.y));

            Input.Value.Clear = false;
            Input.Value.Randomize = false;
        }

        var leftPressed = Mouse.current.leftButton.isPressed;
        var rightPressed = Mouse.current.rightButton.isPressed;
        var pressedWorldPosition = Vector3.zero;

        if (leftPressed || rightPressed)
        {
            var mousePosition = Mouse.current.position.ReadValue();
            var ray = Camera.ScreenPointToRay(mousePosition);
            if (_ZeroPlane.Raycast(ray, out var dist))
            {
                pressedWorldPosition = ray.GetPoint(dist) * _Scale;
            }

            Entities.ForEach((ref CellState state, ref CellData data) =>
            {
                if (data.Value.x < pressedWorldPosition.x && (data.Value.x + 1) > pressedWorldPosition.x &&
                    data.Value.y < pressedWorldPosition.y && (data.Value.y + 1) > pressedWorldPosition.y)
                {
                    if (leftPressed)
                    {
                        state.Alive = true;
                        data.Value.z = 1;
                    }
                    else if (rightPressed)
                    {
                        state.Alive = false;
                        data.Value.z = 0;
                    }
                }

            }).ScheduleParallel();
        }

        var time = (float)Time.ElapsedTime;
        if ((time > _NextSimTime && inputData.Playing) || inputData.QueStep)
        {
            Input.Value.QueStep = false;
            _NextSimTime = time + _SimDeltaTime;

            Entities.ForEach((ref CellState state, in CellData data) =>
            {
                var c00 = gridData[new int2((int)data.Value.x - 1, (int)data.Value.y - 1)];
                var c01 = gridData[new int2((int)data.Value.x - 1, (int)data.Value.y - 0)];
                var c02 = gridData[new int2((int)data.Value.x - 1, (int)data.Value.y + 1)];

                var c10 = gridData[new int2((int)data.Value.x - 0, (int)data.Value.y - 1)];
                var c12 = gridData[new int2((int)data.Value.x - 0, (int)data.Value.y + 1)];

                var c20 = gridData[new int2((int)data.Value.x + 1, (int)data.Value.y - 1)];
                var c21 = gridData[new int2((int)data.Value.x + 1, (int)data.Value.y - 0)];
                var c22 = gridData[new int2((int)data.Value.x + 1, (int)data.Value.y + 1)];

                var liveNeighbours = 0;

                if (c00 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c00).Value.z;
                if (c01 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c01).Value.z;
                if (c02 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c02).Value.z;

                if (c10 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c10).Value.z;
                if (c12 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c12).Value.z;

                if (c20 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c20).Value.z;
                if (c21 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c21).Value.z;
                if (c22 != Entity.Null) liveNeighbours += (int)GetComponent<CellData>(c22).Value.z;

                if (state.Alive)
                {
                    if (liveNeighbours < 2)
                        state.Alive = false;
                    else if (liveNeighbours > 3)
                        state.Alive = false;
                }
                else
                {
                    if (liveNeighbours == 3)
                        state.Alive = true;
                }

            }).ScheduleParallel();
        }

        Entities.ForEach((ref CellData data, in CellState state) =>
        {
            data.Value.z = state.Alive ? 1 : 0;

        }).ScheduleParallel();
    }

    private GridData CreateGrid(InputData input)
    {
        var data = new GridData
        {
            Size = input.Size,
        };

        var totalEntities = input.Size.x * input.Size.y;
        Debug.Log($"Total Entities: {totalEntities}");

        using (var entities = new NativeArray<Entity>(totalEntities, Allocator.TempJob))
        {
            EntityManager.RemoveComponent<LinkedEntityGroup>(_CellPrefab);
            EntityManager.Instantiate(_CellPrefab, entities);

            using (var builder = new BlobBuilder(Allocator.TempJob))
            {
                ref var root = ref builder.ConstructRoot<CellReferences>();
                var array = builder.Allocate(ref root.Entities, totalEntities);

                for (int i = 0; i < array.Length; i++)
                    array[i] = entities[i];

                data.ReferenceGrid = builder.CreateBlobAssetReference<CellReferences>(Allocator.Persistent);
            }

            var random = new Unity.Mathematics.Random((uint)UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            for (int x = 0; x < input.Size.x; x++)
            {
                for (int y = 0; y < input.Size.y; y++)
                {
                    var index = (x * input.Size.x) + y;
                    var alive = 0f;
                    if (input.Randomize && random.NextFloat() < input.PercentageAlive)
                        alive = 1f;

                    var cellData = new CellData { Value = new float3(x, y, alive) };
                    EntityManager.SetComponentData(entities[index], cellData);
                    EntityManager.SetComponentData(entities[index], new CellState { Alive = alive == 1 ? true : false });
                }
            }
        }

        return data;
    }
}