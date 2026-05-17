using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

        string text = Encoding.GetEncoding(949).GetString(listfile);
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
            List<int> hashCandidates = FindHashTableByPattern(file, header.BaseOffset + 32, header.HashCount);
            AddFixedHashCandidates(hashCandidates, file.Length, header.BaseOffset, header.HashTableOffset, header.HashCount);
            hashCandidates = hashCandidates.Distinct().ToList();

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
                List<int> blockCandidates = BuildAdjacentBlockCandidates(file, hashOffset, header.HashCount, header.BlockTableOffset, header.BlockCount, header.BaseOffset);

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

                    data = TryRecoverEncryptedScenarioChkByAlternateKeys(file, header, hashes, blocks, out extraFiles);
                    if (data != null)
                    {
                        stats.MpqDeepRecoveryUsed++;
                        stats.MpqDeepRecoveryDetail =
                            "alternate encrypted CHK recovery succeeded" +
                            ", headers=" + headers.Count +
                            ", tableCandidates=" + stats.MpqDeepTableCandidatesTried +
                            ", headerBase=0x" + header.BaseOffset.ToString("X") +
                            ", hash=0x" + hashOffset.ToString("X") +
                            ", block=0x" + blockOffset.ToString("X");
                        return data;
                    }

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

        byte[] rawChk = TryScanRawChk(file);
        if (rawChk != null)
        {
            stats.MpqDeepRecoveryUsed++;
            stats.MpqDeepRecoveryDetail = "raw CHK scan succeeded";
            return rawChk;
        }

        stats.MpqDeepRecoveryDetail =
            "deep recovery failed: " + reason +
            ", headers=" + headers.Count +
            ", tableCandidates=" + stats.MpqDeepTableCandidatesTried;
        return null;
    }

    private static byte[] TryRecoverEncryptedScenarioChkByAlternateKeys(
        byte[] file,
        MpqHeaderCandidate header,
        HashTable[] hashes,
        BlockTable[] blocks,
        out List<MpqFileEntry> extraFiles)
    {
        extraFiles = new List<MpqFileEntry>();

        int scenarioBlock = FindUsableScenarioBlock(hashes, blocks);
        if (scenarioBlock < 0)
        {
            return null;
        }

        BlockTable block = blocks[scenarioBlock];
        int archiveLength = file.Length - header.BaseOffset;
        if (!IsBlockInArchive(block, archiveLength))
        {
            return null;
        }

        int blockOffset = header.BaseOffset + block.FileOffset;
        int compressedSize = (int)block.CompSize;
        byte[] encrypted = new byte[compressedSize];
        Buffer.BlockCopy(file, blockOffset, encrypted, 0, encrypted.Length);

        int sectorSize = GetMpqSectorSize(file, header.BaseOffset);
        int expectedSize = block.FileSize > Int32.MaxValue ? 0 : (int)block.FileSize;
        string[] keyNames =
        {
            "staredit\\scenario.chk",
            "scenario.chk",
            "staredit/scenario.chk"
        };

        foreach (string keyName in keyNames)
        {
            uint key = Encryption.HashString(keyName, Encryption.HashType.Hash_FileKey);
            byte[] data = TryDecryptAndDecompressMpqFile(encrypted, key, expectedSize, sectorSize);
            data = TryCoerceRecoveredChk(data);
            if (data != null)
            {
                return data;
            }

            if ((((uint)block.Flags & (uint)Flags.ModKey) != 0) && expectedSize > 0)
            {
                uint fixedKey = (key + (uint)block.FileOffset) ^ (uint)expectedSize;
                data = TryDecryptAndDecompressMpqFile(encrypted, fixedKey, expectedSize, sectorSize);
                data = TryCoerceRecoveredChk(data);
                if (data != null)
                {
                    return data;
                }
            }
        }

        return null;
    }

    private static byte[] TryCoerceRecoveredChk(byte[] data)
    {
        if (data == null)
        {
            return null;
        }

        if (LooksLikeChk(data) && ParseChk(data).Count > 0)
        {
            return data;
        }

        byte[] rebuilt = TryRebuildLooseChkFromBuffer(data);
        return rebuilt != null && ParseChk(rebuilt).Count > 0 ? rebuilt : null;
    }

    private static int GetMpqSectorSize(byte[] file, int headerBase)
    {
        if (headerBase + 16 > file.Length)
        {
            return 4096;
        }

        ushort shift = BitConverter.ToUInt16(file, headerBase + 14);
        if (shift > 24)
        {
            return 4096;
        }

        long size = 512L << shift;
        return size > Int32.MaxValue ? Int32.MaxValue : (int)size;
    }

    private static byte[] TryDecryptAndDecompressMpqFile(byte[] encrypted, uint key, int expectedSize, int sectorSize)
    {
        if (encrypted == null || encrypted.Length < 4)
        {
            return null;
        }

        byte[] offsetTable = (byte[])encrypted.Clone();
        DecryptMpqData(offsetTable, key - 1);

        int sectorCount = expectedSize > 0 && sectorSize > 0
            ? Math.Max(1, (expectedSize + sectorSize - 1) / sectorSize)
            : 1;
        int tableEntries = sectorCount + 1;
        int tableBytes = tableEntries * 4;
        if (tableBytes > encrypted.Length)
        {
            return null;
        }

        var offsets = new int[tableEntries];
        bool validOffsets = true;
        for (int i = 0; i < tableEntries; i++)
        {
            uint offset = BitConverter.ToUInt32(offsetTable, i * 4);
            if (offset > encrypted.Length || (i > 0 && offset < offsets[i - 1]))
            {
                validOffsets = false;
                break;
            }

            offsets[i] = (int)offset;
        }

        if (!validOffsets || offsets[0] < tableBytes || offsets[tableEntries - 1] > encrypted.Length)
        {
            return TryDecryptAndDecompressSingleSector(encrypted, key);
        }

        try
        {
            using (var output = new MemoryStream(expectedSize > 0 ? expectedSize : 0))
            {
                for (int i = 0; i < sectorCount; i++)
                {
                    int start = offsets[i];
                    int end = offsets[i + 1];
                    if (end <= start)
                    {
                        return null;
                    }

                    byte[] sector = new byte[end - start];
                    Buffer.BlockCopy(encrypted, start, sector, 0, sector.Length);
                    DecryptMpqData(sector, key + (uint)i);
                    byte[] decompressed = DecompressMpqSector(sector);
                    if (decompressed == null)
                    {
                        return null;
                    }

                    output.Write(decompressed, 0, decompressed.Length);
                }

                return output.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

    private static byte[] TryDecryptAndDecompressSingleSector(byte[] encrypted, uint key)
    {
        byte[] sector = (byte[])encrypted.Clone();
        DecryptMpqData(sector, key);
        return DecompressMpqSector(sector);
    }

    private static byte[] TryRebuildLooseChkFromBuffer(byte[] data)
    {
        if (data == null || data.Length < 8)
        {
            return null;
        }

        var rebuilt = new List<Section>();
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            int end;
            List<Section> sequence = TryReadLooseSectionSequence(data, pos, out end);
            if (sequence != null && sequence.Count >= 3)
            {
                foreach (Section section in sequence)
                {
                    rebuilt.Add(section);
                }

                pos = Math.Max(pos + 1, end);
                continue;
            }

            pos++;
        }

        if (rebuilt.Count < 3)
        {
            return null;
        }

        AddMissingFixedLooseSections(data, rebuilt);

        bool hasCore =
            rebuilt.Any(section => section.Name == "VER ") ||
            rebuilt.Any(section => section.Name == "DIM ") ||
            rebuilt.Any(section => section.Name == "TRIG") ||
            rebuilt.Any(section => section.Name == "MTXM");
        if (!hasCore)
        {
            return null;
        }

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms, Encoding.ASCII))
        {
            foreach (Section section in rebuilt)
            {
                writer.Write(Encoding.ASCII.GetBytes(section.Name));
                writer.Write(section.Data.Length);
                writer.Write(section.Data);
            }

            return ms.ToArray();
        }
    }

    private static void AddMissingFixedLooseSections(byte[] data, List<Section> sections)
    {
        string[] names =
        {
            "VER ", "TYPE", "IVE2", "VCOD", "IOWN", "OWNR", "SIDE", "COLR",
            "ERA ", "DIM ", "SPRP", "FORC", "UPRP", "UPUS", "WAV ", "SWNM"
        };

        foreach (string name in names)
        {
            if (sections.Any(existing => existing.Name == name))
            {
                continue;
            }

            Section found = FindFirstLooseSection(data, name);
            if (found != null)
            {
                sections.Add(found);
            }
        }
    }

    private static Section FindFirstLooseSection(byte[] data, string expectedName)
    {
        for (int pos = 0; pos + 8 <= data.Length; pos++)
        {
            string name = Encoding.ASCII.GetString(data, pos, 4);
            if (name != expectedName)
            {
                continue;
            }

            uint size32 = BitConverter.ToUInt32(data, pos + 4);
            if (size32 > Int32.MaxValue || pos + 8L + size32 > data.Length)
            {
                continue;
            }

            int size = (int)size32;
            if (!IsPlausibleLooseSectionSize(name, size))
            {
                continue;
            }

            byte[] sectionData = new byte[size];
            Buffer.BlockCopy(data, pos + 8, sectionData, 0, size);
            return new Section(name, sectionData);
        }

        return null;
    }

    private static List<Section> TryReadLooseSectionSequence(byte[] data, int start, out int end)
    {
        end = start;
        var sections = new List<Section>();
        int pos = start;
        while (pos + 8 <= data.Length)
        {
            string name = Encoding.ASCII.GetString(data, pos, 4);
            if (!IsKnownChkSectionName(name))
            {
                break;
            }

            uint size32 = BitConverter.ToUInt32(data, pos + 4);
            if (size32 > 64 * 1024 * 1024 || pos + 8L + size32 > data.Length)
            {
                break;
            }

            int size = (int)size32;
            if (!IsPlausibleLooseSectionSize(name, size))
            {
                break;
            }

            byte[] sectionData = new byte[size];
            Buffer.BlockCopy(data, pos + 8, sectionData, 0, size);
            sections.Add(new Section(name, sectionData));
            pos += 8 + size;
        }

        end = pos;
        return sections.Count == 0 ? null : sections;
    }

    private static bool IsKnownChkSectionName(string name)
    {
        switch (name)
        {
            case "VER ":
            case "TYPE":
            case "IVE2":
            case "VCOD":
            case "IOWN":
            case "OWNR":
            case "SIDE":
            case "COLR":
            case "ERA ":
            case "DIM ":
            case "MTXM":
            case "TILE":
            case "ISOM":
            case "UNIT":
            case "PUNI":
            case "UNIx":
            case "PUPx":
            case "UPGx":
            case "DD2 ":
            case "THG2":
            case "MASK":
            case "MRGN":
            case "STR ":
            case "SPRP":
            case "FORC":
            case "WAV ":
            case "PTEx":
            case "TECx":
            case "MBRF":
            case "TRIG":
            case "UPRP":
            case "UPUS":
            case "SWNM":
                return true;
            default:
                return false;
        }
    }

    private static bool IsPlausibleLooseSectionSize(string name, int size)
    {
        if (size < 0)
        {
            return false;
        }

        switch (name)
        {
            case "VER ":
            case "IVE2":
            case "ERA ":
                return size == 2;
            case "TYPE":
            case "DIM ":
                return size == 4;
            case "IOWN":
            case "OWNR":
            case "SIDE":
            case "COLR":
                return size == 12;
            case "FORC":
                return size == 20;
            case "SPRP":
                return size == 4;
            case "UPUS":
                return size == 64;
            case "UPRP":
                return size == 1280;
            case "WAV ":
                return size == 2048;
            case "SWNM":
                return size == 1024;
            case "UNIT":
                return size % 36 == 0;
            case "MRGN":
                return size % 20 == 0;
            case "TRIG":
            case "MBRF":
                return size % 2400 == 0;
            case "DD2 ":
                return size % 8 == 0;
            case "THG2":
                return size % 10 == 0;
            default:
                return size <= 32 * 1024 * 1024;
        }
    }

    private static byte[] DecompressMpqSector(byte[] sector)
    {
        if (sector == null || sector.Length == 0)
        {
            return null;
        }

        byte compression = sector[0];
        if (compression == 0x02)
        {
            return InflateZlibSector(sector, 1);
        }

        if (compression == 0x08)
        {
            return DecompressWithTkMpq(sector);
        }

        if (compression == 0)
        {
            byte[] copy = new byte[sector.Length - 1];
            Buffer.BlockCopy(sector, 1, copy, 0, copy.Length);
            return copy;
        }

        return null;
    }

    private static byte[] DecompressWithTkMpq(byte[] sector)
    {
        try
        {
            byte[] source = (byte[])sector.Clone();
            byte[] destination = new byte[GetMaxRecoveredSectorSize(sector.Length)];
            int length = Compressions.Decompress(ref destination, ref source);
            if (length <= 0 || length > destination.Length)
            {
                return null;
            }

            byte[] result = new byte[length];
            Buffer.BlockCopy(destination, 0, result, 0, result.Length);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static int GetMaxRecoveredSectorSize(int compressedLength)
    {
        return Math.Max(4096, Math.Min(16 * 1024 * 1024, compressedLength * 256));
    }

    private static byte[] InflateZlibSector(byte[] sector, int offset)
    {
        if (sector.Length <= offset + 2)
        {
            return null;
        }

        int deflateOffset = offset;
        int deflateLength = sector.Length - offset;
        if (sector[offset] == 0x78 && offset + 2 < sector.Length)
        {
            deflateOffset += 2;
            deflateLength -= 2;
            if (deflateLength > 4)
            {
                deflateLength -= 4;
            }
        }

        if (deflateLength <= 0)
        {
            return null;
        }

        try
        {
            using (var input = new MemoryStream(sector, deflateOffset, deflateLength))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

    private static void DecryptMpqData(byte[] data, uint key)
    {
        uint seed1 = key;
        uint seed2 = 0xEEEEEEEE;

        for (int i = 0; i + 3 < data.Length; i += 4)
        {
            seed2 += CryptTable[0x400 + (seed1 & 0xFF)];
            uint value = BitConverter.ToUInt32(data, i) ^ (seed1 + seed2);
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, i, 4);
            seed1 = AdvanceSeed1(seed1);
            seed2 = value + seed2 + (seed2 << 5) + 3;
        }
    }

    private static byte[] TryScanRawChk(byte[] file)
    {
        for (int pos = 0; pos + 8 <= file.Length; pos += 4)
        {
            string name = Encoding.ASCII.GetString(file, pos, 4);
            if (!IsPlausibleSectionName(name))
            {
                continue;
            }

            uint size32 = BitConverter.ToUInt32(file, pos + 4);
            if (size32 > 64 * 1024 * 1024)
            {
                continue;
            }

            int size = (int)size32;
            if (pos + 8 + size > file.Length)
            {
                continue;
            }

            byte[] candidate = new byte[file.Length - pos];
            Buffer.BlockCopy(file, pos, candidate, 0, candidate.Length);
            if (LooksLikeChk(candidate) && ParseChk(candidate).Count >= 3)
            {
                return candidate;
            }
        }

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

    private static readonly uint[] CryptTable = BuildCryptTable();

    private static uint[] BuildCryptTable()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (int i = 0; i < 0x100; i++)
        {
            int idx = i;
            for (int j = 0; j < 5; j++)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                uint t1 = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[idx] = t1 | (seed & 0xFFFF);
                idx += 0x100;
            }
        }

        return table;
    }

    private static uint AdvanceSeed1(uint s1)
    {
        return ((~s1 << 21) + 0x11111111u) | (s1 >> 11);
    }

    private static uint AdvanceSeed1N(uint s1, int n)
    {
        for (int i = 0; i < n; i++)
        {
            s1 = AdvanceSeed1(s1);
        }

        return s1;
    }

    private static List<int> FindHashTableByPattern(byte[] file, int minOffset, int hashCount)
    {
        var results = new List<int>();
        int tableBytes = hashCount * 16;
        if (tableBytes <= 0 || tableBytes > file.Length)
        {
            return results;
        }

        uint nameA = Encryption.HashString("staredit\\scenario.chk", Encryption.HashType.Hash_Name_A);
        uint nameB = Encryption.HashString("staredit\\scenario.chk", Encryption.HashType.Hash_Name_B);
        uint hashKey = Encryption.HashString("(hash table)", Encryption.HashType.Hash_FileKey);

        int j = (int)(nameA % (uint)hashCount);
        int byteOffset = j * 16;

        uint s1_j0 = AdvanceSeed1N(hashKey, j * 4);
        uint s1_j1 = AdvanceSeed1(s1_j0);
        uint ctEntry = CryptTable[0x400 + (s1_j1 & 0xFF)];

        int maxStart = file.Length - tableBytes;
        for (int p = Math.Max(minOffset, 0); p <= maxStart; p += 4)
        {
            int pos0 = p + byteOffset;
            if (pos0 + 8 > file.Length)
            {
                continue;
            }

            uint C0 = BitConverter.ToUInt32(file, pos0);
            uint C1 = BitConverter.ToUInt32(file, pos0 + 4);

            uint s2m0 = (C0 ^ nameA) - s1_j0;
            uint s2_next = nameA + s2m0 + (s2m0 << 5) + 3;
            uint s2m1 = s2_next + ctEntry;

            if (s1_j1 + s2m1 == (C1 ^ nameB))
            {
                results.Add(p);
            }
        }

        return results;
    }

    private static void AddFixedHashCandidates(List<int> candidates, int fileLength, int headerBase, uint headerHashOffset, int hashCount)
    {
        int tableLength = hashCount * 16;
        if (tableLength <= 0 || tableLength > fileLength)
        {
            return;
        }

        long relative = headerHashOffset;
        long absolute = headerBase + relative;
        AddCandidateOffset(candidates, absolute, tableLength, fileLength);
        AddCandidateOffset(candidates, absolute - 256, tableLength, fileLength);
        AddCandidateOffset(candidates, absolute - 512, tableLength, fileLength);
        AddCandidateOffset(candidates, absolute - 1024, tableLength, fileLength);
        AddCandidateOffset(candidates, relative, tableLength, fileLength);
        AddCandidateOffset(candidates, relative - 256, tableLength, fileLength);
        AddCandidateOffset(candidates, relative - 512, tableLength, fileLength);
        AddCandidateOffset(candidates, relative - 1024, tableLength, fileLength);
    }

    private static List<int> BuildAdjacentBlockCandidates(byte[] file, int hashOffset, int hashCount, uint headerBlockOffset, int blockCount, int headerBase)
    {
        var candidates = new List<int>();
        int tableLength = blockCount * 16;
        if (tableLength <= 0 || tableLength > file.Length)
        {
            return candidates;
        }

        int adjacent = hashOffset + hashCount * 16;
        AddCandidateOffset(candidates, adjacent, tableLength, file.Length);
        AddCandidateOffset(candidates, adjacent + 16, tableLength, file.Length);
        AddCandidateOffset(candidates, adjacent - 16, tableLength, file.Length);

        long relative = headerBlockOffset;
        long absolute = headerBase + relative;
        AddCandidateOffset(candidates, absolute, tableLength, file.Length);
        AddCandidateOffset(candidates, absolute - 256, tableLength, file.Length);
        AddCandidateOffset(candidates, absolute - 512, tableLength, file.Length);
        AddCandidateOffset(candidates, absolute - 1024, tableLength, file.Length);
        AddCandidateOffset(candidates, relative, tableLength, file.Length);
        AddCandidateOffset(candidates, relative - 256, tableLength, file.Length);
        AddCandidateOffset(candidates, relative - 512, tableLength, file.Length);
        AddCandidateOffset(candidates, relative - 1024, tableLength, file.Length);

        List<int> pattern = FindBlockTableByPattern(file, headerBase + 32, blockCount);
        foreach (int p in pattern)
        {
            AddCandidateOffset(candidates, p, tableLength, file.Length);
        }

        return candidates.Distinct().ToList();
    }

    private static List<int> FindBlockTableByPattern(byte[] file, int minOffset, int blockCount)
    {
        var results = new List<int>();
        int tableBytes = blockCount * 16;
        if (tableBytes <= 0 || tableBytes > file.Length)
        {
            return results;
        }

        uint blockKey = Encryption.HashString("(block table)", Encryption.HashType.Hash_FileKey);

        // seed2 starts at 0xEEEEEEEE — fully known for the first entry
        uint s1_0 = blockKey;
        uint s2m_0 = 0xEEEEEEEEu + CryptTable[0x400 + (s1_0 & 0xFF)];
        uint xorKey_0 = s1_0 + s2m_0;

        uint s1_1 = AdvanceSeed1(s1_0);

        int maxStart = file.Length - tableBytes;
        for (int p = Math.Max(minOffset, 0); p <= maxStart; p += 4)
        {
            if (p + 8 > file.Length)
            {
                break;
            }

            uint P0 = BitConverter.ToUInt32(file, p) ^ xorKey_0;
            if (P0 >= (uint)file.Length)
            {
                continue;
            }

            uint s2_1 = P0 + s2m_0 + (s2m_0 << 5) + 3;
            uint s2m_1 = s2_1 + CryptTable[0x400 + (s1_1 & 0xFF)];
            uint P1 = BitConverter.ToUInt32(file, p + 4) ^ (s1_1 + s2m_1);

            if (P1 == 0 || P1 > (uint)(file.Length - (int)P0))
            {
                continue;
            }

            results.Add(p);
        }

        return results;
    }

    private static bool DetectFreezeProtection(byte[] data, out uint[] seedKey, out uint[] destKey)
    {
        seedKey = null;
        destKey = null;
        byte[] marker = Encoding.ASCII.GetBytes("freeze05 protect");
        for (int i = data.Length - marker.Length; i >= 16; i--)
        {
            bool match = true;
            for (int k = 0; k < marker.Length; k++)
            {
                if (data[i + k] != marker[k])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            if (i + marker.Length + 16 > data.Length)
            {
                continue;
            }

            seedKey = new uint[4];
            destKey = new uint[4];
            for (int j = 0; j < 4; j++)
            {
                seedKey[j] = BitConverter.ToUInt32(data, i - 16 + j * 4);
                destKey[j] = BitConverter.ToUInt32(data, i + marker.Length + j * 4);
            }

            return true;
        }

        return false;
    }
}
