import trimesh
import numpy as np
from scipy.ndimage import gaussian_filter
from scipy.spatial import cKDTree
from skimage import measure
from plyfile import PlyData

def main():
    input_file = r"C:\GitHub\Wide Area AR Attempt 4\Wide Area AT Attempt 4\Assets\Prefab\July13.ply"
    output_file = r"C:\GitHub\Wide Area AR Attempt 4\Wide Area AT Attempt 4\Assets\Prefab\July13_SolidMesh.obj"

    print("Loading point cloud with plyfile...")
    try:
        plydata = PlyData.read(input_file)
    except Exception as e:
        print(f"Error loading point cloud: {e}")
        return

    print("Extracting vertices and colors...")
    vertex_data = plydata['vertex']
    x = vertex_data['x']
    y = vertex_data['y']
    z = vertex_data['z']
    vertices = np.vstack((x, y, z)).T

    try:
        f_dc_0 = vertex_data['f_dc_0']
        f_dc_1 = vertex_data['f_dc_1']
        f_dc_2 = vertex_data['f_dc_2']
        
        # Standard Spherical Harmonics 0 to RGB conversion
        SH_C0 = 0.28209479177387814
        r = f_dc_0 * SH_C0 + 0.5
        g = f_dc_1 * SH_C0 + 0.5
        b = f_dc_2 * SH_C0 + 0.5
        
        r = np.clip(r, 0.0, 1.0)
        g = np.clip(g, 0.0, 1.0)
        b = np.clip(b, 0.0, 1.0)
        
        colors = np.vstack((r, g, b, np.ones_like(r))).T * 255.0
        colors = colors.astype(np.uint8)
        print("Colors successfully extracted and converted.")
    except Exception as e:
        print(f"Warning: Failed to extract custom colors ({e}). Mesh will be uncolored.")
        colors = np.ones((len(vertices), 4), dtype=np.uint8) * 255
        
    # Downsample points to avoid OOM when building KDTree
    max_pts = 3000000
    if len(vertices) > max_pts:
        print(f"Downsampling from {len(vertices)} to {max_pts} points...")
        indices = np.random.choice(len(vertices), max_pts, replace=False)
        vertices = vertices[indices]
        colors = colors[indices]

    print("Computing bounds...")
    min_bound = np.min(vertices, axis=0)
    max_bound = np.max(vertices, axis=0)
    
    # Pad bounds slightly
    padding = (max_bound - min_bound) * 0.05
    min_bound -= padding
    max_bound += padding

    # Define voxel grid resolution
    grid_size = 384
    print(f"Voxelizing into a {grid_size}^3 grid...")
    
    diff = max_bound - min_bound
    step = np.max(diff) / grid_size
    
    dims = np.ceil(diff / step).astype(int) + 1
    idx = np.floor((vertices - min_bound) / step).astype(int)
    
    volume = np.zeros(dims, dtype=np.float32)
    
    valid_idx = (idx[:, 0] >= 0) & (idx[:, 0] < dims[0]) & \
                (idx[:, 1] >= 0) & (idx[:, 1] < dims[1]) & \
                (idx[:, 2] >= 0) & (idx[:, 2] < dims[2])
    idx = idx[valid_idx]
    
    volume[idx[:, 0], idx[:, 1], idx[:, 2]] = 1.0

    print("Applying smoothing to create solid surfaces...")
    volume_smooth = gaussian_filter(volume, sigma=1.5)

    print("Extracting surface mesh (Marching Cubes)...")
    try:
        verts_mc, faces_mc, normals_mc, values_mc = measure.marching_cubes(volume_smooth, level=0.03)
    except Exception as e:
        print(f"Marching cubes failed: {e}")
        return

    verts_world = verts_mc * step + min_bound

    print("Transferring colors from point cloud to mesh (KDTree)...")
    tree = cKDTree(vertices)
    distances, closest_indices = tree.query(verts_world, k=1)
    
    mesh_colors = colors[closest_indices]

    print("Saving OBJ file...")
    mesh = trimesh.Trimesh(vertices=verts_world, faces=faces_mc, vertex_colors=mesh_colors)
    mesh.export(output_file)
    
    print(f"Done! Saved mesh with {len(verts_world)} vertices and {len(faces_mc)} faces to {output_file}")

if __name__ == "__main__":
    main()
