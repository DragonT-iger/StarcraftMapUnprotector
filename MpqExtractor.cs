using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
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
}
