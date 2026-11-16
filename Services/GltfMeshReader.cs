using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text.Json;
using HelixToolkit.SharpDX.Core;
using BullerStudio.Models;
using Vector3 = System.Numerics.Vector3;
using SharpVector3 = SharpDX.Vector3;

namespace BullerStudio.Services;

/// <summary>Reads triangle geometry from glTF 2.0 JSON and binary GLB assets.</summary>
internal static class GltfMeshReader
{
    private const int MaxAssetBytes = 512 * 1024 * 1024;
    private const int MaxVertices = 5_000_000;

    internal sealed record MaterialFactors(float Red, float Green, float Blue, float Alpha, float Metallic, float Roughness,
        string BaseColorTexture, byte[]? BaseColorTextureData, string NormalTexture, byte[]? NormalTextureData,
        string MetallicRoughnessTexture, byte[]? MetallicRoughnessTextureData,
        float EmissiveRed, float EmissiveGreen, float EmissiveBlue, float EmissiveStrength,
        string EmissiveTexture, byte[]? EmissiveTextureData, string AlphaMode, float AlphaCutoff);

    public static MeshBuilder Read(string path, GameProject project, out MaterialFactors? materialFactors)
    {
        materialFactors = null;
        if (new FileInfo(path).Length > MaxAssetBytes) throw new InvalidDataException("The glTF asset has an unsupported size.");
        var file = File.ReadAllBytes(path);
        if (file.Length < 4 || file.Length > MaxAssetBytes) throw new InvalidDataException("The glTF asset has an unsupported size.");
        byte[]? binaryChunk = null;
        JsonDocument document;
        if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase))
        {
            if (file.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0, 4)) != 0x46546C67
                || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4, 4)) != 2
                || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8, 4)) != file.Length)
                throw new InvalidDataException("The GLB header is invalid.");
            ReadOnlySpan<byte> json = default;
            var offset = 12;
            while (offset + 8 <= file.Length)
            {
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset, 4)));
                var type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset + 4, 4));
                offset += 8;
                if (length < 0 || offset + length > file.Length) throw new InvalidDataException("A GLB chunk exceeds the file bounds.");
                if (type == 0x4E4F534A && json.IsEmpty) json = file.AsSpan(offset, length);
                else if (type == 0x004E4942 && binaryChunk is null) binaryChunk = file.AsSpan(offset, length).ToArray();
                offset += length;
            }
            if (json.IsEmpty) throw new InvalidDataException("The GLB has no JSON chunk.");
            document = JsonDocument.Parse(json.ToArray());
        }
        else document = JsonDocument.Parse(file);

        using (document)
        {
            var root = document.RootElement;
            var buffers = ReadBuffers(root, binaryChunk, Path.GetDirectoryName(path)!, project);
            var meshes = root.GetProperty("meshes");
            MaterialFactors? parsedMaterialFactors = null;
            var accessors = root.GetProperty("accessors");
            var views = root.GetProperty("bufferViews");
            var nodes = root.GetProperty("nodes");
            var builder = new MeshBuilder();
            var visited = new HashSet<int>();
            var triangleCount = 0;

            void Visit(int nodeIndex, Matrix4x4 parentTransform, int depth)
            {
                if (depth > 128 || nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength() || !visited.Add(nodeIndex)) return;
                var node = nodes[nodeIndex];
                var transform = ReadNodeTransform(node) * parentTransform;
                if (TryGetInt(node, "mesh", out var meshIndex) && meshIndex >= 0 && meshIndex < meshes.GetArrayLength())
                {
                    foreach (var primitive in meshes[meshIndex].GetProperty("primitives").EnumerateArray())
                    {
                        if (parsedMaterialFactors is null && TryGetInt(primitive, "material", out var materialIndex))
                            parsedMaterialFactors = ReadMaterialFactors(root, materialIndex, path, project, buffers);
                        var mode = TryGetInt(primitive, "mode", out var primitiveMode) ? primitiveMode : 4;
                        if (mode is not (4 or 5 or 6)) continue;
                        if (!primitive.TryGetProperty("attributes", out var attributes) || !TryGetInt(attributes, "POSITION", out var positionIndex)) continue;
                        var positions = ReadPositions(positionIndex, accessors, views, buffers);
                        var indices = primitive.TryGetProperty("indices", out var indexElement) && indexElement.TryGetInt32(out var accessorIndex)
                            ? ReadIndices(accessorIndex, accessors, views, buffers)
                            : Enumerable.Range(0, positions.Length).ToArray();
                        var triangles = mode == 4 ? indices : Triangulate(indices, mode);
                        if (triangles.Length % 3 != 0) throw new InvalidDataException("A glTF triangle index list is incomplete.");
                        triangleCount = checked(triangleCount + triangles.Length / 3);
                        if (triangleCount > MaxVertices) throw new InvalidDataException("The glTF mesh contains too many triangles.");
                        for (var index = 0; index < triangles.Length; index += 3)
                        {
                            var a = Transform(positions, triangles[index], transform);
                            var b = Transform(positions, triangles[index + 1], transform);
                            var c = Transform(positions, triangles[index + 2], transform);
                            builder.AddTriangle(a, b, c);
                        }
                    }
                }
                if (node.TryGetProperty("children", out var children))
                    foreach (var child in children.EnumerateArray()) if (child.TryGetInt32(out var childIndex)) Visit(childIndex, transform, depth + 1);
            }

            var scenes = root.TryGetProperty("scenes", out var sceneList) ? sceneList : default;
            if (scenes.ValueKind == JsonValueKind.Array && scenes.GetArrayLength() > 0)
            {
                var sceneIndex = TryGetInt(root, "scene", out var defaultScene) ? defaultScene : 0;
                if (sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength()) sceneIndex = 0;
                foreach (var node in scenes[sceneIndex].GetProperty("nodes").EnumerateArray())
                    if (node.TryGetInt32(out var nodeIndex)) Visit(nodeIndex, Matrix4x4.Identity, 0);
            }
            else
            {
                var childIndices = nodes.EnumerateArray()
                    .Where(node => node.TryGetProperty("children", out _))
                    .SelectMany(node => node.GetProperty("children").EnumerateArray())
                    .Where(child => child.TryGetInt32(out _)).Select(child => child.GetInt32()).ToHashSet();
                for (var nodeIndex = 0; nodeIndex < nodes.GetArrayLength(); nodeIndex++)
                    if (!childIndices.Contains(nodeIndex)) Visit(nodeIndex, Matrix4x4.Identity, 0);
            }

            if (triangleCount == 0) throw new InvalidDataException("The glTF asset contains no triangle meshes.");
            materialFactors = parsedMaterialFactors;
            return builder;
        }
    }

    private static MaterialFactors? ReadMaterialFactors(JsonElement root, int materialIndex, string modelPath, GameProject project, byte[][] buffers)
    {
        if (!root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array) return null;
        if (materialIndex < 0 || materialIndex >= materials.GetArrayLength()) return null;
        var material = materials[materialIndex];
        var alphaMode = material.TryGetProperty("alphaMode", out var alphaModeElement)
            && alphaModeElement.ValueKind == JsonValueKind.String
            && alphaModeElement.GetString() is "BLEND" or "MASK"
            ? alphaModeElement.GetString()! : "OPAQUE";
        var alphaCutoff = material.TryGetProperty("alphaCutoff", out var alphaCutoffElement)
            && alphaCutoffElement.TryGetSingle(out var cutoff) && float.IsFinite(cutoff) && cutoff >= 0
            ? Math.Clamp(cutoff, 0, 1) : .5f;
        var color = new[] { 1f, 1f, 1f, 1f };
        var metallic = 1f;
        var roughness = 1f;
        var baseColorTexture = string.Empty;
        byte[]? baseColorTextureData = null;
        var metallicRoughnessTexture = string.Empty;
        byte[]? metallicRoughnessTextureData = null;
        var normalTexture = string.Empty;
        if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            if (pbr.TryGetProperty("baseColorFactor", out var factor) && factor.ValueKind == JsonValueKind.Array)
            {
                var values = factor.EnumerateArray().Select(value => value.TryGetSingle(out var number) ? number : float.NaN).ToArray();
                if (values.Length == 4 && values.All(float.IsFinite)) color = values.Select(value => Math.Clamp(value, 0, 1)).ToArray();
            }
            metallic = ReadMaterialFactor(pbr, "metallicFactor", 1);
            roughness = ReadMaterialFactor(pbr, "roughnessFactor", 1);
            if (pbr.TryGetProperty("baseColorTexture", out var baseColorInfo))
            {
                baseColorTexture = ReadTexturePath(root, baseColorInfo, modelPath, project);
                baseColorTextureData = ReadEmbeddedTexture(root, baseColorInfo, buffers);
            }
            if (pbr.TryGetProperty("metallicRoughnessTexture", out var metallicRoughnessInfo))
            {
                metallicRoughnessTexture = ReadTexturePath(root, metallicRoughnessInfo, modelPath, project);
                metallicRoughnessTextureData = ReadEmbeddedTexture(root, metallicRoughnessInfo, buffers);
            }
        }
        byte[]? normalTextureData = null;
        if (material.TryGetProperty("normalTexture", out var normalInfo))
        {
            normalTexture = ReadTexturePath(root, normalInfo, modelPath, project);
            normalTextureData = ReadEmbeddedTexture(root, normalInfo, buffers);
        }
        var emissive = new[] { 0f, 0f, 0f };
        if (material.TryGetProperty("emissiveFactor", out var emissiveFactor) && emissiveFactor.ValueKind == JsonValueKind.Array)
        {
            var values = emissiveFactor.EnumerateArray().Select(value => value.TryGetSingle(out var number) ? number : float.NaN).ToArray();
            if (values.Length == 3 && values.All(float.IsFinite)) emissive = values.Select(value => Math.Clamp(value, 0, 1)).ToArray();
        }
        var emissiveStrength = 1f;
        if (material.TryGetProperty("extensions", out var extensions)
            && extensions.TryGetProperty("KHR_materials_emissive_strength", out var emissiveExtension))
            emissiveStrength = emissiveExtension.TryGetProperty("emissiveStrength", out var strengthElement)
                && strengthElement.TryGetSingle(out var strength) && float.IsFinite(strength) && strength >= 0
                ? Math.Clamp(strength, 0, 1000) : 1;
        var emissiveTexture = string.Empty;
        byte[]? emissiveTextureData = null;
        if (material.TryGetProperty("emissiveTexture", out var emissiveInfo))
        {
            emissiveTexture = ReadTexturePath(root, emissiveInfo, modelPath, project);
            emissiveTextureData = ReadEmbeddedTexture(root, emissiveInfo, buffers);
        }
        return new MaterialFactors(color[0], color[1], color[2], color[3], metallic, roughness,
            baseColorTexture, baseColorTextureData, normalTexture, normalTextureData,
            metallicRoughnessTexture, metallicRoughnessTextureData, emissive[0], emissive[1], emissive[2], emissiveStrength,
            emissiveTexture, emissiveTextureData, alphaMode, alphaCutoff);
    }

    private static byte[]? ReadEmbeddedTexture(JsonElement root, JsonElement textureInfo, byte[][] buffers)
    {
        if (textureInfo.ValueKind != JsonValueKind.Object
            || !TryGetInt(textureInfo, "index", out var textureIndex)
            || !root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array
            || textureIndex < 0 || textureIndex >= textures.GetArrayLength()
            || !textures[textureIndex].TryGetProperty("source", out var sourceElement) || !sourceElement.TryGetInt32(out var imageIndex)
            || !root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array
            || imageIndex < 0 || imageIndex >= images.GetArrayLength()) return null;

        var image = images[imageIndex];
        if (image.TryGetProperty("uri", out var uriElement) && uriElement.ValueKind == JsonValueKind.String)
        {
            var uri = uriElement.GetString() ?? string.Empty;
            if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            var comma = uri.IndexOf(',');
            if (comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            var encoded = uri[(comma + 1)..];
            if (encoded.Length > (MaxAssetBytes * 4L / 3) + 8) throw new InvalidDataException("An embedded glTF image is too large.");
            var data = Convert.FromBase64String(encoded);
            if (data.Length > MaxAssetBytes) throw new InvalidDataException("An embedded glTF image is too large.");
            return data;
        }

        if (!TryGetInt(image, "bufferView", out var viewIndex)
            || !root.TryGetProperty("bufferViews", out var views) || views.ValueKind != JsonValueKind.Array
            || viewIndex < 0 || viewIndex >= views.GetArrayLength()) return null;
        var view = views[viewIndex];
        var bufferIndex = TryGetInt(view, "buffer", out var specifiedBuffer) ? specifiedBuffer : 0;
        var byteOffset = TryGetInt(view, "byteOffset", out var specifiedOffset) ? specifiedOffset : 0;
        if (!TryGetInt(view, "byteLength", out var byteLength) || bufferIndex < 0 || bufferIndex >= buffers.Length
            || byteOffset < 0 || byteLength <= 0 || byteLength > MaxAssetBytes
            || (long)byteOffset + byteLength > buffers[bufferIndex].Length)
            throw new InvalidDataException("An embedded glTF image buffer view is invalid.");
        return buffers[bufferIndex].AsSpan(byteOffset, byteLength).ToArray();
    }

    private static string ReadTexturePath(JsonElement root, JsonElement textureInfo, string modelPath, GameProject project)
    {
        if (!TryGetInt(textureInfo, "index", out var textureIndex)
            || !root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array
            || textureIndex < 0 || textureIndex >= textures.GetArrayLength()
            || !textures[textureIndex].TryGetProperty("source", out var sourceElement) || !sourceElement.TryGetInt32(out var imageIndex)
            || !root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array
            || imageIndex < 0 || imageIndex >= images.GetArrayLength()
            || !images[imageIndex].TryGetProperty("uri", out var uriElement)) return string.Empty;

        if (uriElement.ValueKind != JsonValueKind.String) return string.Empty;
        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(uri, UriKind.Absolute, out _)) return string.Empty;

        var decoded = Uri.UnescapeDataString(uri);
        var pathParts = decoded.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Any(part => part.Contains("(m)", StringComparison.Ordinal))) return string.Empty;
        var assetsRoot = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
        var imagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(modelPath)!, decoded.Replace('/', Path.DirectorySeparatorChar)));
        var relativePath = Path.GetRelativePath(assetsRoot, imagePath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return string.Empty;
        try
        {
            var safeImagePath = GameProjectStore.GetSafeAssetFilePathInAssets(project, relativePath);
            return File.Exists(safeImagePath) ? relativePath.Replace('\\', '/') : string.Empty;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static float ReadMaterialFactor(JsonElement pbr, string name, float fallback) =>
        pbr.TryGetProperty(name, out var element) && element.TryGetSingle(out var value) && float.IsFinite(value)
            ? Math.Clamp(value, 0, 1) : fallback;

    private static byte[][] ReadBuffers(JsonElement root, byte[]? binaryChunk, string assetDirectory, GameProject project)
    {
        var buffers = root.GetProperty("buffers");
        var result = new byte[buffers.GetArrayLength()][];
        for (var index = 0; index < result.Length; index++)
        {
            var buffer = buffers[index];
            var requiredLength = buffer.GetProperty("byteLength").GetInt32();
            if (requiredLength < 0 || requiredLength > MaxAssetBytes) throw new InvalidDataException("A glTF buffer length is invalid.");
            byte[] data;
            if (buffer.TryGetProperty("uri", out var uriElement))
            {
                if (uriElement.ValueKind != JsonValueKind.String) throw new InvalidDataException("A glTF buffer URI is invalid.");
                var uri = uriElement.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = uri.IndexOf(',');
                    if (comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only base64 data URIs are supported for glTF buffers.");
                    data = Convert.FromBase64String(uri[(comma + 1)..]);
                }
                else
                {
                    var decoded = Uri.UnescapeDataString(uri);
                    if (Uri.TryCreate(decoded, UriKind.Absolute, out _)) throw new InvalidDataException("Remote glTF buffers are not supported.");
                    var pathParts = decoded.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (pathParts.Any(part => part.Contains("(m)", StringComparison.Ordinal)))
                        throw new InvalidDataException("The glTF buffer path is unavailable.");
                    var assetsRoot = Path.GetFullPath(Path.Combine(project.ProjectPath, "assets"));
                    var externalPath = Path.GetFullPath(Path.Combine(assetDirectory, decoded.Replace('/', Path.DirectorySeparatorChar)));
                    var relativePath = Path.GetRelativePath(assetsRoot, externalPath);
                    if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                        throw new InvalidDataException("The glTF buffer is outside the project assets folder.");
                    externalPath = GameProjectStore.GetSafeAssetFilePathInAssets(project, relativePath);
                    if (!File.Exists(externalPath) || new FileInfo(externalPath).Length > MaxAssetBytes)
                        throw new InvalidDataException("The glTF buffer is missing or too large.");
                    data = File.ReadAllBytes(externalPath);
                }
            }
            else if (index == 0 && binaryChunk is not null) data = binaryChunk;
            else throw new InvalidDataException("A glTF buffer has no data.");
            if (data.Length < requiredLength) throw new InvalidDataException("A glTF buffer is shorter than its declared length.");
            result[index] = data;
        }
        return result;
    }

    private static Vector3[] ReadPositions(int accessorIndex, JsonElement accessors, JsonElement views, byte[][] buffers)
    {
        var accessor = GetAccessor(accessorIndex, accessors);
        if (accessor.GetProperty("componentType").GetInt32() != 5126 || accessor.GetProperty("type").GetString() != "VEC3")
            throw new InvalidDataException("glTF positions must be float VEC3 values.");
        var count = accessor.GetProperty("count").GetInt32();
        if (count < 0 || count > MaxVertices) throw new InvalidDataException("A glTF position count is invalid.");
        var (data, start, stride, end) = AccessorData(accessor, views, buffers, 12);
        var positions = new Vector3[count];
        for (var index = 0; index < count; index++)
        {
            var offset = checked(start + index * stride);
            if (offset > end - 12) throw new InvalidDataException("A glTF position exceeds its buffer view.");
            EnsureRange(data, offset, 12);
            var x = ReadFloat(data, offset); var y = ReadFloat(data, offset + 4); var z = ReadFloat(data, offset + 8);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw new InvalidDataException("A glTF position is not finite.");
            positions[index] = new Vector3(x, y, z);
        }
        return positions;
    }

    private static int[] ReadIndices(int accessorIndex, JsonElement accessors, JsonElement views, byte[][] buffers)
    {
        var accessor = GetAccessor(accessorIndex, accessors);
        if (accessor.GetProperty("type").GetString() != "SCALAR") throw new InvalidDataException("glTF indices must be scalar values.");
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var componentBytes = componentType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("A glTF index type is unsupported.") };
        var count = accessor.GetProperty("count").GetInt32();
        if (count < 0 || count > MaxVertices * 3) throw new InvalidDataException("A glTF index count is invalid.");
        var (data, start, stride, end) = AccessorData(accessor, views, buffers, componentBytes);
        var indices = new int[count];
        for (var index = 0; index < count; index++)
        {
            var offset = checked(start + index * stride);
            if (offset > end - componentBytes) throw new InvalidDataException("A glTF index exceeds its buffer view.");
            EnsureRange(data, offset, componentBytes);
            var value = componentType switch
            {
                5121 => data[offset],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)),
                _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)))
            };
            indices[index] = value;
        }
        return indices;
    }

    private static (byte[] Data, int Start, int Stride, int End) AccessorData(JsonElement accessor, JsonElement views, byte[][] buffers, int elementSize)
    {
        if (!accessor.TryGetProperty("bufferView", out var viewElement) || !viewElement.TryGetInt32(out var viewIndex))
            throw new InvalidDataException("Sparse or implicit glTF accessors are not supported.");
        if (viewIndex < 0 || viewIndex >= views.GetArrayLength()) throw new InvalidDataException("A glTF buffer view index is invalid.");
        var view = views[viewIndex];
        var bufferIndex = view.GetProperty("buffer").GetInt32();
        if (bufferIndex < 0 || bufferIndex >= buffers.Length) throw new InvalidDataException("A glTF buffer index is invalid.");
        var data = buffers[bufferIndex];
        var viewOffset = view.TryGetProperty("byteOffset", out var viewOffsetElement) ? viewOffsetElement.GetInt32() : 0;
        var viewLength = view.GetProperty("byteLength").GetInt32();
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorOffsetElement) ? accessorOffsetElement.GetInt32() : 0;
        var stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : elementSize;
        if (viewOffset < 0 || viewLength < 0 || accessorOffset < 0 || stride < elementSize) throw new InvalidDataException("A glTF accessor offset or stride is invalid.");
        var start = checked(viewOffset + accessorOffset);
        var end = checked(viewOffset + viewLength);
        EnsureRange(data, viewOffset, viewLength);
        if (start > end) throw new InvalidDataException("A glTF accessor starts outside its buffer view.");
        return (data, start, stride, end);
    }

    private static JsonElement GetAccessor(int index, JsonElement accessors)
    {
        if (index < 0 || index >= accessors.GetArrayLength()) throw new InvalidDataException("A glTF accessor index is invalid.");
        return accessors[index];
    }

    private static Matrix4x4 ReadNodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrixElement))
        {
            var values = matrixElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (values.Length != 16 || values.Any(value => !float.IsFinite(value))) throw new InvalidDataException("A glTF node matrix is invalid.");
            // glTF stores column-major matrices; System.Numerics transforms row vectors.
            return new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
        }
        var translation = ReadVector(node, "translation", Vector3.Zero);
        var scale = ReadVector(node, "scale", Vector3.One);
        var rotation = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out var rotationElement))
        {
            var values = rotationElement.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (values.Length != 4 || values.Any(value => !float.IsFinite(value))) throw new InvalidDataException("A glTF node rotation is invalid.");
            rotation = new Quaternion(values[0], values[1], values[2], values[3]);
            if (!float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-12f) throw new InvalidDataException("A glTF node rotation is invalid.");
            rotation = Quaternion.Normalize(rotation);
        }
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static int[] Triangulate(int[] indices, int mode)
    {
        if (indices.Length < 3) return [];
        var result = new int[(indices.Length - 2) * 3];
        var output = 0;
        for (var index = 2; index < indices.Length; index++)
        {
            if (mode == 5 && index % 2 == 0)
            {
                result[output++] = indices[index - 2]; result[output++] = indices[index - 1];
            }
            else if (mode == 5)
            {
                result[output++] = indices[index - 1]; result[output++] = indices[index - 2];
            }
            else
            {
                result[output++] = indices[0]; result[output++] = indices[index - 1];
            }
            result[output++] = indices[index];
        }
        return result;
    }

    private static Vector3 ReadVector(JsonElement node, string name, Vector3 fallback)
    {
        if (!node.TryGetProperty(name, out var element)) return fallback;
        var values = element.EnumerateArray().Select(value => value.GetSingle()).ToArray();
        if (values.Length != 3 || values.Any(value => !float.IsFinite(value))) throw new InvalidDataException($"A glTF node {name} is invalid.");
        return new Vector3(values[0], values[1], values[2]);
    }

    private static SharpVector3 Transform(Vector3[] positions, int index, Matrix4x4 transform)
    {
        if (index < 0 || index >= positions.Length) throw new InvalidDataException("A glTF triangle references a missing vertex.");
        var point = Vector3.Transform(positions[index], transform);
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) throw new InvalidDataException("A transformed glTF vertex is not finite.");
        return new SharpVector3(point.X, point.Y, point.Z);
    }

    private static bool TryGetInt(JsonElement element, string property, out int value) =>
        element.TryGetProperty(property, out var valueElement) && valueElement.TryGetInt32(out value) ? true : SetZero(out value);

    private static bool SetZero(out int value) { value = 0; return false; }

    private static float ReadFloat(byte[] data, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));

    private static void EnsureRange(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count) throw new InvalidDataException("A glTF accessor exceeds its buffer bounds.");
    }
}
