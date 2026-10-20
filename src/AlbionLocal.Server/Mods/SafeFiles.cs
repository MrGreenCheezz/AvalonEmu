using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AlbionLocal.Server.Mods;

/// <summary>
/// The file operations of the modkit, written so that the official client can never be changed through them (modkit SPEC §3).
/// Every file of game-local's GameData is an NTFS hard link to the official install (Reverse/game): writing into it in place
/// would write the official file too. So no game-local file is ever opened for writing: a new content goes into a temp file
/// in the same folder, the game-local name is deleted (which removes that one link, the official name keeps the data) and the
/// temp file is moved to the name. Writes under the official folder are refused outright.
/// </summary>
public static class SafeFiles
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIMEs are two DWORDs: no 8-byte alignment
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    private static ByHandleFileInformation Info(string path)
    {
        // Read-only open with full sharing: the information call needs a handle, nothing is written.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var info) ? info : throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path}: no file information");
    }

    /// <summary>How many names the file has (1 = its own data; 2+ = a hard link, e.g. game-local's to the official install).</summary>
    public static int LinkCount(string path) => OperatingSystem.IsWindows() ? (int)Info(path).NumberOfLinks : 1;

    /// <summary>Whether the two paths are names of the same file (same volume and file index).</summary>
    public static bool SameFile(string a, string b)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(a) || !File.Exists(b)) return false;
        var x = Info(a);
        var y = Info(b);
        return x.VolumeSerialNumber == y.VolumeSerialNumber && x.FileIndexHigh == y.FileIndexHigh && x.FileIndexLow == y.FileIndexLow;
    }

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Throws when <paramref name="path"/> lies in <paramref name="protectedDir"/> (the official install's folder), also when reached
    /// through a junction or symbolic link (both sides are resolved first).
    /// </summary>
    public static void GuardNotOfficial(string path, string protectedDir)
    {
        var full = Path.GetFullPath(path);
        var real = Path.Combine(RealPath(Path.GetDirectoryName(full)!), Path.GetFileName(full));
        var root = RealPath(protectedDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (real.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (real + Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refused: {full} is in the official folder {protectedDir}; the modkit never writes there");
    }

    /// <summary>
    /// Puts <paramref name="content"/> at <paramref name="target"/> without ever writing into the file that is there: a temp file
    /// next to it, then delete the name and move the temp file in (the old data, e.g. the official file the name was linked to,
    /// is not touched). Afterwards the target must be a file of its own (one link) with exactly <paramref name="content"/>, and
    /// <paramref name="official"/> (when given) must still have the hash it had before; anything else throws.
    /// </summary>
    public static void ReplaceUnlinked(string target, byte[] content, string protectedDir, string? official = null)
    {
        GuardNotOfficial(target, protectedDir);
        var officialBefore = official is not null && File.Exists(official) ? Sha256(official) : null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var temp = Path.Combine(dir, Path.GetFileName(target) + ".modkit-tmp");
        if (File.Exists(temp)) File.Delete(temp); // a leftover of an aborted run: deleting a name never changes another link's data
        // FileMode.CreateNew: a brand-new file, never an existing one (and so never a link to the official data).
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(content);
        if (LinkCount(temp) != 1) throw new InvalidOperationException($"{temp}: a new file with {LinkCount(temp)} links; aborted");
        if (File.Exists(target)) File.Delete(target); // only removes this name; the official name keeps its data
        File.Move(temp, target);
        if (LinkCount(target) != 1) throw new InvalidOperationException($"{target}: {LinkCount(target)} links after the replace");
        if (Sha256(target) != Sha256(content)) throw new InvalidOperationException($"{target}: the written content differs");
        if (officialBefore is not null && Sha256(official!) != officialBefore)
            throw new InvalidOperationException($"{official} changed while {target} was replaced: STOP and check the official install");
    }

    /// <summary>
    /// Makes <paramref name="target"/> a hard link to <paramref name="official"/> again (as game-local was set up), or a copy when no link
    /// can be made (another volume). The target name is deleted first; the official file is only read.
    /// </summary>
    public static bool RelinkOrCopy(string target, string official, string protectedDir)
    {
        GuardNotOfficial(target, protectedDir);
        if (File.Exists(target)) File.Delete(target);
        if (OperatingSystem.IsWindows() && CreateHardLink(Path.GetFullPath(target), Path.GetFullPath(official), IntPtr.Zero)) return true;
        File.Copy(official, target);
        return false;
    }

    /// <summary>A hard link <paramref name="link"/> to <paramref name="existing"/> (the self-test's stand-in for game-local).</summary>
    public static void HardLink(string link, string existing)
    {
        if (!CreateHardLink(Path.GetFullPath(link), Path.GetFullPath(existing), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"cannot link {link} to {existing}");
    }

    /// <summary>The path with every symbolic link or junction on the way resolved (e.g. a game-local junction → the real client folder).</summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = target.FullName;
            }
            catch (IOException) { }
        }
        return current;
    }
}
