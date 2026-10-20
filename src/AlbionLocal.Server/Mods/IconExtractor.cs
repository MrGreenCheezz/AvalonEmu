using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AlbionLocal.Server.Mods;

/// <summary>
/// A read-only reader of a Unity SerializedFile (format 22+, little-endian, without type trees: Albion's resources.assets, Unity
/// 6000.3): the object table, and the raw bytes of one object. The file is opened for reading with sharing, never written.
/// </summary>
public sealed class UnitySerializedFile : IDisposable
{
    public readonly record struct Obj(long PathId, long Offset, uint Size, int ClassId);

    private readonly FileStream _file;

    public string Path { get; }
    public string UnityVersion { get; }
    public int FormatVersion { get; }
    public Dictionary<long, Obj> Objects { get; } = [];

    public UnitySerializedFile(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var head = new byte[48];
        _file.ReadExactly(head);
        FormatVersion = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8));
        if (FormatVersion < 22) throw new InvalidDataException($"{path}: SerializedFile format {FormatVersion}, only 22+ is supported");
        if (head[16] != 0) throw new InvalidDataException($"{path}: big-endian SerializedFile is not supported");
        var metaSize = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20));
        var dataOffset = BinaryPrimitives.ReadInt64BigEndian(head.AsSpan(32));
        if (metaSize > 512u << 20) throw new InvalidDataException($"{path}: metadata of {metaSize} bytes");
        var meta = new byte[metaSize];
        _file.ReadExactly(meta);
        var r = new UnityReader(meta);
        UnityVersion = r.CString();
        r.I32(); // platform
        if (r.U8() != 0) throw new InvalidDataException($"{path}: has type trees (not needed by this reader; not supported)");
        var types = new int[r.I32()];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = r.I32();
            r.U8();
            r.I16();
            if (types[i] == 114) r.Skip(16);
            r.Skip(16);
        }
        var count = r.I32();
        for (var i = 0; i < count; i++)
        {
            r.Align();
            var id = r.I64();
            var start = r.I64();
            var size = r.U32();
            var type = r.I32();
            Objects[id] = new Obj(id, dataOffset + start, size, types[type]);
        }
    }

    public byte[] Read(long pathId)
    {
        var o = Objects.TryGetValue(pathId, out var found) ? found : throw new InvalidDataException($"no object {pathId} in {Path}");
        if (o.Size > 256u << 20) throw new InvalidDataException($"object {pathId}: {o.Size} bytes");
        var b = new byte[o.Size];
        _file.Position = o.Offset;
        _file.ReadExactly(b);
        return b;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Little-endian reads over an object's bytes, with Unity's 4-byte alignment after strings and bools.</summary>
public sealed class UnityReader(byte[] b, int p = 0)
{
    public int Pos { get; set; } = p;
    public int Length => b.Length;
    private void Need(int n) { if (Pos + n > b.Length || n < 0) throw new InvalidDataException("read past the end of the object"); }
    public byte U8() { Need(1); return b[Pos++]; }
    public short I16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(Pos)); Pos += 2; return v; }
    public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(Pos)); Pos += 4; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(Pos)); Pos += 4; return v; }
    public long I64() { Need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(Pos)); Pos += 8; return v; }
    public float F32() => BitConverter.Int32BitsToSingle(I32());
    public void Skip(int n) { Need(n); Pos += n; }
    public void Align() => Pos = (Pos + 3) & ~3;
    public bool Bool() { var v = U8() != 0; Align(); return v; }
    public (int File, long Id) PPtr() => (I32(), I64());

    public string Str(int max = 4096)
    {
        var n = I32();
        if (n < 0 || n > max) throw new InvalidDataException($"string of {n} bytes");
        Need(n);
        var s = Encoding.UTF8.GetString(b, Pos, n);
        Pos += n;
        Align();
        return s;
    }

    public string CString()
    {
        var end = Array.IndexOf(b, (byte)0, Pos);
        if (end < 0) throw new InvalidDataException("unterminated string");
        var s = Encoding.ASCII.GetString(b, Pos, end - Pos);
        Pos = end + 1;
        return s;
    }
}

/// <summary>A sprite of an NGUI atlas (UISpriteData): its rectangle from the atlas's top-left, and the trimmed padding.</summary>
public sealed record AtlasSprite(string Name, int X, int Y, int W, int H, int PadL, int PadR, int PadT, int PadB);

/// <summary>
/// The one-time icon extraction (modkit <c>icons</c>, icons SPEC): the NGUI atlases (UIAtlas MonoBehaviours on the GameObjects
/// ItemAtlas, ItemAtlas_01, ItemAtlas_02, SpellAtlas) of the client's resources.assets → material → Texture2D (mip 0 out of
/// resources.assets.resS) → one PNG per sprite in &lt;out&gt;/&lt;atlas&gt;/, plus &lt;out&gt;/index.json (sprite → file). Only reads the client files.
/// </summary>
public static class IconExtractor
{
    public static readonly string[] DefaultAtlases = ["ItemAtlas", "ItemAtlas_01", "ItemAtlas_02", "SpellAtlas"];

    public sealed record AtlasInfo(string Name, string Texture, string Format, int Width, int Height, int Sprites, int Written);

    public sealed record Result(List<AtlasInfo> Atlases, int Icons, long Bytes, TimeSpan Time, List<string> Notes);

    /// <summary>Sprite names as file names: letters, digits, _ - @ . kept, the rest as _ (index.json keeps the real names).</summary>
    public static string FileName(string sprite)
    {
        var sb = new StringBuilder(sprite.Length);
        foreach (var ch in sprite) sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '@' or '.' ? ch : '_');
        var s = sb.ToString().TrimStart('.');
        return s.Length == 0 ? "_" : s.Length > 100 ? s[..100] : s;
    }

    /// <summary>Parses a UIAtlas MonoBehaviour: (material, sprites, replacement) or null when the bytes are not one.</summary>
    public static ((int File, long Id) Material, List<AtlasSprite> Sprites, (int File, long Id) Replacement)? ParseAtlas(byte[] mb)
    {
        try
        {
            var r = new UnityReader(mb);
            r.PPtr(); // m_GameObject
            r.Bool(); // m_Enabled
            r.PPtr(); // m_Script
            r.Str(); // m_Name
            r.I32(); // mWidth
            r.I32(); // mHeight
            var material = r.PPtr();
            var n = r.I32();
            if (n < 0 || n > 100_000) return null;
            var sprites = new List<AtlasSprite>(n);
            for (var i = 0; i < n; i++)
            {
                var name = r.Str(512);
                int x = r.I32(), y = r.I32(), w = r.I32(), h = r.I32();
                r.Skip(16); // borders
                int pl = r.I32(), pr = r.I32(), pt = r.I32(), pb = r.I32();
                r.Bool(); // bCuttedSprite
                r.Skip(16); // iCuttedParent x/y/w/h
                r.Bool(); // PreserveOrientation
                sprites.Add(new AtlasSprite(name, x, y, w, h, pl, pr, pt, pb));
            }
            r.F32(); // mPixelSize
            var replacement = r.PPtr();
            return (material, sprites, replacement);
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The _MainTex of a Material: the PPtr after the length-prefixed name in m_SavedProperties.m_TexEnvs.</summary>
    public static (int File, long Id)? MainTex(byte[] material)
    {
        ReadOnlySpan<byte> key = [8, 0, 0, 0, (byte)'_', (byte)'M', (byte)'a', (byte)'i', (byte)'n', (byte)'T', (byte)'e', (byte)'x'];
        var at = material.AsSpan().IndexOf(key);
        if (at < 0 || at + 24 > material.Length) return null;
        var r = new UnityReader(material, at + 12);
        return r.PPtr();
    }

    public sealed record TextureInfo(string Name, int Width, int Height, int Format, long CompleteSize, string StreamPath, long StreamOffset, long StreamSize, int InlineAt);

    /// <summary>
    /// A Texture2D (Unity 6000.3 layout): name, size and format from the front; the image data from the end (m_StreamData = offset,
    /// size, path into the .resS; or, when the path is empty, the inline image bytes before it).
    /// </summary>
    public static TextureInfo ParseTexture(byte[] t)
    {
        var r = new UnityReader(t);
        var name = r.Str();
        r.I32(); // m_ForcedFallbackFormat
        int w = r.I32(), h = r.I32();
        long complete = r.U32();
        r.I32(); // m_MipsStripped
        var format = r.I32();
        if (w is <= 0 or > 16384 || h is <= 0 or > 16384) throw new InvalidDataException($"texture {name}: size {w}×{h} not plausible (layout?)");
        // The tail: ... image data (int count + bytes, aligned) | u64 offset | u32 size | string path (aligned) = end.
        for (var len = 0; len <= 512; len++)
        {
            var p = t.Length - ((4 + len + 3) & ~3);
            if (p < 16) break;
            if (BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(p)) != len) continue;
            var path = Encoding.UTF8.GetString(t, p + 4, len);
            if (path.Any(char.IsControl)) continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(p - 4));
            var offset = BinaryPrimitives.ReadInt64LittleEndian(t.AsSpan(p - 12));
            if (len > 0) return new TextureInfo(name, w, h, format, complete, path, offset, size, -1);
            var q = p - 12 - (int)((complete + 3) & ~3L) - 4;
            if (q >= 0 && BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(q)) == complete)
                return new TextureInfo(name, w, h, format, complete, "", 0, complete, q + 4);
        }
        throw new InvalidDataException($"texture {name}: image data not found");
    }

    /// <summary>Mip 0 bytes of a texture (streamed from the .resS next to the assets file, or inline).</summary>
    public static byte[] ReadMip0(UnitySerializedFile file, byte[] texture, TextureInfo info)
    {
        var need = TextureDecode.Mip0Size(info.Format, info.Width, info.Height);
        if (need > info.StreamSize || need > int.MaxValue) throw new InvalidDataException($"texture {info.Name}: {info.StreamSize} bytes, mip 0 needs {need}");
        var data = new byte[need];
        if (info.InlineAt >= 0)
        {
            Array.Copy(texture, info.InlineAt, data, 0, need);
            return data;
        }
        if (info.StreamPath.Contains(':') || info.StreamPath.Contains('/') || info.StreamPath.Contains('\\'))
            throw new InvalidDataException($"texture {info.Name}: stream {info.StreamPath} (only a .resS beside the assets file is supported)");
        var res = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.Path)!, info.StreamPath);
        using var f = new FileStream(res, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (info.StreamOffset < 0 || info.StreamOffset + need > f.Length) throw new InvalidDataException($"texture {info.Name}: {res} too short");
        f.Position = info.StreamOffset;
        f.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Extracts <paramref name="atlases"/> of <paramref name="assetsFile"/> into <paramref name="outDir"/>: old PNGs of those atlases are
    /// replaced, index.json is written last (the web panel's switch).
    /// </summary>
    public static Result Extract(string assetsFile, string outDir, IReadOnlyCollection<string>? atlases, TextWriter log)
    {
        var watch = Stopwatch.StartNew();
        var wanted = new HashSet<string>(atlases is { Count: > 0 } ? atlases : DefaultAtlases, StringComparer.Ordinal);
        var notes = new List<string>();
        using var file = new UnitySerializedFile(assetsFile);
        log.WriteLine($"{assetsFile}: Unity {file.UnityVersion}, format {file.FormatVersion}, {file.Objects.Count} objects");
        // The GameObjects named like a wanted atlas, and their MonoBehaviours.
        var found = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var o in file.Objects.Values.Where(o => o.ClassId == 1 && o.Size <= 4096).OrderBy(o => o.Offset))
        {
            var b = file.Read(o.PathId);
            try
            {
                var r = new UnityReader(b);
                var n = r.I32();
                if (n is < 0 or > 64) continue;
                var comps = new List<long>();
                for (var i = 0; i < n; i++) comps.Add(r.PPtr().Id);
                r.U32(); // m_Layer
                var name = r.Str(256);
                if (wanted.Contains(name)) found.GetOrAdd(name).AddRange(comps.Where(c => file.Objects.TryGetValue(c, out var co) && co.ClassId == 114));
            }
            catch (InvalidDataException) { }
        }
        var index = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var infos = new List<AtlasInfo>();
        long bytes = 0;
        var outFull = System.IO.Path.GetFullPath(outDir);
        Directory.CreateDirectory(outFull);
        foreach (var atlasName in wanted.OrderBy(a => Array.IndexOf(DefaultAtlases, a) is var i and >= 0 ? i : 99).ThenBy(a => a, StringComparer.Ordinal))
        {
            var parsed = found.GetValueOrDefault(atlasName)?.Select(c => ParseAtlas(file.Read(c))).FirstOrDefault(p => p is { Sprites.Count: > 0 });
            if (parsed is not { } atlas)
            {
                notes.Add($"{atlasName}: no NGUI atlas with sprites found (a Ref* atlas only redirects; name the real one)");
                continue;
            }
            if (atlas.Material.File != 0 || MainTex(file.Read(atlas.Material.Id)) is not { File: 0 } tex)
            {
                notes.Add($"{atlasName}: material/texture in another file, not supported");
                continue;
            }
            var texBytes = file.Read(tex.Id);
            var info = ParseTexture(texBytes);
            if (!TextureDecode.Supported(info.Format))
            {
                notes.Add($"{atlasName}: texture {info.Name} is {TextureDecode.FormatName(info.Format)} (no decoder)");
                continue;
            }
            var rgba = TextureDecode.ToRgba(info.Format, info.Width, info.Height, ReadMip0(file, texBytes, info));
            var dir = System.IO.Path.Combine(outFull, FileName(atlasName));
            if (Directory.Exists(dir)) foreach (var old in Directory.EnumerateFiles(dir, "*.png")) File.Delete(old);
            Directory.CreateDirectory(dir);
            var written = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in atlas.Sprites)
            {
                if (s.W <= 0 || s.H <= 0 || s.W > 4096 || s.H > 4096 || index.ContainsKey(s.Name)) continue;
                var fname = FileName(s.Name);
                for (var k = 2; !used.Add(fname); k++) fname = FileName(s.Name) + "~" + k;
                var px = TextureDecode.Crop(rgba, info.Width, info.Height, s.X, s.Y, s.W, s.H, Math.Max(0, s.PadL), Math.Max(0, s.PadT), Math.Max(0, s.PadR), Math.Max(0, s.PadB));
                var png = Png.Encode(s.W + Math.Max(0, s.PadL) + Math.Max(0, s.PadR), s.H + Math.Max(0, s.PadT) + Math.Max(0, s.PadB), px);
                File.WriteAllBytes(System.IO.Path.Combine(dir, fname + ".png"), png);
                index[s.Name] = FileName(atlasName) + "/" + fname + ".png";
                bytes += png.Length;
                written++;
            }
            infos.Add(new AtlasInfo(atlasName, info.Name, TextureDecode.FormatName(info.Format), info.Width, info.Height, atlas.Sprites.Count, written));
            log.WriteLine($"  {atlasName}: texture {info.Name} {info.Width}×{info.Height} {TextureDecode.FormatName(info.Format)}, {atlas.Sprites.Count} sprites, {written} PNG written");
        }
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            source = System.IO.Path.GetFileName(assetsFile),
            unity = file.UnityVersion,
            created = DateTime.UtcNow.ToString("u"),
            atlases = infos,
            sprites = index,
        }, new JsonSerializerOptions { WriteIndented = true });
        var tmp = System.IO.Path.Combine(outFull, "index.json.tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, System.IO.Path.Combine(outFull, "index.json"), overwrite: true);
        return new Result(infos, index.Count, bytes, watch.Elapsed, notes);
    }

    private static List<long> GetOrAdd(this Dictionary<string, List<long>> d, string key) =>
        d.TryGetValue(key, out var l) ? l : d[key] = [];
}
