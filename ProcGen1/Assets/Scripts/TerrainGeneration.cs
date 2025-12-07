using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;
using UnityEngine.Rendering;

public class TerrainGeneration : MonoBehaviour
{
    public int RandomSeed;
    public int Width;
    public int Depth;
    public int MaxHeight;
    public Material TerrainMaterial;
    public float Frequency = 1.0f;
    public float Amplitude = 0.5f;
    public float Lacunarity = 2.0f;
    public float Gain = 0.5f;
    public int Octaves = 8;
    public float Scale = 0.01f;
    public float NormalizeBias = 1.0f;

    [Header("Terrain Thresholds")]
    public float snowStart = 3f;
    public float rockStart = 0f;
    public float grassStart = -5f;
    public float sandStart = -10f;
    
    public ObjectGenerator objectGenerator;
    public int generationRadius = 2; // desired radius (1 = 1 chunk, 2 = 3x3, 3 = 5x5, etc.)

    [Header("Erosion")]
    public Erosion erosion; // optional erosion component
    public int erosionIterations = 0; // number of droplets to simulate per chunk

    private GameObject mRealTerrain;
    private NoiseAlgorithm mTerrainNoise;
    private GameObject mLight;
    
    // code to get rid of fog from: https://forum.unity.com/threads/how-do-i-turn-off-fog-on-a-specific-camera-using-urp.1373826/
    // Unity calls this method automatically when it enables this component
    private void OnEnable()
    {
        // Add WriteLogMessage as a delegate of the RenderPipelineManager.beginCameraRendering event
        RenderPipelineManager.beginCameraRendering += BeginRender;
        RenderPipelineManager.endCameraRendering += EndRender;
    }
 
    // Unity calls this method automatically when it disables this component
    private void OnDisable()
    {
        // Remove WriteLogMessage as a delegate of the  RenderPipelineManager.beginCameraRendering event
        RenderPipelineManager.beginCameraRendering -= BeginRender;
        RenderPipelineManager.endCameraRendering -= EndRender;
    }
 
    // When this method is a delegate of RenderPipeline.beginCameraRendering event, Unity calls this method every time it raises the beginCameraRendering event
    void BeginRender(ScriptableRenderContext context, Camera camera)
    {
        if(camera.name == "Main Camera No Fog")
        {
            //Debug.Log("Turn fog off");
            RenderSettings.fog = false;
        }
         
    }
 
    void EndRender(ScriptableRenderContext context, Camera camera)
    {
        if (camera.name == "Main Camera No Fog")
        {
            //Debug.Log("Turn fog on");
            RenderSettings.fog = true;
        }
    }

    // Start is called before the first frame update
    void Start()
    {
        int half = generationRadius - 1;

        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                // Start by creating a NoiseAlgorithm for each chunk
                NoiseAlgorithm chunkNoise = new NoiseAlgorithm();
                chunkNoise.InitializeNoise(Width + 1, Depth + 1, RandomSeed);

                // Offset the noise sampling
                int sampleOffsetX = dx * Width;
                int sampleOffsetZ = dz * Depth;
                chunkNoise.InitializePerlinNoise(Frequency, Amplitude, Octaves, 
                    Lacunarity, Gain, Scale, NormalizeBias);
                NativeArray<float> terrainHeightMap = new NativeArray<float>((Width + 1) * (Depth + 1), Allocator.Persistent);
                chunkNoise.setNoise(terrainHeightMap, sampleOffsetX, sampleOffsetZ);

                // If erosion specified, run it on a managed copy of the heightmap
                if (erosion != null && erosionIterations > 0)
                {
                    int mapSize = Width + 1; // number of nodes along one side
                    int len = terrainHeightMap.Length;
                    float[] managed = new float[len];
                    for (int i = 0; i < len; i++) managed[i] = terrainHeightMap[i];

                    // Use a deterministic per-chunk seed so neighboring chunks differ
                    int originalSeed = erosion.seed;
                    erosion.seed = RandomSeed + sampleOffsetX * 73856093 ^ sampleOffsetZ * 19349663;

                    // Erode: map uses indexing x * mapSize + z
                    erosion.Erode(managed, mapSize, erosionIterations, true);

                    // restore seed
                    erosion.seed = originalSeed;

                    // copy back into NativeArray
                    for (int i = 0; i < len; i++) terrainHeightMap[i] = managed[i];
                }

                // Create the mesh and set it to a new terrain GameObject
                GameObject chunkTerrain = GameObject.CreatePrimitive(PrimitiveType.Cube);
                chunkTerrain.transform.position = new Vector3(sampleOffsetX, 0, sampleOffsetZ);
                MeshRenderer meshRenderer = chunkTerrain.GetComponent<MeshRenderer>();
                MeshFilter meshFilter = chunkTerrain.GetComponent<MeshFilter>();
                meshRenderer.material = TerrainMaterial;
                meshFilter.mesh = GenerateTerrainMesh(terrainHeightMap);
                terrainHeightMap.Dispose();

                // Add collider
                MeshCollider meshCollider = chunkTerrain.GetComponent<MeshCollider>();
                if (meshCollider == null)
                {
                    meshCollider = chunkTerrain.AddComponent<MeshCollider>();
                }
                meshCollider.sharedMesh = meshFilter.mesh;

                // Generate objects for this chunk (world-space bounds)
                float areaXMin = sampleOffsetX;
                float areaXMax = sampleOffsetX + Width;
                float areaZMin = sampleOffsetZ;
                float areaZMax = sampleOffsetZ + Depth;
                objectGenerator.GenerateObjects(areaXMin, areaXMax, areaZMin, areaZMax);
            }
        }
        NoiseAlgorithm.OnExit();
    }

    private void Update()
    {
      
    }

    // create a new mesh with
    // perlin noise
    // makes a quad and connects it with the next quad
    // uses whatever texture the material is given
    public Mesh GenerateTerrainMesh(NativeArray<float> heightMap)
    {
        int width = Width + 1, depth = Depth + 1;
        int height = MaxHeight;

        Mesh terrainMesh = new Mesh();
        terrainMesh.indexFormat = IndexFormat.UInt32; // support large meshes

        List<Vector3> vert = new List<Vector3>(width * depth * 4);
        List<int> indices = new List<int>(width * depth * 6);
        List<Vector2> uvs = new List<Vector2>(width * depth * 4);

        int vertexIndex = 0;
        const float inset = 0.001f; // small inset to avoid bilinear bleeding between atlas tiles

        for (int x = 0; x < width - 1; x++)
        {
            for (int z = 0; z < depth - 1; z++)
            {
                // heights for this quad's corners
                float y00 = heightMap[(x) * (width) + (z)] * height - (MaxHeight / 2.0f);
                float y10 = heightMap[(x + 1) * (width) + (z)] * height - (MaxHeight / 2.0f);
                float y01 = heightMap[(x) * (width) + (z + 1)] * height - (MaxHeight / 2.0f);
                float y11 = heightMap[(x + 1) * (width) + (z + 1)] * height - (MaxHeight / 2.0f);

                // add four vertices for this quad (positions)
                vert.Add(new Vector3(x, y00, z));           // v00
                vert.Add(new Vector3(x, y01, z + 1));       // v01
                vert.Add(new Vector3(x + 1, y10, z));       // v10
                vert.Add(new Vector3(x + 1, y11, z + 1));   // v11

                // pick tile based on average height of the quad to avoid mixing tiles across a single quad
                float avgY = (y00 + y01 + y10 + y11) * 0.25f;

                float u0, v0, u1, v1;
                if (avgY >= snowStart)
                {
                    u0 = 0f; v0 = 0.75f; u1 = 0.25f; v1 = 1f;
                }
                else if (avgY >= rockStart)
                {
                    u0 = 0.5f; v0 = 0.5f; u1 = 0.75f; v1 = 0.75f;
                }
                else if (avgY >= grassStart)
                {
                    u0 = 0.75f; v0 = 0f; u1 = 1f; v1 = 0.25f;
                }
                else
                {
                    u0 = 0.75f; v0 = 0.5f; u1 = 1f; v1 = 0.75f;
                }

                // inset UVs slightly to avoid sampling neighboring tiles due to filtering
                u0 += inset; v0 += inset; u1 -= inset; v1 -= inset;

                // assign same tile corners to all four vertices of the quad
                uvs.Add(new Vector2(u0, v0)); // v00
                uvs.Add(new Vector2(u0, v1)); // v01
                uvs.Add(new Vector2(u1, v0)); // v10
                uvs.Add(new Vector2(u1, v1)); // v11

                // add indices for two triangles (v00, v01, v10) and (v10, v01, v11)
                indices.Add(vertexIndex);
                indices.Add(vertexIndex + 1);
                indices.Add(vertexIndex + 2);

                indices.Add(vertexIndex + 2);
                indices.Add(vertexIndex + 1);
                indices.Add(vertexIndex + 3);

                vertexIndex += 4;
            }
        }

        terrainMesh.vertices = vert.ToArray();
        terrainMesh.triangles = indices.ToArray();
        terrainMesh.SetUVs(0, uvs);

        terrainMesh.RecalculateNormals();
        terrainMesh.RecalculateBounds();

        return terrainMesh;
    }

}
