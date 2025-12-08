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

        // We'll first generate all chunk heightmaps, run erosion per-chunk,
        // then stitch edges between neighboring chunks by averaging shared vertices,
        // and finally create meshes for each chunk from the stitched heightmaps.

        int mapWidth = Width + 1; // nodes along X
        int mapHeight = Depth + 1; // nodes along Z
        int mapLen = mapWidth * mapHeight;

        var chunkMaps = new Dictionary<(int dx, int dz), (float[] map, int sampleOffsetX, int sampleOffsetZ)>();

        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                // Start by creating a NoiseAlgorithm for each chunk
                NoiseAlgorithm chunkNoise = new NoiseAlgorithm();
                chunkNoise.InitializeNoise(mapWidth, mapHeight, RandomSeed);

                // Offset the noise sampling
                int sampleOffsetX = dx * Width;
                int sampleOffsetZ = dz * Depth;
                chunkNoise.InitializePerlinNoise(Frequency, Amplitude, Octaves,
                    Lacunarity, Gain, Scale, NormalizeBias);

                // Fill a NativeArray then copy into managed array
                NativeArray<float> terrainHeightNative = new NativeArray<float>(mapLen, Allocator.Persistent);
                chunkNoise.setNoise(terrainHeightNative, sampleOffsetX, sampleOffsetZ);

                float[] managed = new float[mapLen];
                for (int i = 0; i < mapLen; i++) managed[i] = terrainHeightNative[i];
                terrainHeightNative.Dispose();

                // If erosion specified, run it on the managed heightmap
                if (erosion != null && erosionIterations > 0)
                {
                    int originalSeed = erosion.seed;
                    erosion.seed = RandomSeed + sampleOffsetX * 73856093 ^ sampleOffsetZ * 19349663;

                    // Erode: map uses indexing x * mapHeight + z
                    erosion.Erode(managed, mapWidth, mapHeight, erosionIterations, true);

                    erosion.seed = originalSeed;
                }

                chunkMaps[(dx, dz)] = (managed, sampleOffsetX, sampleOffsetZ);
            }
        }

        // Stitch seams: average shared vertices between neighboring chunks (right and forward neighbors)
        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                var key = (dx, dz);
                if (!chunkMaps.ContainsKey(key)) continue;

                var entry = chunkMaps[key].map;

                // Stitch with chunk to the +X (right)
                var rightKey = (dx + 1, dz);
                if (chunkMaps.ContainsKey(rightKey))
                {
                    var right = chunkMaps[rightKey].map;
                    // shared seam: left's x = Width, right's x = 0
                    int leftX = Width;
                    int rightX = 0;
                    for (int z = 0; z < mapHeight; z++)
                    {
                        int leftIdx = leftX * mapHeight + z;
                        int rightIdx = rightX * mapHeight + z;
                        float avg = (entry[leftIdx] + right[rightIdx]) * 0.5f;
                        entry[leftIdx] = avg;
                        right[rightIdx] = avg;
                    }
                }

                // Stitch with chunk to the +Z (forward)
                var forwardKey = (dx, dz + 1);
                if (chunkMaps.ContainsKey(forwardKey))
                {
                    var forward = chunkMaps[forwardKey].map;
                    // shared seam: current's z = Depth, forward's z = 0
                    int zCurrent = Depth;
                    int zForward = 0;
                    for (int x = 0; x < mapWidth; x++)
                    {
                        int idxCurrent = x * mapHeight + zCurrent;
                        int idxForward = x * mapHeight + zForward;
                        float avg = (entry[idxCurrent] + forward[idxForward]) * 0.5f;
                        entry[idxCurrent] = avg;
                        forward[idxForward] = avg;
                    }
                }
            }
        }

        // Create meshes from stitched heightmaps
        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                var key = (dx, dz);
                if (!chunkMaps.ContainsKey(key)) continue;

                var tuple = chunkMaps[key];
                float[] managed = tuple.map;
                int sampleOffsetX = tuple.sampleOffsetX;
                int sampleOffsetZ = tuple.sampleOffsetZ;

                NativeArray<float> terrainHeightMap = new NativeArray<float>(mapLen, Allocator.Persistent);
                for (int i = 0; i < mapLen; i++) terrainHeightMap[i] = managed[i];

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

                // free managed map to allow GC
                // (we could Remove key but we'll let scope end)
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
