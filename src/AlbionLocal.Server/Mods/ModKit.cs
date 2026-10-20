using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Mods;

/// <summary>Where the modkit reads and writes: game-local's GameData (modded), the official GameData (read only) and its install root (never written).</summary>
public sealed record ModPaths(string Local, string Official, string OfficialRoot)
{
    /// <summary>
    /// The official GameData that belongs to <paramref name="localGameData"/>: game-local and game sit side by side
    /// (Reverse/game-local, Reverse/game), the same layout below each; junctions on the way are followed. Null when not found.
    /// </summary>
    public static ModPaths? For(string localGameData)
    {
        var local = Path.GetFullPath(localGameData);
        var real = SafeFiles.RealPath(local);
        var localRoot = Directory.GetParent(real)?.Parent?.Parent; // GameData → StreamingAssets → Albion-Online_Data → game-local
        if (localRoot?.Parent is not { } reverse) return null;
        var officialRoot = Path.Combine(reverse.FullName, "game");
        var official = Path.Combine(officialRoot, Path.GetRelativePath(localRoot.FullName, real));
        return Directory.Exists(official) && !official.Equals(real, StringComparison.OrdinalIgnoreCase) ? new ModPaths(local, official, officialRoot) : null;
    }

    /// <summary>
    /// The self-test's data folder: <paramref name="localGameData"/>, or the official GameData when game-local is modded (a modkit
    /// state with installed files, or one of <paramref name="files"/> that differs), so that the golden checks keep comparing
    /// the official data. Null when game-local is modded and no official folder is found.
    /// </summary>
    public static string? PristineFor(string localGameData, IEnumerable<string> files)
    {
        var paths = For(localGameData);
        var modded = ModState.Load(localGameData) is { Installed.Count: > 0 };
        if (!modded && paths is not null)
            modded = files.Any(f => File.Exists(Path.Combine(localGameData, f)) && File.Exists(Path.Combine(paths.Official, f))
                && !SafeFiles.SameFile(Path.Combine(localGameData, f), Path.Combine(paths.Official, f))
                && SafeFiles.Sha256(Path.Combine(localGameData, f)) != SafeFiles.Sha256(Path.Combine(paths.Official, f)));
        return !modded ? localGameData : paths?.Official;
    }
}

/// <summary>
/// The modkit's verbs over <see cref="ModPaths"/> (tools/AlbionLocal.ModKit): build (and check), restore, status, extract.
/// Every write goes through <see cref="SafeFiles"/>: game-local files are replaced, never written in place, and nothing under
/// the official install is ever opened for writing.
/// </summary>
public sealed class ModKit(ModPaths paths, TextWriter output)
{
    public ModPaths Paths { get; } = paths;

    private void Line(string text) => output.WriteLine(text);

    /// <summary>check: builds in memory and reports; nothing is written. True when the packs are fine.</summary>
    public bool Check(IReadOnlyList<ModPack> packs)
    {
        var result = new ModBuilder(Paths.Official).Build(packs);
        Report(result);
        return result.Ok;
    }

    private void Report(ModBuildResult result)
    {
        foreach (var n in result.Notes) Line("  " + n);
        foreach (var w in result.Warnings) Line("  warning: " + w);
        foreach (var e in result.Errors) Line("  ERROR: " + e);
        Line(result.Ok ? $"ok: {result.Files.Count} file(s) built" : $"{result.Errors.Count} error(s): nothing built");
    }

    /// <summary>
    /// build: the packs over the pristine official files, installed into game-local. Refused (nothing written) on any error, and
    /// when the official client changed since the hashes were recorded (an update: the packs may not fit any more) unless
    /// <paramref name="force"/>. Files a previous build modded and this one does not are linked back to the official ones.
    /// </summary>
    public ModBuildResult? Build(IReadOnlyList<ModPack> packs, bool force)
    {
        var state = ModState.Load(Paths.Local) ?? new ModState();
        var build = ModBuilder.ReadBuild(Paths.Official);
        if (state.ClientBuild is { } recorded && build != recorded && !force)
        {
            Line($"the official client changed since the modkit's first build ({recorded} -> {build ?? "unknown"}): check the packs against the new data, then build with --force");
            return null;
        }
        var result = new ModBuilder(Paths.Official).Build(packs);
        Report(result);
        if (!result.Ok) return result;
        var changed = result.PristineHashes.Where(p => state.Pristine.GetValueOrDefault(p.Key) is { } old && old != p.Value).Select(p => p.Key).ToList();
        if (changed.Count > 0 && !force)
        {
            Line($"official file(s) changed since they were recorded: {string.Join(", ", changed)} (an update?): check the packs, then build with --force");
            return null;
        }
        foreach (var (file, hash) in result.PristineHashes) state.Pristine[file] = hash;
        state.ClientBuild = build;
        foreach (var (file, bin) in result.Files)
        {
            SafeFiles.ReplaceUnlinked(Path.Combine(Paths.Local, file), bin, Paths.OfficialRoot, Path.Combine(Paths.Official, file));
            Line($"  installed {file} ({bin.Length} bytes, a file of its own; the official {file} is unchanged)");
        }
        foreach (var file in state.Installed.Keys.Where(f => !result.Files.ContainsKey(f)).ToList())
        {
            var linked = SafeFiles.RelinkOrCopy(Path.Combine(Paths.Local, file), Path.Combine(Paths.Official, file), Paths.OfficialRoot);
            Line($"  {file}: no pack changes it any more, {(linked ? "linked to" : "copied from")} the official file again");
        }
        state.Installed = result.Files.ToDictionary(f => f.Key, f => new InstalledFile(SafeFiles.Sha256(f.Value), result.PacksByFile[f.Key]), StringComparer.OrdinalIgnoreCase);
        state.Packs = [.. packs.Select(p => $"{p.Name} {p.Info.Version}".Trim())];
        state.BuiltUtc = DateTime.UtcNow;
        state.Items = result.Items;
        state.Spells = result.Spells;
        state.Save(Paths.Local);
        Line($"built {string.Join(", ", state.Packs)} into {Paths.Local}; state in {ModState.PathFor(Paths.Local)}");
        return result;
    }

    /// <summary>restore: every modded file becomes a hard link to the official file again (a copy across volumes). The recorded official hashes stay.</summary>
    public void Restore()
    {
        var state = ModState.Load(Paths.Local);
        var files = state?.Installed.Keys.ToList() ?? [];
        // Also any leftover the state does not list: a file of its own that differs from the official one.
        foreach (var path in Directory.EnumerateFiles(Paths.Local, "*.bin"))
        {
            var name = Path.GetFileName(path);
            var official = Path.Combine(Paths.Official, name);
            if (!files.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(official) && SafeFiles.LinkCount(path) == 1
                && SafeFiles.Sha256(path) != SafeFiles.Sha256(official))
                files.Add(name);
        }
        foreach (var name in files)
        {
            var official = Path.Combine(Paths.Official, name);
            if (!File.Exists(official)) { Line($"  {name}: no official file, left as it is"); continue; }
            var linked = SafeFiles.RelinkOrCopy(Path.Combine(Paths.Local, name), official, Paths.OfficialRoot);
            Line($"  {name}: {(linked ? "linked to" : "copied from")} the official file again");
        }
        foreach (var temp in Directory.EnumerateFiles(Paths.Local, "*.modkit-tmp")) File.Delete(temp);
        if (state is not null)
        {
            state.Installed.Clear();
            state.Packs.Clear();
            state.Items = null;
            state.Spells = null;
            state.Save(Paths.Local);
        }
        Line(files.Count == 0 ? "nothing to restore: game-local has the official files" : $"restored {files.Count} file(s)");
    }

    /// <summary>status: what is modded, by which packs, and whether the files are as built and the official ones as recorded.</summary>
    public void Status()
    {
        var state = ModState.Load(Paths.Local);
        Line($"game-local GameData: {Paths.Local}");
        Line($"official GameData:   {Paths.Official} (build {ModBuilder.ReadBuild(Paths.Official) ?? "unknown"})");
        if (state is null || state.Installed.Count == 0) Line("no packs installed");
        else
        {
            Line($"packs: {string.Join(", ", state.Packs)} (built {state.BuiltUtc:yyyy-MM-dd HH:mm} UTC)");
            foreach (var (file, installed) in state.Installed)
            {
                var path = Path.Combine(Paths.Local, file);
                var now = File.Exists(path) ? SafeFiles.Sha256(path) : null;
                var official = Path.Combine(Paths.Official, file);
                var officialNow = File.Exists(official) ? SafeFiles.Sha256(official) : null;
                Line($"  {file,-20} {string.Join("+", installed.Packs)}  sha256 {installed.Sha256[..12]}  "
                    + (now == installed.Sha256 ? "as built" : now is null ? "MISSING" : "CHANGED since the build")
                    + $", links {(File.Exists(path) ? SafeFiles.LinkCount(path) : 0)}"
                    + (state.Pristine.GetValueOrDefault(file) is { } p && officialNow != p ? ", OFFICIAL FILE UPDATED since the build (rebuild with --force)" : ""));
            }
            if (state.Items is { } i) Line($"  new items: {string.Join(", ", i.New.Select(n => $"{n.Key} {n.Value}"))}");
            if (state.Spells is { } s) Line($"  new spells: {string.Join(", ", s.New.Select(n => $"{n.Key} {n.Value}"))}");
        }
        var stray = Directory.EnumerateFiles(Paths.Local, "*.bin").Count(f => SafeFiles.LinkCount(f) == 1 && !(state?.Installed.ContainsKey(Path.GetFileName(f)) ?? false));
        if (stray > 0) Line($"  note: {stray} other file(s) in game-local are not linked to the official install (copies?)");
    }

    /// <summary>extract: the named files (or wildcards; "*" for all) decrypted to readable XML in <paramref name="outDir"/>, from the official folder or game-local.</summary>
    public int Extract(IEnumerable<string> names, string outDir, bool fromLocal)
    {
        var source = fromLocal ? Paths.Local : Paths.Official;
        var outFull = Path.GetFullPath(outDir);
        SafeFiles.GuardNotOfficial(Path.Combine(outFull, "x"), Paths.OfficialRoot);
        SafeFiles.GuardNotOfficial(Path.Combine(outFull, "x"), Path.GetFullPath(Path.Combine(Paths.Local, "..", "..", "..")));
        Directory.CreateDirectory(outFull);
        var count = 0;
        foreach (var pattern in names)
        {
            var file = pattern.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || pattern.Contains('*') ? pattern : pattern + ".bin";
            var matches = Directory.GetFiles(source, file, file.Contains('/') || file.Contains('\\') ? SearchOption.TopDirectoryOnly : SearchOption.TopDirectoryOnly);
            if (matches.Length == 0) Line($"  {pattern}: no such file in {source}");
            foreach (var path in matches)
            {
                var xml = GameDataFile.Open(source, Path.GetFileName(path)).DecodeXml(out _);
                var target = Path.Combine(outFull, Path.GetFileNameWithoutExtension(path) + ".xml");
                File.WriteAllBytes(target, xml);
                Line($"  {Path.GetFileName(path)} -> {target} ({xml.Length} bytes)");
                count++;
            }
        }
        return count;
    }
}
