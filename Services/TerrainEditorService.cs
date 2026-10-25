using BullerStudio.Models;

namespace BullerStudio.Services;

public static class TerrainEditorService
{
    private const int CellSize = 4;
    private const int MaxTerrainCells = 100_000;

    public static IReadOnlyList<SceneNode> FillBlock(GameProject project, float[] position, float[] size, string material) =>
        Fill(project, position, size, material, spherical: false);

    public static IReadOnlyList<SceneNode> FillBall(GameProject project, float[] position, float radius, string material) =>
        Fill(project, position, [radius * 2, radius * 2, radius * 2], material, spherical: true);

    public static IReadOnlyList<SceneNode> FillCylinder(GameProject project, float[] position, float[] size, float[] rotation, string material) =>
        FillRotated(project, position, size, rotation, material, wedge: false);

    public static IReadOnlyList<SceneNode> FillWedge(GameProject project, float[] position, float[] size, float[] rotation, string material) =>
        FillRotated(project, position, size, rotation, material, wedge: true);

    public static IReadOnlyList<SceneNode> Clear(GameProject project)
    {
        var terrain = project.Workspace.Children.FirstOrDefault(node => node.Type == "Terrain");
        if (terrain is null) return [];
        var removed = terrain.Children.Where(IsTerrainCell).ToArray();
        foreach (var cell in removed) terrain.Children.Remove(cell);
        return removed;
    }

    private static IReadOnlyList<SceneNode> FillRotated(GameProject project, float[] position, float[] size, float[] rotation, string material, bool wedge)
    {
        if (position.Length != 3 || rotation.Length != 3
            || !position.All(value => float.IsFinite(value) && Math.Abs(value) <= 1_000_000)
            || !rotation.All(value => float.IsFinite(value) && Math.Abs(value) <= 36000)
            || !ValidSize(size) || !SceneNodeTypes.IsTerrainMaterial(material) || material == "Air") return [];
        var transform = RotationMath.FromDegrees(rotation);
        var inverse = System.Numerics.Matrix4x4.Transpose(transform);
        var bounds = GetRotatedBounds(position, size, transform);
        bool Contains(float[] cellPosition)
        {
            var local = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(
                cellPosition[0] - position[0], cellPosition[1] - position[1], cellPosition[2] - position[2]), inverse);
            var x = local.X / (size[0] * .5f);
            var y = local.Y / (size[1] * .5f);
            var z = local.Z / (size[2] * .5f);
            return wedge
                ? Math.Abs(x) <= .5f && Math.Abs(z) <= .5f && y >= -.5f && y <= -x
                : Math.Abs(y) <= 1 && x * x + z * z <= 1;
        }
        return Fill(project, bounds.Position, bounds.Size, material, spherical: false, contains: Contains);
    }

    private static IReadOnlyList<SceneNode> Fill(GameProject project, float[] position, float[] size, string material, bool spherical, Func<float[], bool>? contains = null)
    {
        if (position.Length != 3 || !ValidSize(size)
            || !position.All(value => float.IsFinite(value) && Math.Abs(value) <= 1_000_000)
            || !SceneNodeTypes.IsTerrainMaterial(material) || material == "Air") return [];
        var terrain = project.Workspace.Children.FirstOrDefault(node => node.Type == "Terrain");
        if (terrain is null) return [];
        var minimum = new int[3];
        var maximum = new int[3];
        for (var axis = 0; axis < 3; axis++)
        {
            minimum[axis] = (int)Math.Floor((position[axis] - size[axis] * .5f + CellSize * .5f) / CellSize);
            maximum[axis] = (int)Math.Ceiling((position[axis] + size[axis] * .5f + CellSize * .5f) / CellSize) - 1;
        }
        var cellCount = (long)(maximum[0] - minimum[0] + 1) * (maximum[1] - minimum[1] + 1) * (maximum[2] - minimum[2] + 1);
        if (cellCount <= 0 || cellCount > MaxTerrainCells) return [];
        var existing = terrain.Children.Where(IsTerrainCell).GroupBy(node => node.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var radius = size[0] * .5f;
        var radiusSquared = radius * radius;
        var changed = new List<SceneNode>();
        for (var x = minimum[0]; x <= maximum[0]; x++)
        for (var y = minimum[1]; y <= maximum[1]; y++)
        for (var z = minimum[2]; z <= maximum[2]; z++)
        {
            var cellPosition = new[] { (float)(x * CellSize), (float)(y * CellSize), (float)(z * CellSize) };
            if (spherical && !CellIntersectsSphere(position, radiusSquared, cellPosition)) continue;
            if (contains is not null && !contains(cellPosition)) continue;
            var name = $"TerrainCell_{x}_{y}_{z}";
            if (!existing.TryGetValue(name, out var cell))
            {
                cell = new SceneNode
                {
                    Type = "Part", Name = name, Position = cellPosition,
                    Size = [4f, 4f, 4f], Anchored = true,
                    CanCollide = true, IsExplorerVisible = false
                };
                terrain.Children.Add(cell);
                existing.Add(name, cell);
            }
            cell.Material = material;
            cell.Color = SceneNodeTypes.TerrainColor(material);
            changed.Add(cell);
        }
        return changed;
    }

    private static bool IsTerrainCell(SceneNode node) =>
        node.Type == "Part" && node.Name.StartsWith("TerrainCell_", StringComparison.Ordinal);

    private static bool ValidSize(float[] size) => size.Length == 3
        && size.All(value => float.IsFinite(value) && value > 0 && value <= 1024);

    private static (float[] Position, float[] Size) GetRotatedBounds(float[] position, float[] size, System.Numerics.Matrix4x4 transform)
    {
        var minimum = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        var maximum = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(size[0] * x * .5f, size[1] * y * .5f, size[2] * z * .5f), transform);
            var values = new[] { position[0] + corner.X, position[1] + corner.Y, position[2] + corner.Z };
            for (var axis = 0; axis < 3; axis++)
            {
                minimum[axis] = Math.Min(minimum[axis], values[axis]);
                maximum[axis] = Math.Max(maximum[axis], values[axis]);
            }
        }
        return (Enumerable.Range(0, 3).Select(axis => (minimum[axis] + maximum[axis]) * .5f).ToArray(),
            Enumerable.Range(0, 3).Select(axis => maximum[axis] - minimum[axis]).ToArray());
    }

    private static bool CellIntersectsSphere(float[] center, float radiusSquared, float[] cellPosition)
    {
        var halfCell = CellSize * .5f;
        var dx = center[0] - Math.Clamp(center[0], cellPosition[0] - halfCell, cellPosition[0] + halfCell);
        var dy = center[1] - Math.Clamp(center[1], cellPosition[1] - halfCell, cellPosition[1] + halfCell);
        var dz = center[2] - Math.Clamp(center[2], cellPosition[2] - halfCell, cellPosition[2] + halfCell);
        return dx * dx + dy * dy + dz * dz <= radiusSquared;
    }
}
