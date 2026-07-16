// Manual test for LoadFromPlyBytes.
// Add to a GameObject together with GsplatRenderer, set PlyPath (absolute, or relative
// to StreamingAssets), and enter Play mode. The PLY is read with File.ReadAllBytes and
// loaded via LoadFromPlyBytes — no importer / asset file involved — then rendered.

using System.IO;
using UnityEngine;

namespace Gsplat
{
    [RequireComponent(typeof(GsplatRenderer))]
    public class RuntimePlyBytesLoaderTest : MonoBehaviour
    {
        [Tooltip("Absolute path, or relative to StreamingAssets")]
        public string PlyPath;

        public CompressionMode Compression = CompressionMode.Spark;
        public SourceCoordinates SourceCoordinates = SourceCoordinates.RUB;

        void Start()
        {
            var path = Path.IsPathRooted(PlyPath)
                ? PlyPath
                : Path.Combine(Application.streamingAssetsPath, PlyPath);
            if (!File.Exists(path))
            {
                Debug.LogError($"PLY not found: {path}");
                return;
            }

            // Simulates data arriving as a byte array (e.g. downloaded at runtime).
            var bytes = File.ReadAllBytes(path);

            GsplatAsset asset = Compression == CompressionMode.Spark
                ? ScriptableObject.CreateInstance<GsplatAssetSpark>()
                : ScriptableObject.CreateInstance<GsplatAssetUncompressed>();
            asset.LoadFromPlyBytes(bytes, null, SourceCoordinates);

            GetComponent<GsplatRenderer>().GsplatAsset = asset;
            Debug.Log($"Loaded {asset.SplatCount} splats (SH bands {asset.SHBands}) " +
                      $"from {bytes.Length} bytes via LoadFromPlyBytes");
        }
    }
}
