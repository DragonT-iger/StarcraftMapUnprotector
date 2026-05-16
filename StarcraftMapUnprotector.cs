using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TkMPQLib;

internal static class StarcraftMapUnprotector
{
    private const int UnitTypeCount = 228;
    private const int UnitNameStringOffset = 14 * UnitTypeCount;

    private static readonly string[] CanonicalOrder =
    {
        "VER ", "TYPE", "IVE2", "VCOD", "IOWN", "OWNR", "SIDE", "COLR",
        "ERA ", "DIM ", "MTXM", "TILE", "ISOM", "UNIT", "PUNI", "UNIx",
        "PUPx", "UPGx", "DD2 ", "THG2", "MASK", "MRGN", "STR ", "SPRP",
        "FORC", "WAV ", "PTEx", "TECx", "MBRF", "TRIG", "UPRP", "UPUS",
        "SWNM"
    };

    private sealed class Section
    {
        public string Name;
        public byte[] Data;

        public Section(string name, byte[] data)
        {
            Name = name;
            Data = data;
        }
    }

    private sealed class Stats
    {
        public int MpqHashIndexesPatched;
        public int MpqTablesRecovered;
        public int MpqDeepRecoveryUsed;
        public int MpqDeepHeadersFound;
        public int MpqDeepTableCandidatesTried;
        public string MpqDeepRecoveryDetail = "";
        public int ExtraFilesCopied;
        public int RemovedSmlpSections;
        public int RemovedDuplicateSections;
        public int RemovedFakeUnits;
        public int RemovedFakeTriggers;
        public int RemovedTriggerComments;
        public int NormalizedTriggerStrings;
        public int NormalizedTriggerLocations;
        public int RebuiltStrings;
        public int RepairedLocations;
        public int MergedSections;
        public int AddedDefaultSections;
        public int TerrainCandidatesScanned;
        public int TerrainSectionsRepaired;
        public int IsomCandidateSelected;
        public int IsomGenerated;
        public int IsomConfidence;
        public int TileMtxmMatchRate;
        public string MtxmSelection = "";
        public string IsomRepairMode = "";
    }

    private sealed class MpqFileEntry
    {
        public string Name;
        public byte[] Data;

        public MpqFileEntry(string name, byte[] data)
        {
            Name = name;
            Data = data;
        }
    }

    private sealed class TerrainChoice
    {
        public byte[] Data;
        public int Score;
        public int Index;
    }

    private sealed class MpqHeaderCandidate
    {
        public int BaseOffset;
        public uint HashTableOffset;
        public uint BlockTableOffset;
        public int HashCount;
        public int BlockCount;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        bool pauseOnExit = true;
        args = args.Where(arg =>
        {
            if (arg == "--no-pause")
            {
                pauseOnExit = false;
                return false;
            }

            return true;
        }).ToArray();

        int exitCode;
        try
        {
            exitCode = Run(args);
        }
        finally
        {
            if (pauseOnExit)
            {
                PauseForLogReview();
            }
        }

        return exitCode;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            return RunBatchFromDefaultFolders();
        }

        if (args[0] == "-h" || args[0] == "--help")
        {
            Console.WriteLine("Usage: StarcraftMapUnprotector.exe <protected.scx|scm|scenario.chk> [output.scx]");
            Console.WriteLine("       StarcraftMapUnprotector.exe");
            Console.WriteLine();
            Console.WriteLine("No arguments: unprotects every map-like file in Maps\\Originals to Maps\\Outputs.");
            Console.WriteLine("Optional: add --no-pause to close immediately when finished.");
            return 0;
        }

        string input = Path.GetFullPath(args[0]);
        string output = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.Combine(
                Path.GetDirectoryName(input) ?? ".",
                Path.GetFileNameWithoutExtension(input) + ".unprotected" + Path.GetExtension(input));

        if (!File.Exists(input))
        {
            Console.Error.WriteLine("Input file not found: " + input);
            return 2;
        }

        bool usedDeepRecovery;
        return UnprotectOne(input, output, out usedDeepRecovery) ? 0 : 3;
    }

    private static int RunBatchFromDefaultFolders()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string inputDir = Path.Combine(root, "Maps", "Originals");
        string outputDir = Path.Combine(root, "Maps", "Outputs");

        Console.WriteLine("StarCraft Map Unprotector batch mode");
        Console.WriteLine("Input folder : " + inputDir);
        Console.WriteLine("Output folder: " + outputDir);
        Console.WriteLine();

        if (!Directory.Exists(inputDir))
        {
            Console.Error.WriteLine("Input folder not found: " + inputDir);
            return 2;
        }

        Directory.CreateDirectory(outputDir);

        string[] allowedExtensions = { ".scx", ".scm", ".tmp" };
        FileInfo[] maps = new DirectoryInfo(inputDir)
            .GetFiles()
            .Where(file => allowedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (maps.Length == 0)
        {
            Console.WriteLine("No map files found.");
            return 0;
        }

        int ok = 0;
        int failed = 0;
        int deepRecovered = 0;

        foreach (FileInfo map in maps)
        {
            string outputName = map.Name.IndexOf(".unprotected.", StringComparison.OrdinalIgnoreCase) >= 0
                ? map.Name
                : Path.GetFileNameWithoutExtension(map.Name) + ".unprotected" + map.Extension;
            string output = Path.Combine(outputDir, outputName);

            Console.WriteLine("============================================================");
            Console.WriteLine(map.Name + " -> " + outputName);
            Console.WriteLine("============================================================");

            bool usedDeepRecovery;
            if (UnprotectOne(map.FullName, output, out usedDeepRecovery))
            {
                ok++;
            }
            else
            {
                failed++;
            }

            if (usedDeepRecovery)
            {
                deepRecovered++;
            }

            Console.WriteLine();
        }

        Console.WriteLine("Done.");
        Console.WriteLine("Succeeded: " + ok);
        Console.WriteLine("Failed   : " + failed);
        Console.WriteLine("Total    : " + maps.Length);
        Console.WriteLine("MPQ deep recovery used: " + deepRecovered);

        return failed == 0 ? 0 : 3;
    }

    private static bool UnprotectOne(string input, string output, out bool usedDeepRecovery)
    {
        usedDeepRecovery = false;
        try
        {
            var stats = new Stats();
            List<MpqFileEntry> extraFiles;
            byte[] inputBytes = File.ReadAllBytes(input);
            byte[] chk;
            if (LooksLikeChk(inputBytes))
            {
                chk = inputBytes;
                extraFiles = new List<MpqFileEntry>();
            }
            else
            {
                chk = ExtractScenarioChk(input, stats, out extraFiles);
            }
            List<Section> sections = ParseChk(chk);

            if (sections.Count == 0)
            {
                throw new InvalidDataException("scenario.chk could not be parsed.");
            }

            byte[] normalized = BuildNormalizedChk(sections, stats);
            WriteStandardMpq(output, normalized, extraFiles);
            usedDeepRecovery = stats.MpqDeepRecoveryUsed > 0;

            Console.WriteLine("Input : " + input);
            Console.WriteLine("Output: " + output);
            Console.WriteLine("scenario.chk: " + chk.Length + " bytes -> " + normalized.Length + " bytes");
            Console.WriteLine("MPQ hash indexes patched: " + stats.MpqHashIndexesPatched);
            Console.WriteLine("MPQ tables recovered    : " + stats.MpqTablesRecovered);
            Console.WriteLine("MPQ deep recovery used  : " + stats.MpqDeepRecoveryUsed);
            if (stats.MpqDeepRecoveryDetail.Length > 0)
            {
                Console.WriteLine("MPQ deep recovery detail: " + stats.MpqDeepRecoveryDetail);
            }
            Console.WriteLine("extra files copied      : " + stats.ExtraFilesCopied);
            Console.WriteLine("SMLP sections removed    : " + stats.RemovedSmlpSections);
            Console.WriteLine("duplicate sections fixed : " + stats.RemovedDuplicateSections);
            Console.WriteLine("split sections merged    : " + stats.MergedSections);
            Console.WriteLine("fake UNIT records removed: " + stats.RemovedFakeUnits);
            Console.WriteLine("fake TRIG records removed: " + stats.RemovedFakeTriggers);
            Console.WriteLine("trigger comments removed : " + stats.RemovedTriggerComments);
            Console.WriteLine("trigger strings normalized: " + stats.NormalizedTriggerStrings);
            Console.WriteLine("trigger locations fixed  : " + stats.NormalizedTriggerLocations);
            Console.WriteLine("string table rebuilt     : " + stats.RebuiltStrings);
            Console.WriteLine("locations repaired       : " + stats.RepairedLocations);
            Console.WriteLine("default sections added   : " + stats.AddedDefaultSections);
            Console.WriteLine("terrain candidates scanned: " + stats.TerrainCandidatesScanned);
            Console.WriteLine("terrain sections repaired : " + stats.TerrainSectionsRepaired);
            Console.WriteLine("ISOM candidate selected   : " + stats.IsomCandidateSelected);
            Console.WriteLine("ISOM generated            : " + stats.IsomGenerated);
            Console.WriteLine("ISOM repair mode          : " + stats.IsomRepairMode);
            Console.WriteLine("MTXM selection            : " + stats.MtxmSelection);
            Console.WriteLine("ISOM confidence           : " + stats.IsomConfidence + "%");
            Console.WriteLine("TILE/MTXM match rate      : " + stats.TileMtxmMatchRate + "%");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed: " + ex.Message);
            return false;
        }
    }

    private static void PauseForLogReview()
    {
        Console.WriteLine();
        Console.Write("Press Enter to close...");
        try
        {
            Console.ReadLine();
        }
        catch
        {
        }
    }

    private static byte[] ExtractScenarioChk(string input, Stats stats, out List<MpqFileEntry> extraFiles)
    {
        extraFiles = new List<MpqFileEntry>();

        using (var mpq = new TkMPQ(input))
        {
            RecoverShiftedProtectedTables(input, mpq, stats);
            PatchProtectedHashIndexes(mpq, stats);
            extraFiles = ExtractExtraFiles(mpq, stats);

            byte[] data = TryReadScenarioChkFromMpq(mpq);
            if (data != null)
            {
                return data;
            }
        }

        List<MpqFileEntry> recoveredExtraFiles;
        byte[] recovered = TryRecoverScenarioChkAggressively(input, stats, out recoveredExtraFiles);
        if (recovered != null)
        {
            extraFiles = recoveredExtraFiles;
            return recovered;
        }

        string detail = stats.MpqDeepRecoveryDetail.Length > 0 ? " " + stats.MpqDeepRecoveryDetail : "";
        throw new InvalidDataException("Could not find a readable staredit\\scenario.chk." + detail);
    }

    private static byte[] TryReadScenarioChkFromMpq(TkMPQ mpq)
    {
        string[] names =
        {
            "staredit\\scenario.chk",
            "staredit/scenario.chk",
            "scenario.chk"
        };

        foreach (string name in names)
        {
            foreach (Locale locale in GetKnownLocales())
            {
                try
                {
                    using (MPQReader reader = mpq.GetFile(name, locale))
                    {
                        if (reader == null)
                        {
                            continue;
                        }

                        byte[] data = reader.ToArray();
                        if (LooksLikeChk(data) && ParseChk(data).Count > 0)
                        {
                            return data;
                        }
                    }
                }
                catch
                {
                    // Protected MPQs often deliberately point hashes at malformed blocks.
                }
            }
        }

        return null;
    }

    private static Locale[] GetKnownLocales()
    {
        return new[]
        {
            Locale.English, Locale.Neutral, Locale.Korean, Locale.Japanese,
            Locale.Chinese, Locale.EnglishUK, Locale.German, Locale.French,
            Locale.Spanish, Locale.Italian, Locale.Polish, Locale.Portuguese,
            Locale.Russsuan, Locale.Czech
        };
    }

    private static List<MpqFileEntry> ExtractExtraFiles(TkMPQ mpq, Stats stats)
    {
        var result = new List<MpqFileEntry>();
        byte[] listfile = TryReadMpqFile(mpq, "(listfile)");
        if (listfile == null || listfile.Length == 0)
        {
            return result;
        }

        string text = Encoding.Default.GetString(listfile);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in text.Replace("\r", "\n").Split('\n'))
        {
            string name = rawLine.Trim();
            if (name.Length == 0 || name.Equals("staredit\\scenario.chk", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("staredit/scenario.chk", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("(listfile)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(name))
            {
                continue;
            }

            byte[] data = TryReadMpqFile(mpq, name);
            if (data == null)
            {
                continue;
            }

            result.Add(new MpqFileEntry(name, data));
            stats.ExtraFilesCopied++;
        }

        return result;
    }

    private static byte[] TryReadMpqFile(TkMPQ mpq, string name)
    {
        foreach (Locale locale in GetKnownLocales())
        {
            try
            {
                using (MPQReader reader = mpq.GetFile(name, locale))
                {
                    if (reader != null)
                    {
                        return reader.ToArray();
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static byte[] TryRecoverScenarioChkAggressively(string input, Stats stats, out List<MpqFileEntry> extraFiles)
    {
        extraFiles = new List<MpqFileEntry>();

        byte[] file = File.ReadAllBytes(input);
        List<MpqHeaderCandidate> headers = FindMpqHeaderCandidates(file);
        stats.MpqDeepHeadersFound = headers.Count;
        if (headers.Count == 0)
        {
            stats.MpqDeepRecoveryDetail = "deep recovery failed: no MPQ header candidates";
            return null;
        }

        bool sawScenarioHash = false;
        bool sawUsableBlock = false;
        bool sawReadableMpq = false;
        bool sawBadChk = false;

        foreach (MpqHeaderCandidate header in headers)
        {
            List<int> hashCandidates = BuildTableOffsetCandidates(file.Length, header.BaseOffset, header.HashTableOffset, header.HashCount);
            List<int> blockCandidates = BuildTableOffsetCandidates(file.Length, header.BaseOffset, header.BlockTableOffset, header.BlockCount);

            foreach (int hashOffset in hashCandidates)
            {
                HashTable[] hashes;
                try
                {
                    hashes = ReadHashTable(file, hashOffset, header.HashCount);
                }
                catch
                {
                    continue;
                }

                stats.MpqDeepTableCandidatesTried++;
                if (!hashes.Any(IsScenarioHash))
                {
                    continue;
                }

                sawScenarioHash = true;
                int afterHash = hashOffset + header.HashCount * 16;
                AddCandidateOffset(blockCandidates, afterHash, header.BlockCount * 16, file.Length);

                foreach (int blockOffset in blockCandidates)
                {
                    BlockTable[] blocks;
                    try
                    {
                        blocks = ReadBlockTable(file, blockOffset, header.BlockCount);
                    }
                    catch
                    {
                        continue;
                    }

                    stats.MpqDeepTableCandidatesTried++;
                    FixProtectedBlockSizes(blocks, file.Length - header.BaseOffset);
                    if (!LooksLikeDeepRecoveredTables(hashes, blocks, file.Length - header.BaseOffset))
                    {
                        continue;
                    }

                    sawUsableBlock = true;
                    byte[] data = TryReadWithRecoveredTables(file, header.BaseOffset, hashes, blocks, stats, out extraFiles);
                    if (data != null)
                    {
                        stats.MpqDeepRecoveryUsed++;
                        stats.MpqDeepRecoveryDetail =
                            "headers=" + headers.Count +
                            ", tableCandidates=" + stats.MpqDeepTableCandidatesTried +
                            ", headerBase=0x" + header.BaseOffset.ToString("X") +
                            ", hash=0x" + hashOffset.ToString("X") +
                            ", block=0x" + blockOffset.ToString("X");
                        return data;
                    }

                    sawReadableMpq = true;

                    BlockTable[] adjustedBlocks = TryBuildBaseAdjustedBlocks(blocks, header.BaseOffset, file.Length - header.BaseOffset);
                    if (adjustedBlocks != null)
                    {
                        data = TryReadWithRecoveredTables(file, header.BaseOffset, hashes, adjustedBlocks, stats, out extraFiles);
                        if (data != null)
                        {
                            stats.MpqDeepRecoveryUsed++;
                            stats.MpqDeepRecoveryDetail =
                                "headers=" + headers.Count +
                                ", tableCandidates=" + stats.MpqDeepTableCandidatesTried +
                                ", headerBase=0x" + header.BaseOffset.ToString("X") +
                                ", hash=0x" + hashOffset.ToString("X") +
                                ", block=0x" + blockOffset.ToString("X") +
                                ", adjustedBlockOffsets=1";
                            return data;
                        }
                    }
                    else
                    {
                        sawBadChk = true;
                    }
                }
            }
        }

        string reason = "no table candidates";
        if (!sawScenarioHash)
        {
            reason = "scenario hash not found";
        }
        else if (!sawUsableBlock)
        {
            reason = "usable scenario block not found";
        }
        else if (sawReadableMpq || sawBadChk)
        {
            reason = "recovered data did not look like CHK";
        }

        stats.MpqDeepRecoveryDetail =
            "deep recovery failed: " + reason +
            ", headers=" + headers.Count +
            ", tableCandidates=" + stats.MpqDeepTableCandidatesTried;
        return null;
    }

    private static byte[] TryReadWithRecoveredTables(
        byte[] file,
        int headerBase,
        HashTable[] hashes,
        BlockTable[] blocks,
        Stats stats,
        out List<MpqFileEntry> extraFiles)
    {
        extraFiles = new List<MpqFileEntry>();

        try
        {
            byte[] archiveBytes = new byte[file.Length - headerBase];
            Buffer.BlockCopy(file, headerBase, archiveBytes, 0, archiveBytes.Length);
            using (var stream = new MemoryStream(archiveBytes, false))
            using (var mpq = new TkMPQ(stream))
            {
                SetRecoveredTables(mpq, hashes, blocks);
                PatchProtectedHashIndexes(mpq, stats);

                byte[] data = TryReadScenarioChkFromMpq(mpq);
                if (data == null)
                {
                    return null;
                }

                extraFiles = ExtractExtraFiles(mpq, stats);
                return data;
            }
        }
        catch
        {
            return null;
        }
    }

    private static List<MpqHeaderCandidate> FindMpqHeaderCandidates(byte[] file)
    {
        var result = new List<MpqHeaderCandidate>();
        for (int offset = 0; offset + 32 <= file.Length; offset += 4)
        {
            if (file[offset] != (byte)'M' || file[offset + 1] != (byte)'P' ||
                file[offset + 2] != (byte)'Q' || file[offset + 3] != 0x1A)
            {
                continue;
            }

            uint hashOffset = BitConverter.ToUInt32(file, offset + 16);
            uint blockOffset = BitConverter.ToUInt32(file, offset + 20);
            int hashCount = (int)(BitConverter.ToUInt32(file, offset + 24) & 0x0FFFFFFF);
            int blockCount = (int)(BitConverter.ToUInt32(file, offset + 28) & 0x0FFFFFFF);
            if (hashCount <= 0 || hashCount > 65536 || blockCount <= 0 || blockCount > 65536)
            {
                continue;
            }

            result.Add(new MpqHeaderCandidate
            {
                BaseOffset = offset,
                HashTableOffset = hashOffset,
                BlockTableOffset = blockOffset,
                HashCount = hashCount,
                BlockCount = blockCount
            });
        }

        return result;
    }

    private static List<int> BuildTableOffsetCandidates(int fileLength, int headerBase, uint tableOffset, int entryCount)
    {
        int tableLength = entryCount * 16;
        var candidates = new List<int>();
        if (tableLength <= 0 || tableLength > fileLength)
        {
            return candidates;
        }

        long relative = tableOffset;
        long absolute = headerBase + relative;
        AddCandidateOffset(candidates, absolute, tableLength, fileLength);
        AddCandidateOffset(candidates, absolute - 256, tableLength, fileLength);
        AddCandidateOffset(candidates, absolute - 512, tableLength, fileLength);
        AddCandidateOffset(candidates, absolute - 1024, tableLength, fileLength);
        AddCandidateOffset(candidates, relative, tableLength, fileLength);
        AddCandidateOffset(candidates, relative - 256, tableLength, fileLength);
        AddCandidateOffset(candidates, relative - 512, tableLength, fileLength);
        AddCandidateOffset(candidates, relative - 1024, tableLength, fileLength);

        int tailStart = Math.Max(32, fileLength - 262144);
        for (int offset = tailStart; offset + tableLength <= fileLength; offset += 4)
        {
            AddCandidateOffset(candidates, offset, tableLength, fileLength);
        }

        int windowStart = (int)Math.Max(32, absolute - 8192);
        int windowEnd = (int)Math.Min(fileLength - tableLength, absolute + 8192);
        for (int offset = windowStart; offset <= windowEnd; offset += 4)
        {
            AddCandidateOffset(candidates, offset, tableLength, fileLength);
        }

        return candidates.Distinct().ToList();
    }

    private static void AddCandidateOffset(List<int> candidates, long offset, int length, int fileLength)
    {
        if (offset < 0 || offset > Int32.MaxValue)
        {
            return;
        }

        int value = (int)offset;
        if ((long)value + length <= fileLength)
        {
            candidates.Add(value);
        }
    }

    private static bool LooksLikeDeepRecoveredTables(HashTable[] hashes, BlockTable[] blocks, int archiveLength)
    {
        if (!hashes.Any(IsScenarioHash))
        {
            return false;
        }

        int scenarioBlock = FindUsableScenarioBlock(hashes, blocks);
        if (scenarioBlock >= 0 && IsBlockInArchive(blocks[scenarioBlock], archiveLength))
        {
            return true;
        }

        return blocks.Any(block => IsBlockInArchive(block, archiveLength));
    }

    private static bool IsBlockInArchive(BlockTable block, int archiveLength)
    {
        if (block.FileOffset < 0 || block.FileSize == 0 || (((uint)block.Flags & (uint)Flags.Exists) == 0))
        {
            return false;
        }

        if (block.CompSize == 0 || block.CompSize == UInt32.MaxValue || block.CompSize > Int32.MaxValue)
        {
            return false;
        }

        long end = (long)block.FileOffset + block.CompSize;
        return end > block.FileOffset && end <= archiveLength;
    }

    private static void FixProtectedBlockSizes(BlockTable[] blocks, int archiveLength)
    {
        for (int i = 0; i < blocks.Length; i++)
        {
            BlockTable block = blocks[i];
            if (block.FileOffset < 0 || block.CompSize != UInt32.MaxValue)
            {
                continue;
            }

            int next = archiveLength;
            for (int j = 0; j < blocks.Length; j++)
            {
                int other = blocks[j].FileOffset;
                if (other > block.FileOffset && other < next)
                {
                    next = other;
                }
            }

            if (next > block.FileOffset)
            {
                block.CompSize = (uint)(next - block.FileOffset);
                blocks[i] = block;
            }
        }
    }

    private static BlockTable[] TryBuildBaseAdjustedBlocks(BlockTable[] blocks, int headerBase, int archiveLength)
    {
        if (headerBase <= 0)
        {
            return null;
        }

        var adjusted = new BlockTable[blocks.Length];
        bool changed = false;
        for (int i = 0; i < blocks.Length; i++)
        {
            BlockTable block = blocks[i];
            if (block.FileOffset >= headerBase)
            {
                block.FileOffset -= headerBase;
                changed = true;
            }

            adjusted[i] = block;
        }

        return changed && adjusted.Any(block => IsBlockInArchive(block, archiveLength)) ? adjusted : null;
    }

    private static void SetRecoveredTables(TkMPQ mpq, HashTable[] hashes, BlockTable[] blocks)
    {
        typeof(TkMPQ).GetField("HashTables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SetValue(mpq, hashes);
        typeof(TkMPQ).GetField("BlockTables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SetValue(mpq, blocks);
        FieldInfo sectionField = typeof(TkMPQ).GetField("SectionSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (sectionField != null)
        {
            sectionField.SetValue(mpq, (ushort)3);
        }
        FieldInfo versionField = typeof(TkMPQ).GetField("MPQVersion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (versionField != null)
        {
            versionField.SetValue(mpq, (ushort)0);
        }
    }

    private static void PatchProtectedHashIndexes(TkMPQ mpq, Stats stats)
    {
        FieldInfo hashField = typeof(TkMPQ).GetField("HashTables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo blockField = typeof(TkMPQ).GetField("BlockTables", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (hashField == null || blockField == null)
        {
            return;
        }

        var hashes = (HashTable[])hashField.GetValue(mpq);
        var blocks = (BlockTable[])blockField.GetValue(mpq);
        if (hashes == null || blocks == null)
        {
            return;
        }

        for (int i = 0; i < hashes.Length; i++)
        {
            HashTable hash = hashes[i];
            int blockIndex = hash.BlockTable;
            if (blockIndex < blocks.Length && blockIndex >= 0)
            {
                continue;
            }

            int masked = blockIndex & 0x0FFFFFFF;
            if (masked >= 0 && masked < blocks.Length)
            {
                hash.BlockTable = masked;
                hashes[i] = hash;
                stats.MpqHashIndexesPatched++;
            }
        }

        int scenarioBlock = FindUsableScenarioBlock(hashes, blocks);
        if (scenarioBlock >= 0)
        {
            for (int i = 0; i < hashes.Length; i++)
            {
                HashTable hash = hashes[i];
                if (IsScenarioHash(hash) && !IsUsableBlockIndex(hash.BlockTable, blocks))
                {
                    hash.BlockTable = scenarioBlock;
                    hash.Platform = 0;
                    hashes[i] = hash;
                    stats.MpqHashIndexesPatched++;
                }
            }
        }

        int dataEnd = GetProtectedTableStart(blocks);
        if (dataEnd > 0)
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                BlockTable block = blocks[i];
                if (block.FileOffset >= 0 && block.CompSize == UInt32.MaxValue)
                {
                    int next = dataEnd;
                    for (int j = 0; j < blocks.Length; j++)
                    {
                        int other = blocks[j].FileOffset;
                        if (other > block.FileOffset && other < next)
                        {
                            next = other;
                        }
                    }

                    if (next > block.FileOffset)
                    {
                        block.CompSize = (uint)(next - block.FileOffset);
                        blocks[i] = block;
                        stats.MpqHashIndexesPatched++;
                    }
                }
            }
        }
    }

    private static void RecoverShiftedProtectedTables(string input, TkMPQ mpq, Stats stats)
    {
        byte[] file = File.ReadAllBytes(input);
        if (file.Length < 32 || Encoding.ASCII.GetString(file, 0, 4) != "MPQ\x1A")
        {
            return;
        }

        uint headerHashOffset = BitConverter.ToUInt32(file, 16);
        uint headerBlockOffset = BitConverter.ToUInt32(file, 20);
        if (headerHashOffset < file.Length && headerBlockOffset < file.Length)
        {
            return;
        }

        int hashCount = (int)(BitConverter.ToUInt32(file, 24) & 0x0FFFFFFF);
        int blockCount = (int)(BitConverter.ToUInt32(file, 28) & 0x0FFFFFFF);
        if (hashCount <= 0 || hashCount > 65536 || blockCount <= 0 || blockCount > 65536)
        {
            return;
        }

        int hashOffset = FindEncryptedHashTable(file, headerHashOffset, hashCount);
        if (hashOffset < 0)
        {
            return;
        }

        int blockOffset = -1;
        if (headerBlockOffset >= 512 && headerBlockOffset - 512 + (uint)(blockCount * 16) <= file.Length)
        {
            blockOffset = (int)headerBlockOffset - 512;
        }

        if (blockOffset < 0)
        {
            blockOffset = hashOffset + hashCount * 16;
        }

        if (blockOffset < 0 || blockOffset + blockCount * 16 > file.Length)
        {
            return;
        }

        HashTable[] hashes = ReadHashTable(file, hashOffset, hashCount);
        BlockTable[] blocks = ReadBlockTable(file, blockOffset, blockCount);
        if (!LooksLikeRecoveredTables(hashes, blocks))
        {
            return;
        }

        SetRecoveredTables(mpq, hashes, blocks);

        stats.MpqTablesRecovered++;
    }

    private static int FindEncryptedHashTable(byte[] file, uint headerHashOffset, int hashCount)
    {
        var candidates = new List<int>();
        if (headerHashOffset >= 512)
        {
            candidates.Add((int)headerHashOffset - 512);
        }

        if (headerHashOffset + (uint)(hashCount * 16) <= file.Length)
        {
            candidates.Add((int)headerHashOffset);
        }

        int tailStart = Math.Max(32, file.Length - 4096);
        for (int offset = tailStart; offset + hashCount * 16 <= file.Length; offset += 4)
        {
            candidates.Add(offset);
        }

        foreach (int offset in candidates.Distinct())
        {
            if (offset < 0 || offset + hashCount * 16 > file.Length)
            {
                continue;
            }

            HashTable[] hashes = ReadHashTable(file, offset, hashCount);
            if (hashes.Any(IsScenarioHash))
            {
                return offset;
            }
        }

        return -1;
    }

    private static HashTable[] ReadHashTable(byte[] file, int offset, int count)
    {
        byte[] data = ReadAndDecrypt(file, offset, count * 16, "(hash table)");
        var hashes = new HashTable[count];
        for (int i = 0; i < count; i++)
        {
            int p = i * 16;
            hashes[i] = new HashTable
            {
                NameA = BitConverter.ToUInt32(data, p),
                NameB = BitConverter.ToUInt32(data, p + 4),
                Locale = (Locale)BitConverter.ToUInt16(data, p + 8),
                Platform = BitConverter.ToUInt16(data, p + 10),
                BlockTable = BitConverter.ToInt32(data, p + 12)
            };
        }

        return hashes;
    }

    private static BlockTable[] ReadBlockTable(byte[] file, int offset, int count)
    {
        byte[] data = ReadAndDecrypt(file, offset, count * 16, "(block table)");
        var blocks = new BlockTable[count];
        for (int i = 0; i < count; i++)
        {
            int p = i * 16;
            blocks[i] = new BlockTable
            {
                FileOffset = BitConverter.ToInt32(data, p),
                CompSize = BitConverter.ToUInt32(data, p + 4),
                FileSize = BitConverter.ToUInt32(data, p + 8),
                Flags = (Flags)BitConverter.ToUInt32(data, p + 12)
            };
        }

        return blocks;
    }

    private static byte[] ReadAndDecrypt(byte[] file, int offset, int length, string keyName)
    {
        byte[] data = new byte[length];
        Buffer.BlockCopy(file, offset, data, 0, length);
        uint key = Encryption.HashString(keyName, Encryption.HashType.Hash_FileKey);
        Encryption.DecryptData(ref data, key);
        return data;
    }

    private static bool LooksLikeRecoveredTables(HashTable[] hashes, BlockTable[] blocks)
    {
        return hashes.Any(IsScenarioHash) &&
               blocks.Any(b => b.FileOffset >= 0 && b.FileSize > 0 && (((uint)b.Flags & (uint)Flags.Exists) != 0));
    }

    private static bool IsScenarioHash(HashTable hash)
    {
        return hash.NameA == Encryption.HashString("staredit\\scenario.chk", Encryption.HashType.Hash_Name_A) &&
               hash.NameB == Encryption.HashString("staredit\\scenario.chk", Encryption.HashType.Hash_Name_B);
    }

    private static int FindUsableScenarioBlock(HashTable[] hashes, BlockTable[] blocks)
    {
        foreach (HashTable hash in hashes)
        {
            if (IsScenarioHash(hash) && IsUsableBlockIndex(hash.BlockTable, blocks))
            {
                return hash.BlockTable;
            }
        }

        return -1;
    }

    private static bool IsUsableBlockIndex(int index, BlockTable[] blocks)
    {
        if (index < 0 || index >= blocks.Length)
        {
            return false;
        }

        BlockTable block = blocks[index];
        return block.FileOffset >= 0 && block.FileSize > 0 && (((uint)block.Flags & (uint)Flags.Exists) != 0);
    }

    private static int GetProtectedTableStart(BlockTable[] blocks)
    {
        int maxEnd = -1;
        foreach (BlockTable block in blocks)
        {
            if (block.FileOffset >= 0 && block.CompSize != UInt32.MaxValue && block.CompSize < Int32.MaxValue)
            {
                maxEnd = Math.Max(maxEnd, block.FileOffset + (int)block.CompSize);
            }
        }

        if (maxEnd > 0)
        {
            return maxEnd;
        }

        int maxOffset = -1;
        foreach (BlockTable block in blocks)
        {
            if (block.FileOffset >= 0)
            {
                maxOffset = Math.Max(maxOffset, block.FileOffset);
            }
        }

        return maxOffset > 0 ? maxOffset : -1;
    }

    private static bool LooksLikeChk(byte[] data)
    {
        if (data == null || data.Length < 10)
        {
            return false;
        }

        string first = Encoding.ASCII.GetString(data, 0, 4);
        return first == "SMLP" || IsPlausibleSectionName(first);
    }

    private static List<Section> ParseChk(byte[] data)
    {
        var sections = new List<Section>();
        int pos = 0;

        while (pos + 8 <= data.Length)
        {
            string name = Encoding.ASCII.GetString(data, pos, 4);
            uint size32 = BitConverter.ToUInt32(data, pos + 4);
            if (size32 > int.MaxValue)
            {
                break;
            }

            int size = (int)size32;
            if (pos + 8 + size > data.Length)
            {
                break;
            }

            if (!IsPlausibleSectionName(name))
            {
                pos += 8 + size;
                continue;
            }

            byte[] sectionData = new byte[size];
            Buffer.BlockCopy(data, pos + 8, sectionData, 0, size);
            sections.Add(new Section(name, sectionData));
            pos += 8 + size;
        }

        return sections;
    }

    private static bool IsPlausibleSectionName(string name)
    {
        if (name == "SMLP")
        {
            return true;
        }

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool ok =
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == ' ';

            if (!ok)
            {
                return false;
            }
        }

        return name.Trim().Length > 0;
    }

    private static byte[] BuildNormalizedChk(List<Section> input, Stats stats)
    {
        var grouped = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        foreach (Section section in input)
        {
            if (section.Name == "SMLP")
            {
                stats.RemovedSmlpSections++;
                continue;
            }

            List<byte[]> list;
            if (!grouped.TryGetValue(section.Name, out list))
            {
                list = new List<byte[]>();
                grouped.Add(section.Name, list);
            }

            list.Add(section.Data);
        }

        NormalizeSingleSection(grouped, "VER ", IsValidVer, ChooseLastValid, stats);
        NormalizeSingleSection(grouped, "DIM ", IsValidDim, ChooseLastValid, stats);

        MergeRepeated(grouped, "TRIG", 2400, stats);
        MergeRepeated(grouped, "MBRF", 2400, stats);
        TrimRecordSection(grouped, "TRIG", 2400);
        TrimRecordSection(grouped, "MBRF", 2400);

        RemoveFakeUnitRecords(grouped, stats);
        RemoveFakeTriggerRecords(grouped, stats);
        RepairLocations(grouped, stats);
        NormalizeStringTableAndReferences(grouped, stats);
        NormalizeTriggerRecords(grouped, "TRIG", stats);
        NormalizeTriggerRecords(grouped, "MBRF", stats);
        NormalizeCoreMapSections(grouped);

        AddDefaultSections(grouped, stats);
        EnsureValidStringTable(grouped);

        var result = new List<Section>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string name in CanonicalOrder)
        {
            List<byte[]> list;
            if (grouped.TryGetValue(name, out list))
            {
                foreach (byte[] data in list)
                {
                    result.Add(new Section(name, data));
                }

                seen.Add(name);
            }
        }

        foreach (var pair in grouped.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (seen.Contains(pair.Key))
            {
                continue;
            }

            foreach (byte[] data in pair.Value)
            {
                result.Add(new Section(pair.Key, data));
            }
        }

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.ASCII))
        {
            foreach (Section section in result)
            {
                byte[] name = Encoding.ASCII.GetBytes(section.Name);
                if (name.Length != 4)
                {
                    continue;
                }

                writer.Write(name);
                writer.Write(section.Data.Length);
                writer.Write(section.Data);
            }

            return ms.ToArray();
        }
    }

    private static void NormalizeSingleSection(
        Dictionary<string, List<byte[]>> grouped,
        string name,
        Func<byte[], bool> isValid,
        Func<List<byte[]>, Func<byte[], bool>, byte[]> chooser,
        Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count <= 1)
        {
            return;
        }

        byte[] chosen = chooser(list, isValid);
        if (chosen != null)
        {
            grouped[name] = new List<byte[]> { chosen };
            stats.RemovedDuplicateSections += list.Count - 1;
        }
    }

    private static byte[] ChooseLastValid(List<byte[]> list, Func<byte[], bool> isValid)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (isValid(list[i]))
            {
                return list[i];
            }
        }

        return list[list.Count - 1];
    }

    private static bool IsValidVer(byte[] data)
    {
        if (data.Length != 2)
        {
            return false;
        }

        ushort version = BitConverter.ToUInt16(data, 0);
        return version != 0 && version < 1000;
    }

    private static bool IsValidDim(byte[] data)
    {
        if (data.Length != 4)
        {
            return false;
        }

        ushort width = BitConverter.ToUInt16(data, 0);
        ushort height = BitConverter.ToUInt16(data, 2);
        return IsPlausibleMapDimension(width) && IsPlausibleMapDimension(height);
    }

    private static bool IsPlausibleMapDimension(ushort value)
    {
        return value == 64 || value == 96 || value == 128 || value == 192 || value == 256;
    }

    private static void MergeRepeated(Dictionary<string, List<byte[]>> grouped, string name, int recordSize, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count <= 1)
        {
            return;
        }

        int total = list.Sum(d => d.Length);
        byte[] merged = new byte[total];
        int pos = 0;
        foreach (byte[] part in list)
        {
            Buffer.BlockCopy(part, 0, merged, pos, part.Length);
            pos += part.Length;
        }

        grouped[name] = new List<byte[]> { merged };
        stats.MergedSections++;

        if (recordSize > 0 && merged.Length % recordSize != 0)
        {
            Console.Error.WriteLine("Warning: " + name + " size is not aligned to " + recordSize + " bytes.");
        }
    }

    private static void TrimRecordSection(Dictionary<string, List<byte[]>> grouped, string name, int recordSize)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0 || recordSize <= 0)
        {
            return;
        }

        byte[] data = list[0];
        int alignedLength = (data.Length / recordSize) * recordSize;
        if (alignedLength == data.Length)
        {
            return;
        }

        byte[] trimmed = new byte[alignedLength];
        Buffer.BlockCopy(data, 0, trimmed, 0, alignedLength);
        list[0] = trimmed;
    }

    private static void RemoveFakeUnitRecords(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("UNIT", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if (data.Length % 36 != 0)
        {
            return;
        }

        var kept = new List<byte>();
        for (int pos = 0; pos < data.Length; pos += 36)
        {
            bool allFF = true;
            for (int i = 0; i < 36; i++)
            {
                if (data[pos + i] != 0xFF)
                {
                    allFF = false;
                    break;
                }
            }

            ushort unitId = BitConverter.ToUInt16(data, pos + 8);
            byte player = data[pos + 16];

            if (allFF || unitId > 232 || player > 11)
            {
                stats.RemovedFakeUnits++;
                continue;
            }

            kept.AddRange(data.Skip(pos).Take(36));
        }

        list[0] = kept.ToArray();
    }

    private static void RemoveFakeTriggerRecords(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("TRIG", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if (data.Length % 2400 != 0)
        {
            return;
        }

        var kept = new List<byte>(data.Length);
        for (int pos = 0; pos < data.Length; pos += 2400)
        {
            if (LooksLikeFakeTrigger(data, pos))
            {
                stats.RemovedFakeTriggers++;
                continue;
            }

            kept.AddRange(data.Skip(pos).Take(2400));
        }

        list[0] = kept.ToArray();
    }

    private static bool LooksLikeFakeTrigger(byte[] data, int offset)
    {
        bool allZero = true;
        bool allFF = true;

        for (int i = 0; i < 2400; i++)
        {
            byte value = data[offset + i];
            if (value != 0x00)
            {
                allZero = false;
            }

            if (value != 0xFF)
            {
                allFF = false;
            }
        }

        if (allZero || allFF)
        {
            return true;
        }

        // SMLP's fake trigger commonly has impossible player flags at the end.
        bool tailAllFF = true;
        for (int i = 2368; i < 2400; i++)
        {
            if (data[offset + i] != 0xFF)
            {
                tailAllFF = false;
                break;
            }
        }

        return tailAllFF;
    }

    private static void NormalizeTriggerRecords(Dictionary<string, List<byte[]>> grouped, string sectionName, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(sectionName, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if (data.Length % 2400 != 0)
        {
            return;
        }

        ushort strCount = 0;
        List<byte[]> strList;
        if (grouped.TryGetValue("STR ", out strList) && strList.Count > 0 && strList[0].Length >= 2)
        {
            strCount = BitConverter.ToUInt16(strList[0], 0);
        }

        for (int triggerOffset = 0; triggerOffset < data.Length; triggerOffset += 2400)
        {
            NormalizeTriggerConditionLocations(data, triggerOffset, stats);
            NormalizeTriggerActions(data, triggerOffset, strCount, stats);

            for (int i = 0; i < 28; i++)
            {
                int p = triggerOffset + 2372 + i;
                data[p] = data[p] == 0 ? (byte)0 : (byte)1;
            }
        }
    }

    private static void NormalizeStringTableAndReferences(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        List<byte[]> strList;
        if (!grouped.TryGetValue("STR ", out strList) || strList.Count == 0 || strList[0].Length < 2)
        {
            grouped["STR "] = new List<byte[]> { BuildFallbackStringTable() };
            grouped.Remove("UNIx");
            stats.RebuiltStrings++;
            return;
        }

        byte[] str = strList[0];
        string[] strings = ReadStringTableTolerant(str);
        var used = new SortedSet<ushort>();
        CollectStringReferences(grouped, strings, used);

        if (used.Count == 0)
        {
            grouped["STR "] = new List<byte[]> { BuildFallbackStringTable() };
            grouped.Remove("UNIx");
            stats.RebuiltStrings++;
            return;
        }

        var remap = new Dictionary<ushort, ushort>();
        var rebuilt = new List<string>();
        foreach (ushort oldId in used)
        {
            remap[oldId] = (ushort)(rebuilt.Count + 1);
            rebuilt.Add(strings[oldId]);
        }

        RemapStringReferences(grouped, remap);
        ushort strCount = (ushort)Math.Max(1024, rebuilt.Count);
        grouped["STR "] = new List<byte[]> { BuildStringTable(rebuilt, strCount) };
        stats.RebuiltStrings++;
    }

    private static void EnsureValidStringTable(Dictionary<string, List<byte[]>> grouped)
    {
        List<byte[]> list;
        if (grouped.TryGetValue("STR ", out list) && list.Count > 0 && IsValidStringTable(list[0]))
        {
            grouped["STR "] = new List<byte[]> { list[0] };
            return;
        }

        grouped["STR "] = new List<byte[]>
        {
            BuildFallbackStringTable()
        };
    }

    private static byte[] BuildFallbackStringTable()
    {
        return BuildStringTable(new[]
        {
            "Recovered Map",
            "Recovered by StarcraftMapUnprotector",
            "Force 1",
            "Force 2",
            "Force 3",
            "Force 4"
        }, 1024);
    }

    private static ushort GetStringCount(Dictionary<string, List<byte[]>> grouped)
    {
        List<byte[]> strList;
        if (!grouped.TryGetValue("STR ", out strList) || strList.Count == 0 || strList[0].Length < 2)
        {
            return 0;
        }

        byte[] str = strList[0];
        ushort count = BitConverter.ToUInt16(str, 0);
        if (!IsValidStringTable(str))
        {
            return 0;
        }

        return count;
    }

    private static bool IsValidStringTable(byte[] str)
    {
        if (str == null || str.Length < 2)
        {
            return false;
        }

        ushort count = BitConverter.ToUInt16(str, 0);
        int tableEnd = 2 + count * 2;
        if (count == 0 || tableEnd > str.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            ushort offset = BitConverter.ToUInt16(str, 2 + i * 2);
            if (offset != 0 && offset >= str.Length)
            {
                return false;
            }
        }

        return true;
    }

    private static string[] ReadStringTable(byte[] str)
    {
        ushort count = BitConverter.ToUInt16(str, 0);
        var strings = new string[count + 1];
        strings[0] = "";
        for (int i = 1; i <= count; i++)
        {
            ushort offset = BitConverter.ToUInt16(str, i * 2);
            if (offset == 0 || offset >= str.Length)
            {
                strings[i] = "";
                continue;
            }

            int end = offset;
            while (end < str.Length && str[end] != 0)
            {
                end++;
            }

            strings[i] = Encoding.Default.GetString(str, offset, end - offset);
        }

        return strings;
    }

    private static string[] ReadStringTableTolerant(byte[] str)
    {
        if (IsValidStringTable(str))
        {
            return ReadStringTable(str);
        }

        ushort declaredCount = BitConverter.ToUInt16(str, 0);
        int maxAddressableId = Math.Max(0, (str.Length / 2) - 1);
        int count = Math.Min(declaredCount, maxAddressableId);
        var strings = new string[count + 1];
        strings[0] = "";

        for (int i = 1; i <= count; i++)
        {
            int offsetPosition = i * 2;
            if (offsetPosition + 1 >= str.Length)
            {
                strings[i] = "";
                continue;
            }

            ushort offset = BitConverter.ToUInt16(str, offsetPosition);
            if (offset == 0 || offset >= str.Length || str[offset] == 0)
            {
                strings[i] = "";
                continue;
            }

            int end = offset;
            while (end < str.Length && str[end] != 0)
            {
                end++;
            }

            strings[i] = Encoding.Default.GetString(str, offset, end - offset);
        }

        return strings;
    }

    private static void CollectStringReferences(Dictionary<string, List<byte[]>> grouped, string[] strings, SortedSet<ushort> used)
    {
        CollectUShortRefs(grouped, "SPRP", 0, 2, strings, used);
        CollectUShortRefs(grouped, "FORC", 8, 2, 4, strings, used);
        CollectUShortRefs(grouped, "MRGN", 16, 20, strings, used);
        CollectUnitNameRefs(grouped, "UNIx", strings, used);
        CollectUnitNameRefs(grouped, "UNIS", strings, used);
        CollectPlacedUnitNameRefs(grouped, strings, used);
        CollectTriggerStringRefs(grouped, "TRIG", strings, used);
        CollectTriggerStringRefs(grouped, "MBRF", strings, used);
    }

    private static int GetSectionLength(Dictionary<string, List<byte[]>> grouped, string name)
    {
        List<byte[]> list;
        return grouped.TryGetValue(name, out list) && list.Count > 0 ? list[0].Length : 0;
    }

    private static void CollectUShortRefs(
        Dictionary<string, List<byte[]>> grouped,
        string name,
        int start,
        int stride,
        string[] strings,
        SortedSet<ushort> used)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int offset = start; offset + 1 < data.Length; offset += stride)
        {
            AddStringIfUsed(BitConverter.ToUInt16(data, offset), strings, used);
        }
    }

    private static void CollectUShortRefs(
        Dictionary<string, List<byte[]>> grouped,
        string name,
        int start,
        int stride,
        int count,
        string[] strings,
        SortedSet<ushort> used)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0, offset = start; i < count && offset + 1 < data.Length; i++, offset += stride)
        {
            AddStringIfUsed(BitConverter.ToUInt16(data, offset), strings, used);
        }
    }

    private static void CollectPlacedUnitNameRefs(Dictionary<string, List<byte[]>> grouped, string[] strings, SortedSet<ushort> used)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("UNIT", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if ((data.Length % 36) != 0)
        {
            return;
        }

        for (int offset = 0; offset + 35 < data.Length; offset += 36)
        {
            AddStringIfUsed(BitConverter.ToUInt16(data, offset + 26), strings, used);
        }
    }

    private static void CollectUnitNameRefs(Dictionary<string, List<byte[]>> grouped, string name, string[] strings, SortedSet<ushort> used)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0, offset = UnitNameStringOffset; i < UnitTypeCount && offset + 1 < data.Length; i++, offset += 2)
        {
            AddStringIfUsed(BitConverter.ToUInt16(data, offset), strings, used);
        }
    }

    private static void CollectTriggerStringRefs(Dictionary<string, List<byte[]>> grouped, string name, string[] strings, SortedSet<ushort> used)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if ((data.Length % 2400) != 0)
        {
            return;
        }

        for (int triggerOffset = 0; triggerOffset < data.Length; triggerOffset += 2400)
        {
            for (int i = 0; i < 64; i++)
            {
                int actionOffset = triggerOffset + 320 + i * 32;
                byte actionType = data[actionOffset + 26];
                if (actionType == 0)
                {
                    continue;
                }

                AddStringIfUsed(ReadUInt32AsStringId(data, actionOffset + 4), strings, used);
                AddStringIfUsed(ReadUInt32AsStringId(data, actionOffset + 8), strings, used);
            }
        }
    }

    private static ushort ReadUInt32AsStringId(byte[] data, int offset)
    {
        uint value = BitConverter.ToUInt32(data, offset);
        return value <= UInt16.MaxValue ? (ushort)value : (ushort)0;
    }

    private static void AddStringIfUsed(ushort id, string[] strings, SortedSet<ushort> used)
    {
        if (id == 0 || id >= strings.Length || string.IsNullOrEmpty(strings[id]))
        {
            return;
        }

        used.Add(id);
    }

    private static void RemapStringReferences(Dictionary<string, List<byte[]>> grouped, Dictionary<ushort, ushort> remap)
    {
        RemapUShortRefs(grouped, "SPRP", 0, 2, remap);
        RemapUShortRefs(grouped, "FORC", 8, 2, 4, remap);
        RemapUShortRefs(grouped, "MRGN", 16, 20, remap);
        RemapUnitNameRefs(grouped, "UNIx", remap);
        RemapUnitNameRefs(grouped, "UNIS", remap);
        RemapPlacedUnitNameRefs(grouped, remap);
        RemapTriggerStringRefs(grouped, "TRIG", remap);
        RemapTriggerStringRefs(grouped, "MBRF", remap);
    }

    private static void RemapUShortRefs(Dictionary<string, List<byte[]>> grouped, string name, int start, int stride, Dictionary<ushort, ushort> remap)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int offset = start; offset + 1 < data.Length; offset += stride)
        {
            WriteUInt16(data, offset, RemapStringId(BitConverter.ToUInt16(data, offset), remap));
        }
    }

    private static void RemapUShortRefs(Dictionary<string, List<byte[]>> grouped, string name, int start, int stride, int count, Dictionary<ushort, ushort> remap)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0, offset = start; i < count && offset + 1 < data.Length; i++, offset += stride)
        {
            WriteUInt16(data, offset, RemapStringId(BitConverter.ToUInt16(data, offset), remap));
        }
    }

    private static void RemapPlacedUnitNameRefs(Dictionary<string, List<byte[]>> grouped, Dictionary<ushort, ushort> remap)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("UNIT", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if ((data.Length % 36) != 0)
        {
            return;
        }

        for (int offset = 0; offset + 35 < data.Length; offset += 36)
        {
            WriteUInt16(data, offset + 26, RemapStringId(BitConverter.ToUInt16(data, offset + 26), remap));
        }
    }

    private static void RemapUnitNameRefs(Dictionary<string, List<byte[]>> grouped, string name, Dictionary<ushort, ushort> remap)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0, offset = UnitNameStringOffset; i < UnitTypeCount && offset + 1 < data.Length; i++, offset += 2)
        {
            WriteUInt16(data, offset, RemapStringId(BitConverter.ToUInt16(data, offset), remap));
        }
    }

    private static void RemapTriggerStringRefs(Dictionary<string, List<byte[]>> grouped, string name, Dictionary<ushort, ushort> remap)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if ((data.Length % 2400) != 0)
        {
            return;
        }

        for (int triggerOffset = 0; triggerOffset < data.Length; triggerOffset += 2400)
        {
            for (int i = 0; i < 64; i++)
            {
                int actionOffset = triggerOffset + 320 + i * 32;
                byte actionType = data[actionOffset + 26];
                if (actionType == 0)
                {
                    continue;
                }

                RemapUInt32StringRef(data, actionOffset + 4, remap);
                RemapUInt32StringRef(data, actionOffset + 8, remap);
            }
        }
    }

    private static void RemapUInt32StringRef(byte[] data, int offset, Dictionary<ushort, ushort> remap)
    {
        uint value = BitConverter.ToUInt32(data, offset);
        ushort mapped = value <= UInt16.MaxValue ? RemapStringId((ushort)value, remap) : (ushort)0;
        byte[] bytes = BitConverter.GetBytes((uint)mapped);
        Buffer.BlockCopy(bytes, 0, data, offset, 4);
    }

    private static ushort RemapStringId(ushort oldId, Dictionary<ushort, ushort> remap)
    {
        ushort mapped;
        return oldId != 0 && remap.TryGetValue(oldId, out mapped) ? mapped : (ushort)0;
    }

    private static byte[] BuildStringTable(string[] strings)
    {
        return BuildStringTable((IList<string>)strings, strings.Length);
    }

    private static byte[] BuildStringTable(IList<string> strings, int minimumCount)
    {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.Default))
        {
            int count = Math.Max(minimumCount, strings.Count);
            if (count > UInt16.MaxValue)
            {
                count = UInt16.MaxValue;
            }

            writer.Write((ushort)count);
            int offset = 2 + count * 2;
            for (int i = 0; i < count; i++)
            {
                string value = i < strings.Count ? strings[i] ?? "" : "";
                writer.Write((ushort)offset);
                offset += Encoding.Default.GetByteCount(value) + 1;
            }

            for (int i = 0; i < count; i++)
            {
                string value = i < strings.Count ? strings[i] ?? "" : "";
                writer.Write(Encoding.Default.GetBytes(value));
                writer.Write((byte)0);
            }

            return ms.ToArray();
        }
    }

    private static void NormalizeUShortSectionRefs(Dictionary<string, List<byte[]>> grouped, string name, ushort strCount, params ushort[] defaults)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int offset = 0, index = 0; offset + 1 < data.Length; offset += 2, index++)
        {
            ushort value = BitConverter.ToUInt16(data, offset);
            if (value == 0 || value <= strCount)
            {
                continue;
            }

            ushort replacement = index < defaults.Length ? defaults[index] : (ushort)0;
            WriteUInt16(data, offset, replacement);
        }
    }

    private static void NormalizeForceRefs(Dictionary<string, List<byte[]>> grouped, ushort strCount)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("FORC", out list) || list.Count == 0 || list[0].Length < 16)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0; i < 4; i++)
        {
            int offset = 8 + i * 2;
            ushort value = BitConverter.ToUInt16(data, offset);
            if (value > strCount)
            {
                WriteUInt16(data, offset, (ushort)(3 + i));
            }
        }
    }

    private static void RepairLocations(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("MRGN", out list) || list.Count == 0)
        {
            return;
        }

        ushort width;
        ushort height;
        if (!TryGetMapDimensions(grouped, out width, out height))
        {
            return;
        }

        byte[] data = list[0];
        int alignedLength = (data.Length / 20) * 20;
        if (alignedLength != data.Length)
        {
            byte[] trimmed = new byte[alignedLength];
            Buffer.BlockCopy(data, 0, trimmed, 0, alignedLength);
            list[0] = data = trimmed;
            stats.RepairedLocations++;
        }

        int maxX = width * 32;
        int maxY = height * 32;
        for (int offset = 0; offset + 19 < data.Length; offset += 20)
        {
            int left = BitConverter.ToInt32(data, offset);
            int top = BitConverter.ToInt32(data, offset + 4);
            int right = BitConverter.ToInt32(data, offset + 8);
            int bottom = BitConverter.ToInt32(data, offset + 12);
            bool empty = left == 0 && top == 0 && right == 0 && bottom == 0;
            bool valid =
                empty ||
                (left >= 0 && top >= 0 && right >= left && bottom >= top && right <= maxX && bottom <= maxY);

            if (!valid)
            {
                Array.Clear(data, offset, 20);
                stats.RepairedLocations++;
            }
        }
    }

    private static void NormalizeLocationRefs(Dictionary<string, List<byte[]>> grouped, ushort strCount)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("MRGN", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int offset = 16; offset + 1 < data.Length; offset += 20)
        {
            ushort value = BitConverter.ToUInt16(data, offset);
            if (value > strCount)
            {
                WriteUInt16(data, offset, 0);
            }
        }
    }

    private static void NormalizeUnitNameRefs(Dictionary<string, List<byte[]>> grouped, ushort strCount)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("UNIx", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0, offset = UnitNameStringOffset; i < UnitTypeCount && offset + 1 < data.Length; i++, offset += 2)
        {
            ushort value = BitConverter.ToUInt16(data, offset);
            if (value > strCount)
            {
                WriteUInt16(data, offset, 0);
            }
        }
    }

    private static void NormalizePlacedUnitNameRefs(Dictionary<string, List<byte[]>> grouped, ushort strCount)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("UNIT", out list) || list.Count == 0)
        {
            return;
        }

        byte[] data = list[0];
        if (data.Length % 36 != 0)
        {
            return;
        }

        for (int offset = 0; offset < data.Length; offset += 36)
        {
            ushort nameId = BitConverter.ToUInt16(data, offset + 26);
            if (nameId > strCount)
            {
                WriteUInt16(data, offset + 26, 0);
            }
        }
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        data[offset] = bytes[0];
        data[offset + 1] = bytes[1];
    }

    private static void NormalizeTriggerActions(byte[] data, int triggerOffset, ushort strCount, Stats stats)
    {
        for (int i = 0; i < 64; i++)
        {
            int actionOffset = triggerOffset + 320 + i * 32;
            byte actionType = data[actionOffset + 26];
            if (actionType == 0)
            {
                continue;
            }

            if (actionType == 47)
            {
                Array.Clear(data, actionOffset, 32);
                stats.RemovedTriggerComments++;
                continue;
            }

            NormalizeLocationId(data, actionOffset, stats);
            if (ActionHasSecondLocation(actionType))
            {
                NormalizeLocationId(data, actionOffset + 20, stats);
            }

            NormalizeStrictStringId(data, actionOffset + 4, strCount, stats);
            NormalizeStrictStringId(data, actionOffset + 8, strCount, stats);
            NormalizeActionStringFields(data, actionOffset, actionType, strCount, stats);
        }
    }

    private static void NormalizeTriggerConditionLocations(byte[] data, int triggerOffset, Stats stats)
    {
        for (int i = 0; i < 16; i++)
        {
            int conditionOffset = triggerOffset + i * 20;
            byte conditionType = data[conditionOffset + 15];
            if (conditionType > 23)
            {
                Array.Clear(data, conditionOffset, 20);
                stats.RemovedFakeTriggers++;
                continue;
            }

            if (conditionType != 0)
            {
                NormalizeLocationId(data, conditionOffset, stats);
            }
        }
    }

    private static bool ActionHasSecondLocation(byte actionType)
    {
        return actionType == 38 || actionType == 39 || actionType == 46;
    }

    private static void NormalizeLocationId(byte[] data, int offset, Stats stats)
    {
        uint value = BitConverter.ToUInt32(data, offset);
        if (value > 255)
        {
            data[offset] = 0;
            data[offset + 1] = 0;
            data[offset + 2] = 0;
            data[offset + 3] = 0;
            stats.NormalizedTriggerLocations++;
        }
    }

    private static void NormalizeActionStringFields(byte[] data, int actionOffset, byte actionType, ushort strCount, Stats stats)
    {
        switch (actionType)
        {
            case 7:  // Transmission: text and WAV path strings.
                NormalizeStrictStringId(data, actionOffset + 4, strCount, stats);
                NormalizeStrictStringId(data, actionOffset + 8, strCount, stats);
                break;

            case 8:  // Play WAV.
                NormalizeStrictStringId(data, actionOffset + 8, strCount, stats);
                break;

            case 9:  // Display text.
            case 12: // Mission objectives.
            case 17: // Leaderboard label variants.
            case 18:
            case 19:
            case 20:
            case 33:
            case 34:
            case 35:
            case 36:
            case 37:
            case 40:
            case 41: // Set next scenario.
                NormalizeStrictStringId(data, actionOffset + 4, strCount, stats);
                break;
        }
    }

    private static void NormalizeStrictStringId(byte[] data, int offset, ushort strCount, Stats stats)
    {
        uint value = BitConverter.ToUInt32(data, offset);
        if (value != 0 && (strCount == 0 || value > strCount))
        {
            data[offset] = 0;
            data[offset + 1] = 0;
            data[offset + 2] = 0;
            data[offset + 3] = 0;
            stats.NormalizedTriggerStrings++;
        }
    }

    private static void AddDefaultSections(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        AddIfMissing(grouped, "TYPE", Encoding.ASCII.GetBytes("RAWB"), stats);

        List<byte[]> ownr;
        if (!grouped.ContainsKey("IOWN") && grouped.TryGetValue("OWNR", out ownr) && ownr.Count > 0 && ownr[0].Length == 12)
        {
            byte[] iown = (byte[])ownr[0].Clone();
            for (int i = 0; i < iown.Length; i++)
            {
                if (iown[i] == 0xFF)
                {
                    iown[i] = 0x00;
                }
            }

            AddIfMissing(grouped, "IOWN", iown, stats);
        }

        AddIfMissing(grouped, "IVE2", new byte[] { 0x0B, 0x00 }, stats);
        RepairTerrainSections(grouped, stats);
        AddIfMissing(grouped, "WAV ", new byte[2048], stats);
        AddIfMissing(grouped, "SWNM", new byte[1024], stats);
    }

    private static void NormalizeCoreMapSections(Dictionary<string, List<byte[]>> grouped)
    {
        NormalizeEra(grouped);
        NormalizePlayerBytes(grouped, "OWNR", 0);
        NormalizePlayerBytes(grouped, "IOWN", 0);
        NormalizePlayerBytes(grouped, "SIDE", 7);
    }

    private static void NormalizeEra(Dictionary<string, List<byte[]>> grouped)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("ERA ", out list) || list.Count == 0 || list[0].Length != 2)
        {
            return;
        }

        ushort era = BitConverter.ToUInt16(list[0], 0);
        if (era > 7)
        {
            WriteUInt16(list[0], 0, (ushort)(era % 8));
        }
    }

    private static void NormalizePlayerBytes(Dictionary<string, List<byte[]>> grouped, string name, byte replacement)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0 || list[0].Length != 12)
        {
            return;
        }

        byte[] data = list[0];
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0xFF)
            {
                data[i] = replacement;
            }
        }
    }

    private static void RepairTerrainSections(Dictionary<string, List<byte[]>> grouped, Stats stats)
    {
        ushort width;
        ushort height;
        if (!TryGetMapDimensions(grouped, out width, out height))
        {
            return;
        }

        int tileCount = width * height;
        int tileBytes = tileCount * 2;
        int isomBytes = ((width / 2) + 1) * (height + 1) * 8;

        byte[] mtxm = ChooseLastValidMtxm(grouped, tileBytes, stats);
        byte[] tile = ChooseBestTerrainGrid(grouped, "TILE", tileBytes, stats);

        if (mtxm != null && tile != null)
        {
            stats.TileMtxmMatchRate = GetUShortGridMatchRate(mtxm, tile);
            if (IsLowInformationUShortGrid(tile) && !IsLowInformationUShortGrid(mtxm))
            {
                tile = (byte[])mtxm.Clone();
                stats.TileMtxmMatchRate = 100;
            }
            else if (IsLowInformationUShortGrid(mtxm) && !IsLowInformationUShortGrid(tile))
            {
                mtxm = (byte[])tile.Clone();
                stats.TileMtxmMatchRate = 100;
            }
        }
        else if (mtxm != null)
        {
            tile = (byte[])mtxm.Clone();
            stats.TileMtxmMatchRate = 100;
        }
        else if (tile != null)
        {
            mtxm = (byte[])tile.Clone();
            stats.TileMtxmMatchRate = 100;
        }
        else
        {
            mtxm = new byte[tileBytes];
            tile = new byte[tileBytes];
            stats.TileMtxmMatchRate = 100;
        }

        if (mtxm != null)
        {
            SetSingleTerrainSection(grouped, "MTXM", mtxm, stats);
        }

        if (tile != null)
        {
            SetSingleTerrainSection(grouped, "TILE", tile, stats);
        }

        byte[] mask = ChooseBestMask(grouped, tileCount, stats);
        if (mask == null)
        {
            mask = new byte[tileCount];
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = 0xFF;
            }
        }

        SetSingleTerrainSection(grouped, "MASK", mask, stats);
        byte[] dd2 = ChooseValidDoodadSection(grouped, "DD2 ", 8, width, height, stats);
        byte[] thg2 = ChooseValidThingySection(grouped, width, height, stats);
        SetSingleTerrainSection(grouped, "DD2 ", dd2 ?? new byte[0], stats);
        SetSingleTerrainSection(grouped, "THG2", thg2 ?? new byte[0], stats);

        byte[] isom = BuildDefaultIsom(isomBytes);
        stats.IsomGenerated++;
        stats.IsomConfidence = 100;
        stats.IsomRepairMode = "open-safe default terrain metadata";
        SetSingleTerrainSection(grouped, "ISOM", isom, stats);
    }

    private static byte[] BuildDefaultIsom(int isomBytes)
    {
        byte[] isom = new byte[isomBytes];
        for (int offset = 0; offset + 1 < isom.Length; offset += 2)
        {
            WriteUInt16(isom, offset, 0x0010);
        }

        return isom;
    }

    private static byte[] ChooseValidDoodadSection(
        Dictionary<string, List<byte[]>> grouped,
        string name,
        int recordSize,
        ushort width,
        ushort height,
        Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return null;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            byte[] data = list[i];
            stats.TerrainCandidatesScanned++;
            if (IsValidDoodadSection(data, recordSize, width, height))
            {
                return (byte[])data.Clone();
            }
        }

        return null;
    }

    private static bool IsValidDoodadSection(byte[] data, int recordSize, ushort width, ushort height)
    {
        if (data == null || data.Length == 0)
        {
            return data != null;
        }

        if (recordSize <= 0 || (data.Length % recordSize) != 0)
        {
            return false;
        }

        int maxX = width * 32;
        int maxY = height * 32;
        for (int offset = 0; offset + recordSize - 1 < data.Length; offset += recordSize)
        {
            ushort type = BitConverter.ToUInt16(data, offset);
            ushort x = BitConverter.ToUInt16(data, offset + 2);
            ushort y = BitConverter.ToUInt16(data, offset + 4);
            if (type > 4095 || x > maxX || y > maxY)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] ChooseValidThingySection(Dictionary<string, List<byte[]>> grouped, ushort width, ushort height, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("THG2", out list) || list.Count == 0)
        {
            return null;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            byte[] data = list[i];
            stats.TerrainCandidatesScanned++;
            if (IsValidThingySection(data, width, height))
            {
                return (byte[])data.Clone();
            }
        }

        return null;
    }

    private static bool IsValidThingySection(byte[] data, ushort width, ushort height)
    {
        if (data == null || data.Length == 0)
        {
            return data != null;
        }

        return (data.Length % 10) == 0;
    }

    private static byte[] ChooseLastValidMtxm(Dictionary<string, List<byte[]>> grouped, int expectedBytes, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("MTXM", out list) || list.Count == 0)
        {
            stats.MtxmSelection = "missing";
            return null;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            byte[] data = list[i];
            stats.TerrainCandidatesScanned++;
            if (data != null && data.Length == expectedBytes)
            {
                stats.MtxmSelection = "last valid";
                return (byte[])data.Clone();
            }

            byte[] repaired = RepairNearSizedTerrainGrid(data, expectedBytes);
            if (repaired != null)
            {
                stats.MtxmSelection = "near-size repaired";
                return repaired;
            }
        }

        stats.MtxmSelection = "no valid candidate";
        return null;
    }

    private static byte[] ChooseBestTerrainGrid(Dictionary<string, List<byte[]>> grouped, string name, int expectedBytes, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            return null;
        }

        TerrainChoice best = null;
        for (int i = 0; i < list.Count; i++)
        {
            byte[] data = list[i];
            stats.TerrainCandidatesScanned++;
            if (data == null || data.Length != expectedBytes)
            {
                data = RepairNearSizedTerrainGrid(data, expectedBytes);
                if (data == null)
                {
                    continue;
                }
            }

            int score = 100000 + ScoreUShortGridInformation(data);
            if (IsLowInformationUShortGrid(data))
            {
                score -= 50000;
            }

            if (best == null || score > best.Score)
            {
                best = new TerrainChoice { Data = data, Score = score, Index = i };
            }
        }

        return best == null ? null : (byte[])best.Data.Clone();
    }

    private static byte[] RepairNearSizedTerrainGrid(byte[] data, int expectedBytes)
    {
        if (data == null || data.Length <= 0)
        {
            return null;
        }

        int delta = expectedBytes - data.Length;
        if (delta < 0 || delta > 4096 || (data.Length % 2) != 0)
        {
            return null;
        }

        byte[] repaired = new byte[expectedBytes];
        Buffer.BlockCopy(data, 0, repaired, 0, data.Length);
        return repaired;
    }

    private static byte[] ChooseBestMask(Dictionary<string, List<byte[]>> grouped, int expectedBytes, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("MASK", out list) || list.Count == 0)
        {
            return null;
        }

        TerrainChoice best = null;
        for (int i = 0; i < list.Count; i++)
        {
            byte[] data = list[i];
            stats.TerrainCandidatesScanned++;
            if (data == null || data.Length != expectedBytes)
            {
                continue;
            }

            int score = 100000 + ScoreByteInformation(data);
            if (best == null || score > best.Score)
            {
                best = new TerrainChoice { Data = data, Score = score, Index = i };
            }
        }

        return best == null ? null : (byte[])best.Data.Clone();
    }

    private static byte[] ChooseBestIsom(Dictionary<string, List<byte[]>> grouped, int expectedBytes, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue("ISOM", out list) || list.Count == 0)
        {
            return null;
        }

        TerrainChoice best = null;
        for (int i = 0; i < list.Count; i++)
        {
            byte[] data = list[i];
            stats.TerrainCandidatesScanned++;
            if (data == null || data.Length != expectedBytes)
            {
                continue;
            }

            int score = 100000 + ScoreUShortGridInformation(data) + ScoreIsomContinuity(data);
            if (IsLowInformationUShortGrid(data))
            {
                score -= 50000;
            }

            if (best == null || score > best.Score)
            {
                best = new TerrainChoice { Data = data, Score = score, Index = i };
            }
        }

        return best == null ? null : (byte[])best.Data.Clone();
    }

    private static bool IsExactDefaultIsom(byte[] data)
    {
        if (data == null || data.Length < 2 || (data.Length % 2) != 0)
        {
            return false;
        }

        for (int i = 0; i < data.Length; i += 2)
        {
            if (BitConverter.ToUInt16(data, i) != 0x0010)
            {
                return false;
            }
        }

        return true;
    }

    private static void SetSingleTerrainSection(Dictionary<string, List<byte[]>> grouped, string name, byte[] data, Stats stats)
    {
        List<byte[]> list;
        bool existed = grouped.TryGetValue(name, out list) && list.Count > 0;
        bool same = existed && list.Count == 1 && ByteArraysEqual(list[0], data);

        grouped[name] = new List<byte[]> { data };
        if (!existed)
        {
            stats.AddedDefaultSections++;
            stats.TerrainSectionsRepaired++;
        }
        else if (!same)
        {
            stats.TerrainSectionsRepaired++;
        }
    }

    private static byte[] GenerateApproximateIsom(byte[] grid, ushort width, ushort height, out int confidence)
    {
        int nodeWidth = (width / 2) + 1;
        int nodeHeight = height + 1;
        byte[] isom = new byte[nodeWidth * nodeHeight * 8];
        int stableNodes = 0;
        int informedNodes = 0;

        for (int y = 0; y < nodeHeight; y++)
        {
            for (int x = 0; x < nodeWidth; x++)
            {
                int spread;
                int rightSpread;
                int downSpread;
                ushort center = DominantTileGroup(grid, width, height, x * 2 - 1, y - 1, out spread);
                ushort right = DominantTileGroup(grid, width, height, x * 2 + 1, y - 1, out rightSpread);
                ushort down = DominantTileGroup(grid, width, height, x * 2 - 1, y, out downSpread);
                ushort mixed = (ushort)((center + right + down) / 3);

                int p = (y * nodeWidth + x) * 8;
                WriteUInt16(isom, p, EncodeIsomGuess(center, spread));
                WriteUInt16(isom, p + 2, EncodeIsomGuess(right, rightSpread));
                WriteUInt16(isom, p + 4, EncodeIsomGuess(down, downSpread));
                WriteUInt16(isom, p + 6, EncodeIsomGuess(mixed, Math.Max(spread, Math.Max(rightSpread, downSpread))));

                if (center != 0 || right != 0 || down != 0)
                {
                    informedNodes++;
                }

                if (spread <= 1 && rightSpread <= 1 && downSpread <= 1)
                {
                    stableNodes++;
                }
            }
        }

        int totalNodes = Math.Max(1, nodeWidth * nodeHeight);
        confidence = 35 + (stableNodes * 35 / totalNodes) + (informedNodes * 20 / totalNodes);
        if (confidence > 90)
        {
            confidence = 90;
        }

        return isom;
    }

    private static ushort DominantTileGroup(byte[] grid, int width, int height, int x, int y, out int spread)
    {
        var counts = new Dictionary<ushort, int>();
        int samples = 0;
        for (int dy = 0; dy < 2; dy++)
        {
            int yy = Clamp(y + dy, 0, height - 1);
            for (int dx = 0; dx < 4; dx++)
            {
                int xx = Clamp(x + dx, 0, width - 1);
                int pos = (yy * width + xx) * 2;
                ushort group = NormalizeTileGroup(BitConverter.ToUInt16(grid, pos));
                int count;
                counts.TryGetValue(group, out count);
                counts[group] = count + 1;
                samples++;
            }
        }

        ushort best = 0;
        int bestCount = -1;
        foreach (var pair in counts)
        {
            if (pair.Value > bestCount)
            {
                best = pair.Key;
                bestCount = pair.Value;
            }
        }

        spread = samples - bestCount;
        return best;
    }

    private static ushort NormalizeTileGroup(ushort tile)
    {
        return (ushort)(((tile & 0x0FFF) / 16) & 0x01FF);
    }

    private static ushort EncodeIsomGuess(ushort tileGroup, int spread)
    {
        int value = 0x10 + ((tileGroup & 0x00FF) * 2);
        if (spread > 0)
        {
            value += Math.Min(7, spread);
        }

        return (ushort)value;
    }

    private static int ScoreUShortGridInformation(byte[] data)
    {
        var seen = new HashSet<ushort>();
        int transitions = 0;
        int previous = -1;
        int records = data.Length / 2;
        int step = Math.Max(1, records / 4096);
        for (int i = 0; i < records; i += step)
        {
            ushort value = BitConverter.ToUInt16(data, i * 2);
            seen.Add(value);
            if (previous >= 0 && previous != value)
            {
                transitions++;
            }

            previous = value;
        }

        return Math.Min(20000, seen.Count * 200 + transitions);
    }

    private static int ScoreByteInformation(byte[] data)
    {
        var seen = new HashSet<byte>();
        int transitions = 0;
        int previous = -1;
        int step = Math.Max(1, data.Length / 4096);
        for (int i = 0; i < data.Length; i += step)
        {
            byte value = data[i];
            seen.Add(value);
            if (previous >= 0 && previous != value)
            {
                transitions++;
            }

            previous = value;
        }

        return Math.Min(20000, seen.Count * 200 + transitions);
    }

    private static int ScoreIsomContinuity(byte[] data)
    {
        int records = data.Length / 2;
        int score = 0;
        int step = Math.Max(1, records / 4096);
        for (int i = step; i < records; i += step)
        {
            int previous = BitConverter.ToUInt16(data, (i - step) * 2);
            int current = BitConverter.ToUInt16(data, i * 2);
            int delta = Math.Abs(current - previous);
            if (delta == 0)
            {
                score += 2;
            }
            else if (delta < 32)
            {
                score++;
            }
        }

        return Math.Min(10000, score);
    }

    private static bool IsLowInformationUShortGrid(byte[] data)
    {
        if (data == null || data.Length < 2 || (data.Length % 2) != 0)
        {
            return true;
        }

        var counts = new Dictionary<ushort, int>();
        int records = data.Length / 2;
        int step = Math.Max(1, records / 4096);
        int sampled = 0;
        for (int i = 0; i < records; i += step)
        {
            ushort value = BitConverter.ToUInt16(data, i * 2);
            int count;
            counts.TryGetValue(value, out count);
            counts[value] = count + 1;
            sampled++;
        }

        int max = counts.Values.Count == 0 ? 0 : counts.Values.Max();
        return counts.Count <= 1 || (counts.Count <= 2 && max * 100 / Math.Max(1, sampled) >= 98);
    }

    private static int GetUShortGridMatchRate(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length || (left.Length % 2) != 0)
        {
            return 0;
        }

        int records = left.Length / 2;
        if (records == 0)
        {
            return 100;
        }

        int matches = 0;
        for (int i = 0; i < records; i++)
        {
            if (BitConverter.ToUInt16(left, i * 2) == BitConverter.ToUInt16(right, i * 2))
            {
                matches++;
            }
        }

        return matches * 100 / records;
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static bool TryGetMapDimensions(Dictionary<string, List<byte[]>> grouped, out ushort width, out ushort height)
    {
        width = 0;
        height = 0;

        List<byte[]> list;
        if (!grouped.TryGetValue("DIM ", out list) || list.Count == 0 || list[0].Length != 4)
        {
            return false;
        }

        width = BitConverter.ToUInt16(list[0], 0);
        height = BitConverter.ToUInt16(list[0], 2);
        return IsPlausibleMapDimension(width) && IsPlausibleMapDimension(height);
    }

    private static void AddIfMissing(Dictionary<string, List<byte[]>> grouped, string name, byte[] data, Stats stats)
    {
        if (grouped.ContainsKey(name))
        {
            return;
        }

        grouped[name] = new List<byte[]> { data };
        stats.AddedDefaultSections++;
    }

    private static void WriteStandardMpq(string output, byte[] chk, List<MpqFileEntry> extraFiles)
    {
        string dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(output))
        {
            File.Delete(output);
        }

        using (var mpq = new TkMPQ(1024, 3))
        {
            foreach (MpqFileEntry entry in extraFiles)
            {
                mpq.WriteFile(
                    entry.Name,
                    entry.Data,
                    Flags.Exists | Flags.Compressed,
                    Compression.None,
                    Locale.Neutral);
            }

            mpq.WriteFile(
                "staredit\\scenario.chk",
                chk,
                Flags.Exists | Flags.Compressed,
                Compression.Implode,
                Locale.Neutral);

            mpq.SaveMPQ(output);
        }
    }
}
