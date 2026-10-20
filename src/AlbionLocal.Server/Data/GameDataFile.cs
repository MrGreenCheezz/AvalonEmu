using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// One encrypted XML file of the client's GameData folder (items.bin, spells.bin, …), read the way
/// analysis/tools/decrypt_bin.ps1 does: DES-CBC (PKCS7) with the client's key and IV, then a gzip stream whose header
/// flag byte is garbage, so the 10-byte gzip header is skipped and the raw deflate data is inflated straight into an
/// <see cref="XmlReader"/> (ARCHITECTURE.md §5.1). The .bin bytes are read once; <see cref="Sha256"/> fingerprints them.
/// </summary>
public sealed class GameDataFile
{
    private static readonly byte[] Key = [48, 239, 114, 71, 66, 242, 4, 50];
    private static readonly byte[] IV = [14, 166, 220, 55, 197, 107, 96, 180];

    private static readonly XmlReaderSettings Settings = new()
    {
        IgnoreComments = true,
        IgnoreWhitespace = true,          // whitespace-only nodes; text such as <baselevels> lines stays
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = true,
    };

    private readonly byte[] _bin;

    private GameDataFile(string path, byte[] bin)
    {
        Path = path;
        _bin = bin;
        Sha256 = Convert.ToHexString(SHA256.HashData(bin)).ToLowerInvariant();
    }

    /// <summary>File name in the GameData folder, e.g. "items.bin".</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    public string Path { get; }

    /// <summary>Size of the .bin as shipped.</summary>
    public long Length => _bin.Length;

    /// <summary>SHA-256 of the .bin as shipped (lower-case hex): logged at startup and kept in clientdata.json.</summary>
    public string Sha256 { get; }

    /// <summary>Reads <paramref name="name"/> from <paramref name="folder"/>; a missing or unreadable file throws <see cref="InvalidDataException"/>.</summary>
    public static GameDataFile Open(string folder, string name)
    {
        var path = System.IO.Path.Combine(folder, name);
        if (!File.Exists(path)) throw new InvalidDataException($"{path} missing (Paths:ClientGameData must be the client's GameData folder)");
        try
        {
            return new GameDataFile(path, File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{path}: cannot read ({e.Message})", e);
        }
    }

    /// <summary>The decrypted XML text as a stream (decrypt, skip the 10-byte gzip header, inflate).</summary>
    public Stream OpenXml()
    {
        byte[] plain;
        try
        {
            using var des = DES.Create();
            des.Key = Key;
            plain = des.DecryptCbc(_bin, IV, PaddingMode.PKCS7);
        }
        catch (CryptographicException e)
        {
            throw new InvalidDataException($"{Path}: cannot decrypt ({e.Message})", e);
        }
        if (plain.Length < 10) throw new InvalidDataException($"{Path}: {plain.Length} bytes after decryption, no gzip header");
        return new DeflateStream(new MemoryStream(plain, 10, plain.Length - 10), CompressionMode.Decompress);
    }

    public XmlReader CreateReader() => XmlReader.Create(OpenXml(), Settings);

    /// <summary>
    /// The whole decrypted XML as bytes, with the 10-byte gzip header of the .bin (<paramref name="gzipHeader"/>): what
    /// <see cref="Encode"/> needs to write a file the client reads the same way (modkit, tools/AlbionLocal.ModKit).
    /// </summary>
    public byte[] DecodeXml(out byte[] gzipHeader)
    {
        byte[] plain;
        try
        {
            using var des = DES.Create();
            des.Key = Key;
            plain = des.DecryptCbc(_bin, IV, PaddingMode.PKCS7);
        }
        catch (CryptographicException e)
        {
            throw new InvalidDataException($"{Path}: cannot decrypt ({e.Message})", e);
        }
        if (plain.Length < 10) throw new InvalidDataException($"{Path}: {plain.Length} bytes after decryption, no gzip header");
        gzipHeader = plain[..10];
        using var inflate = new DeflateStream(new MemoryStream(plain, 10, plain.Length - 10), CompressionMode.Decompress);
        using var xml = new MemoryStream();
        inflate.CopyTo(xml);
        return xml.ToArray();
    }

    /// <summary>
    /// The inverse of <see cref="DecodeXml"/>: <paramref name="xml"/> gzip-compressed (with a correct CRC-32 and size trailer),
    /// its 10-byte header replaced by <paramref name="gzipHeader"/> (the original file's, so that the client's reader sees the
    /// header it always saw), then DES-CBC (PKCS7) encrypted with the client's key and IV.
    /// </summary>
    public static byte[] Encode(byte[] xml, ReadOnlySpan<byte> gzipHeader)
    {
        if (gzipHeader.Length != 10) throw new ArgumentException("the gzip header is 10 bytes", nameof(gzipHeader));
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(xml);
        var plain = packed.ToArray();
        gzipHeader.CopyTo(plain);
        using var des = DES.Create();
        des.Key = Key;
        return des.EncryptCbc(plain, IV, PaddingMode.PKCS7);
    }

    /// <summary>A .bin held in memory (modkit: a built file before it is installed), named like the file it stands for.</summary>
    public static GameDataFile FromBytes(string path, byte[] bin) => new(path, bin);

    /// <summary>
    /// The top-level elements (the children of the root) in file order, each read as its own <see cref="XElement"/>
    /// while the file streams: only one element is held at a time. Errors in the data throw <see cref="InvalidDataException"/>.
    /// </summary>
    public IEnumerable<XElement> Elements()
    {
        // Created through Named too: XmlReader.Create already inflates the first bytes to detect the encoding.
        using var reader = Named(CreateReader);
        Named(reader.MoveToContent);
        if (reader.IsEmptyElement) yield break;
        Named(reader.Read);
        while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType == XmlNodeType.Element)
                yield return Named(() => (XElement)XNode.ReadFrom(reader)); // leaves the reader on the next node
            else Named(reader.Read);
        }
    }

    /// <summary>
    /// The children of the top-level elements named in <paramref name="containers"/>, each read as its own
    /// <see cref="XElement"/> while the file streams, with the name of its container (world.xml keeps every cluster in
    /// one &lt;clusters&gt; element and every critical-object list in &lt;criticalObjects&gt;, both as &lt;cluster id&gt;).
    /// Other top-level elements are skipped unread. Errors in the data throw <see cref="InvalidDataException"/>.
    /// </summary>
    public IEnumerable<(string Container, XElement Element)> ElementsIn(params string[] containers)
    {
        using var reader = Named(CreateReader);
        Named(reader.MoveToContent);
        if (reader.IsEmptyElement) yield break;
        Named(reader.Read);
        while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                Named(reader.Read);
                continue;
            }
            var container = reader.LocalName;
            if (!containers.Contains(container) || reader.IsEmptyElement)
            {
                Named(() => { reader.Skip(); return true; });
                continue;
            }
            Named(reader.Read);
            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element)
                    yield return (container, Named(() => (XElement)XNode.ReadFrom(reader)));
                else Named(reader.Read);
            }
            Named(reader.Read); // the container's end tag
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> on a reader of the whole file (for the big files read without building elements:
    /// cluster/tiles.bin); XML and inflate errors throw <see cref="InvalidDataException"/> naming the file.
    /// </summary>
    public T Read<T>(Func<XmlReader, T> read) => Named(() =>
    {
        using var reader = CreateReader();
        return read(reader);
    });

    /// <summary>The whole document's root (for the small files: gamedata, characters).</summary>
    public XElement Root() => Named(() =>
    {
        using var reader = CreateReader();
        return XElement.Load(reader);
    });

    /// <summary>One read step, with XML and inflate errors turned into <see cref="InvalidDataException"/> naming the file.</summary>
    private T Named<T>(Func<T> step)
    {
        try { return step(); }
        catch (Exception e) when (e is XmlException or IOException || e is InvalidDataException && !e.Message.StartsWith(Path))
        {
            throw new InvalidDataException($"{Path}: {e.Message}", e);
        }
    }
}
