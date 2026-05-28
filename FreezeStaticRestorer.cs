using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
    private const uint FreezeOffsetCryptStep = 0x46B7A62C;
    private const uint DeathTableBase = 0x0058A364;

    private sealed class FreezeKeyFile
    {
        public readonly uint[] SeedKey = new uint[4];
        public readonly uint[] DestKey = new uint[4];
        public uint FileCursor;
    }

    private sealed class FreezeTriggerSummary
    {
        public int TotalTriggers;
        public int EncryptedTriggers;
        public readonly int[] EncryptedByExecPlayer = new int[8];
        public readonly List<int> FirstEncrypted = new List<int>();
        public int FreezeOnlyCandidates;
        public int SetDeathsOnlyEudTriggers;
        public int EudSetDeathsActions;
        public int CurrentPlayerWrites;
        public readonly List<int> FirstFreezeOnly = new List<int>();
        public readonly List<FreezeWriteCandidate> FirstWrites = new List<FreezeWriteCandidate>();
    }

    private sealed class FreezeWriteCandidate
    {
        public int TriggerIndex;
        public int ActionIndex;
        public uint Player;
        public ushort Unit;
        public uint Value;
        public byte Modifier;
        public uint Epd;
        public uint Address;
    }

    private sealed class KeycalcInputSegment
    {
        public string Name;
        public int Offset;
        public int Length;
    }

    private sealed class StaticKeycalcResult
    {
        public uint[] SeedKey;
        public uint CryptKeyVal;
        public int HeaderSamples;
        public int HashSamples;
        public int ScenarioSamples;
        public int TableWalkSamples;
    }

    private static byte[] BuildStaticLv2Chk(string input, byte[] inputBytes, byte[] chk, Stats stats)
    {
        byte[] trigData;
        if (!TryGetFirstChkSection(chk, "TRIG", out trigData))
        {
            throw new InvalidDataException("Lv2: TRIG section not found in CHK.");
        }

        if (trigData.Length % FreezeTrigSize != 0)
        {
            throw new InvalidDataException("Lv2: TRIG section size is not a multiple of " + FreezeTrigSize + ".");
        }

        int totalTriggers = trigData.Length / FreezeTrigSize;
        int encryptedOffset;
        int encryptedCount = CountEncryptedFreezeTriggers(trigData, totalTriggers, out encryptedOffset);

        if (encryptedCount == 0)
        {
            Console.WriteLine("  Lv2: no encrypted triggers found. Returning CHK as-is.");
            return chk;
        }

        Console.WriteLine("  Lv2: " + encryptedCount + " encrypted triggers in " + totalTriggers + " total.");

        uint recoveredKey;
        if (!TryRecoverFreezeKeyByFastBruteforce(trigData, totalTriggers, out recoveredKey))
        {
            throw new InvalidDataException("Lv2: triggerKey brute-force failed. Cannot proceed.");
        }

        Console.WriteLine("  Lv2: triggerKey = 0x" + recoveredKey.ToString("X8"));

        int decrypted = DecryptAllFreezeTriggers(trigData, recoveredKey, false);
        stats.DecryptedFreezeTriggers = decrypted;
        Console.WriteLine("  Lv2: decrypted " + decrypted + " triggers (flag restored to exec_flags only).");
        Console.WriteLine("  Lv2: EUD VM triggers preserved (not disabled).");

        return ReplaceTrigSection(chk, trigData);
    }

    private static byte[] ReplaceTrigSection(byte[] chk, byte[] newTrigData)
    {
        using (var ms = new MemoryStream(chk.Length))
        {
            int pos = 0;
            while (pos + 8 <= chk.Length)
            {
                string name = System.Text.Encoding.ASCII.GetString(chk, pos, 4);
                uint size32 = BitConverter.ToUInt32(chk, pos + 4);
                if (size32 > (uint)(chk.Length - pos - 8))
                    break;
                int size = (int)size32;

                if (name == "TRIG")
                {
                    ms.Write(chk, pos, 4);
                    ms.Write(BitConverter.GetBytes(newTrigData.Length), 0, 4);
                    ms.Write(newTrigData, 0, newTrigData.Length);
                }
                else
                {
                    ms.Write(chk, pos, 8 + size);
                }

                pos += 8 + size;
            }

            if (pos < chk.Length)
            {
                ms.Write(chk, pos, chk.Length - pos);
            }

            return ms.ToArray();
        }
    }

    private static void RunLv2Diagnostics(string input, byte[] inputBytes, byte[] chk, Stats stats)
    {
        Console.WriteLine("Lv2 static restore diagnostic");
        Console.WriteLine("Input : " + input);
        Console.WriteLine("CHK   : " + chk.Length + " bytes");
        Console.WriteLine("Freeze05 protection: " + (stats.IsFreezeProtected ? "DETECTED" : "no"));

        if (stats.IsFreezeProtected)
        {
            Console.WriteLine("  marker seedKey    : " + FormatKey(stats.FreezeSeedKey));
            Console.WriteLine("  marker destKey    : " + FormatKey(stats.FreezeDestKey));
        }

        byte[] trigData;
        if (!TryGetFirstChkSection(chk, "TRIG", out trigData))
        {
            Console.WriteLine("TRIG section: not found");
            return;
        }

        FreezeTriggerSummary summary = SummarizeFreezeTriggers(trigData);
        PrintFreezeTriggerSummary(summary);

        FreezeKeyFile keyFile;
        if (TryReadFreezeKeyFile(input, stats, out keyFile))
        {
            Console.WriteLine("(keyfile) seedKey    : " + FormatKey(keyFile.SeedKey));
            Console.WriteLine("(keyfile) destKey    : " + FormatKey(keyFile.DestKey));
            Console.WriteLine("(keyfile) fileCursor : 0x" + keyFile.FileCursor.ToString("X8") +
                              " (" + keyFile.FileCursor + ")");
        }
        else
        {
            Console.WriteLine("(keyfile)            : not readable");
        }

        if (summary.EncryptedTriggers > 0)
        {
            uint recoveredKey;
            if (TryRecoverFreezeKeyByFastBruteforce((byte[])trigData.Clone(), summary.TotalTriggers, out recoveredKey))
            {
                Console.WriteLine("recovered triggerKey : 0x" + recoveredKey.ToString("X8"));

                uint[] seedForCrypt = keyFile != null ? keyFile.SeedKey : stats.FreezeSeedKey;
                if (seedForCrypt != null && seedForCrypt.Length >= 4)
                {
                    uint cryptKeyVal = ComputeCryptKeyVal(seedForCrypt);
                    uint triggerKeyVal = FreezeUnmix2(recoveredKey, cryptKeyVal);
                    Console.WriteLine("cryptKeyVal(seed)    : 0x" + cryptKeyVal.ToString("X8"));
                    Console.WriteLine("triggerKeyVal        : 0x" + triggerKeyVal.ToString("X8") +
                        " (derived by unmix2(recovered, cryptKeyVal))");
                }

                PrintLv2KeycalcInputDiff(inputBytes, chk, trigData, recoveredKey);
            }
            else
            {
                Console.WriteLine("recovered triggerKey : FAILED");
            }
        }

        PrintMpqStructureDiagnostics(inputBytes);

        Console.WriteLine("keycalc strengthened seedKey : pending static port");
        Console.WriteLine("offset decrypt cryptKey/tKeys: pending oJumperArray + initOffsets random-r recovery");
        Console.WriteLine("offset decrypt formula       : plainNext = (encryptedNext ^ cryptKey2), cryptKey2 += 0x" +
                          FreezeOffsetCryptStep.ToString("X8") + " per oJumper entry");
    }

    private static void PrintLv2KeycalcInputDiff(byte[] inputBytes, byte[] chk, byte[] trigData, uint recoveredKey)
    {
        if (LooksLikeChk(inputBytes))
        {
            Console.WriteLine("keycalc input diff     : skipped for raw CHK input");
            return;
        }

        try
        {
            byte[] lv2Trig = (byte[])trigData.Clone();
            DecryptAllFreezeTriggers(lv2Trig, recoveredKey, false);
            byte[] lv2Chk = ReplaceTrigSection(chk, lv2Trig);
            Lv2MpqPatchResult patch = BuildLv2MpqPatch(inputBytes, lv2Chk);

            PrintKeycalcCandidateDiff(inputBytes, patch.File, patch.Tables);
        }
        catch (Exception ex)
        {
            Console.WriteLine("keycalc input diff     : error: " + ex.Message);
        }
    }

    private static void PrintKeycalcCandidateDiff(byte[] originalFile, byte[] patchedFile, MpqTableLocation tables)
    {
        Console.WriteLine("keycalc candidate diff : original MPQ vs Lv2 patched MPQ");

        var segments = new List<KeycalcInputSegment>();
        BlockTable block = tables.Blocks[tables.ScenarioBlockIndex];
        int scenarioOffset = tables.Header.BaseOffset + block.FileOffset;
        int scenarioLength = (int)block.CompSize;

        AddKeycalcSegment(segments, "mpq header", tables.Header.BaseOffset, 32, originalFile.Length);
        AddKeycalcSegment(segments, "hash table raw", tables.HashOffset, tables.Header.HashCount * 16, originalFile.Length);
        AddRawTableSegment(segments, "block table raw", tables.BlockOffset, tables.Header.BlockCount * 16,
            originalFile.Length, tables.Header.BaseOffset, scenarioOffset, scenarioLength);
        AddKeycalcSegment(segments, "scenario.chk raw block", scenarioOffset, (int)block.CompSize, originalFile.Length);

        int changedSegments = 0;
        foreach (KeycalcInputSegment segment in segments)
        {
            int firstDiff = FindFirstDiff(originalFile, patchedFile, segment.Offset, segment.Length);
            bool changed = firstDiff >= 0;
            if (changed)
            {
                changedSegments++;
            }

            uint oldHash = Fnv1a32(originalFile, segment.Offset, segment.Length);
            uint newHash = Fnv1a32(patchedFile, segment.Offset, segment.Length);
            Console.WriteLine("  " + segment.Name.PadRight(22) +
                              " off=0x" + segment.Offset.ToString("X8") +
                              " len=" + segment.Length +
                              " hash " + oldHash.ToString("X8") + " -> " + newHash.ToString("X8") +
                              (changed ? " firstDiff=+0x" + (firstDiff - segment.Offset).ToString("X") : " unchanged"));
        }

        PrintScenarioSectorDiff(originalFile, patchedFile, tables);
        PrintStaticKeycalcCandidate(originalFile, patchedFile, tables);
        Console.WriteLine("keycalc changed regions: " + changedSegments + " / " + segments.Count +
                          " candidate top-level region(s)");
    }

    private static void PrintStaticKeycalcCandidate(byte[] originalFile, byte[] patchedFile, MpqTableLocation tables)
    {
        if (tables == null || tables.Header == null || tables.Blocks == null)
        {
            return;
        }

        try
        {
            BlockTable scenario = tables.Blocks[tables.ScenarioBlockIndex];
            StaticKeycalcResult original = ComputeStaticKeycalcCandidate(
                originalFile,
                tables,
                scenario,
                tables.Header.HashTableOffset,
                tables.Header.BlockTableOffset);
            StaticKeycalcResult patched = ComputeStaticKeycalcCandidate(
                patchedFile,
                tables,
                scenario,
                tables.Header.HashTableOffset,
                tables.Header.BlockTableOffset);

            Console.WriteLine("  static keycalc model  seed " + FormatKey(original.SeedKey) +
                              " -> " + FormatKey(patched.SeedKey));
            Console.WriteLine("  static keycalc model  cryptKeyVal 0x" + original.CryptKeyVal.ToString("X8") +
                              " -> 0x" + patched.CryptKeyVal.ToString("X8") +
                              (original.CryptKeyVal == patched.CryptKeyVal ? " unchanged" : " changed"));
            Console.WriteLine("  static keycalc samples header/hash/scenario/tablewalk: " +
                              original.HeaderSamples + "/" + original.HashSamples + "/" +
                              original.ScenarioSamples + "/" + original.TableWalkSamples);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  static keycalc model  error: " + ex.Message);
        }
    }

    private static StaticKeycalcResult ComputeStaticKeycalcCandidate(
        byte[] file,
        MpqTableLocation tables,
        BlockTable scenario,
        uint headerHashOffset,
        uint headerBlockOffset)
    {
        uint[] seed = DetectFreezeSeedKey(file);
        var result = new StaticKeycalcResult
        {
            SeedKey = seed
        };

        int headerOffset = tables.Header.BaseOffset;
        for (int i = 0; i < 8; i++)
        {
            uint sample;
            if (TryReadUInt32At(file, headerOffset + i * 4, out sample))
            {
                FeedKeycalcSample(seed, sample);
                result.HeaderSamples++;
            }
        }

        int hashOffset = tables.HashOffset;
        if (IsPlausibleRawSpan(hashOffset, tables.Header.HashCount * 16, file.Length, tables.Header.BaseOffset))
        {
            for (int i = 0; i < tables.Header.HashCount * 4; i++)
            {
                uint sample;
                if (TryReadUInt32At(file, hashOffset + i * 4, out sample))
                {
                    FeedKeycalcSample(seed, sample);
                    result.HashSamples++;
                }
            }
        }

        int blockOffset = tables.BlockOffset;
        if (IsPlausibleRawSpan(blockOffset, tables.Header.BlockCount * 16, file.Length, tables.Header.BaseOffset))
        {
            int startBlock = Math.Max(0, Math.Min(tables.ScenarioBlockIndex, tables.Header.BlockCount - 1));
            for (int i = startBlock * 4; i < tables.Header.BlockCount * 4; i++)
            {
                uint sample;
                if (TryReadUInt32At(file, blockOffset + i * 4, out sample))
                {
                    FeedKeycalcSample(seed, sample);
                    result.TableWalkSamples++;
                }
            }

            int sampleCount = Math.Min(512, tables.Header.BlockCount * 4);
            uint cursor = 0;
            for (int keyIndex = 0; keyIndex < 4; keyIndex++)
            {
                for (int j = 0; j < sampleCount; j++)
                {
                    int dwordIndex = (int)(cursor % (uint)Math.Max(1, tables.Header.BlockCount * 4));
                    uint sample;
                    if (TryReadUInt32At(file, blockOffset + dwordIndex * 4, out sample))
                    {
                        seed[keyIndex] = unchecked(seed[keyIndex] + seed[keyIndex] + sample);
                    }

                    cursor = FreezeMix2(cursor, (uint)j);
                }
            }
        }

        int scenarioOffset = tables.Header.BaseOffset + scenario.FileOffset;
        int scenarioLength = scenario.CompSize > Int32.MaxValue ? 0 : (int)scenario.CompSize;
        if (scenarioLength > 0 && IsPlausibleRawSpan(scenarioOffset, scenarioLength, file.Length, tables.Header.BaseOffset))
        {
            int sectorSize = GetMpqSectorSize(file, tables.Header.BaseOffset);
            int fileSize = scenario.FileSize > Int32.MaxValue ? 0 : (int)scenario.FileSize;
            int sectorCount = sectorSize > 0 && fileSize > 0
                ? Math.Max(1, (fileSize + sectorSize - 1) / sectorSize)
                : 1;
            int tableBytes = (sectorCount + 1) * 4;
            byte[] offsets = sectorCount > 1 && tableBytes <= scenarioLength
                ? ReadScenarioSectorOffsetTable(file, scenarioOffset, tableBytes, scenario)
                : null;

            if (offsets != null)
            {
                for (int sector = 0; sector < sectorCount; sector += 3)
                {
                    int start = (int)BitConverter.ToUInt32(offsets, sector * 4);
                    if (start >= 0 && start + 4 <= scenarioLength)
                    {
                        FeedKeycalcSample(seed, BitConverter.ToUInt32(file, scenarioOffset + start));
                        result.ScenarioSamples++;
                    }
                }
            }
            else
            {
                for (int offset = 0; offset + 4 <= scenarioLength; offset += 4096 * 3)
                {
                    FeedKeycalcSample(seed, BitConverter.ToUInt32(file, scenarioOffset + offset));
                    result.ScenarioSamples++;
                }
            }
        }

        for (int i = 0; i < 64; i++)
        {
            seed[0] = FreezeMix2(seed[0], seed[3]);
            seed[1] = FreezeMix2(seed[1], seed[0]);
            seed[2] = FreezeMix2(seed[2], seed[1]);
        }

        if (IsPlausibleRawSpan(blockOffset, tables.Header.BlockCount * 16, file.Length, tables.Header.BaseOffset))
        {
            int tail = blockOffset + tables.Header.BlockCount * 16;
            for (int i = 0; i < 4; i++)
            {
                uint sample;
                if (TryReadUInt32At(file, tail + i * 4, out sample))
                {
                    seed[i] = FreezeMix2(seed[i], sample);
                }
            }
        }

        result.CryptKeyVal = ComputeCryptKeyVal(seed);
        return result;
    }

    private static uint[] DetectFreezeSeedKey(byte[] file)
    {
        uint[] seedKey;
        uint[] destKey;
        if (DetectFreezeProtection(file, out seedKey, out destKey) && seedKey != null && seedKey.Length >= 4)
        {
            return (uint[])seedKey.Clone();
        }

        return new uint[4];
    }

    private static void FeedKeycalcSample(uint[] seed, uint sample)
    {
        seed[0] = FreezeMix2(seed[0], sample);
        seed[1] = FreezeMix2(seed[1], seed[0]);
        seed[2] = FreezeMix2(seed[2], seed[1]);
        seed[3] = FreezeMix2(seed[3], seed[2]);
    }

    private static bool TryReadUInt32At(byte[] data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        value = BitConverter.ToUInt32(data, offset);
        return true;
    }

    private static bool IsPlausibleRawSpan(int offset, int length, int fileLength, int headerBase)
    {
        return offset >= headerBase &&
               length > 0 &&
               offset + length >= offset &&
               offset + length <= fileLength;
    }

    private static void AddRawTableSegment(
        List<KeycalcInputSegment> segments,
        string name,
        int offset,
        int length,
        int fileLength,
        int headerBase,
        int scenarioOffset,
        int scenarioLength)
    {
        bool plausible = offset >= headerBase + 32 &&
                         length > 0 &&
                         offset + length <= fileLength &&
                         !RangesOverlap(offset, length, scenarioOffset, scenarioLength);
        if (!plausible)
        {
            Console.WriteLine("  " + name.PadRight(22) +
                              " skipped: recovered logical table does not map to a plausible raw table span");
            return;
        }

        AddKeycalcSegment(segments, name, offset, length, fileLength);
    }

    private static void PrintScenarioSectorDiff(byte[] originalFile, byte[] patchedFile, MpqTableLocation tables)
    {
        BlockTable block = tables.Blocks[tables.ScenarioBlockIndex];
        int scenarioOffset = tables.Header.BaseOffset + block.FileOffset;
        int length = (int)block.CompSize;
        if (length <= 0 || scenarioOffset < 0 || scenarioOffset + length > originalFile.Length)
        {
            return;
        }

        int sectorSize = GetMpqSectorSize(originalFile, tables.Header.BaseOffset);
        int fileSize = block.FileSize > Int32.MaxValue ? 0 : (int)block.FileSize;
        int sectorCount = sectorSize > 0 && fileSize > 0
            ? Math.Max(1, (fileSize + sectorSize - 1) / sectorSize)
            : 1;
        int tableBytes = (sectorCount + 1) * 4;
        if (sectorCount <= 1 || tableBytes > length)
        {
            return;
        }

        byte[] oldOffsets = ReadScenarioSectorOffsetTable(originalFile, scenarioOffset, tableBytes, block);
        byte[] newOffsets = ReadScenarioSectorOffsetTable(patchedFile, scenarioOffset, tableBytes, block);
        if (oldOffsets == null || newOffsets == null)
        {
            Console.WriteLine("  scenario sectors      offset table unreadable");
            return;
        }

        int changed = 0;
        int firstChanged = -1;
        for (int i = 0; i < sectorCount; i++)
        {
            int oldStart = (int)BitConverter.ToUInt32(oldOffsets, i * 4);
            int oldEnd = (int)BitConverter.ToUInt32(oldOffsets, (i + 1) * 4);
            int newStart = (int)BitConverter.ToUInt32(newOffsets, i * 4);
            int newEnd = (int)BitConverter.ToUInt32(newOffsets, (i + 1) * 4);
            if (oldStart < 0 || oldEnd < oldStart || oldEnd > length ||
                newStart < 0 || newEnd < newStart || newEnd > length)
            {
                continue;
            }

            int cmpLen = Math.Min(oldEnd - oldStart, newEnd - newStart);
            int diff = cmpLen > 0
                ? FindFirstDiff(originalFile, patchedFile, scenarioOffset + oldStart, cmpLen, scenarioOffset + newStart)
                : -1;
            if (oldEnd - oldStart != newEnd - newStart || diff >= 0)
            {
                changed++;
                if (firstChanged < 0)
                {
                    firstChanged = i;
                }
            }
        }

        Console.WriteLine("  scenario sectors      changed=" + changed + "/" + sectorCount +
                          (firstChanged >= 0 ? " first=" + firstChanged : ""));
    }

    private static byte[] ReadScenarioSectorOffsetTable(byte[] file, int scenarioOffset, int tableBytes, BlockTable block)
    {
        byte[] table = new byte[tableBytes];
        Buffer.BlockCopy(file, scenarioOffset, table, 0, table.Length);
        if ((((uint)block.Flags & (uint)Flags.Encrypted) != 0))
        {
            uint fileKey = Encryption.HashString("staredit\\scenario.chk", Encryption.HashType.Hash_FileKey);
            if ((((uint)block.Flags & (uint)Flags.ModKey) != 0))
            {
                fileKey = (fileKey + (uint)block.FileOffset) ^ block.FileSize;
            }

            DecryptMpqData(table, fileKey - 1);
        }

        return table;
    }

    private static void AddKeycalcSegment(List<KeycalcInputSegment> segments, string name, int offset, int length, int fileLength)
    {
        if (offset < 0 || length <= 0 || offset + length > fileLength)
        {
            return;
        }

        segments.Add(new KeycalcInputSegment
        {
            Name = name,
            Offset = offset,
            Length = length
        });
    }

    private static bool RangesOverlap(int leftOffset, int leftLength, int rightOffset, int rightLength)
    {
        long leftEnd = (long)leftOffset + leftLength;
        long rightEnd = (long)rightOffset + rightLength;
        return leftOffset < rightEnd && rightOffset < leftEnd;
    }

    private static int FindFirstDiff(byte[] left, byte[] right, int offset, int length)
    {
        return FindFirstDiff(left, right, offset, length, offset);
    }

    private static int FindFirstDiff(byte[] left, byte[] right, int leftOffset, int length, int rightOffset)
    {
        int max = Math.Min(length, Math.Min(left.Length - leftOffset, right.Length - rightOffset));
        for (int i = 0; i < max; i++)
        {
            if (left[leftOffset + i] != right[rightOffset + i])
            {
                return leftOffset + i;
            }
        }

        return -1;
    }

    private static uint Fnv1a32(byte[] data, int offset, int length)
    {
        uint hash = 2166136261u;
        int end = Math.Min(data.Length, offset + length);
        for (int i = offset; i < end; i++)
        {
            hash ^= data[i];
            hash *= 16777619u;
        }

        return hash;
    }

    private static bool TryReadFreezeKeyFile(string input, Stats stats, out FreezeKeyFile keyFile)
    {
        keyFile = null;

        byte[] data = TryExtractNamedMpqFile(input, "(keyfile)", stats);
        if (data == null || data.Length < 36)
        {
            return false;
        }

        keyFile = new FreezeKeyFile();
        for (int i = 0; i < 4; i++)
        {
            keyFile.SeedKey[i] = BitConverter.ToUInt32(data, i * 4);
            keyFile.DestKey[i] = BitConverter.ToUInt32(data, 16 + i * 4);
        }

        keyFile.FileCursor = BitConverter.ToUInt32(data, 32);
        return true;
    }

    private static bool TryGetFirstChkSection(byte[] chk, string sectionName, out byte[] data)
    {
        data = null;
        int pos = 0;
        while (pos + 8 <= chk.Length)
        {
            string name = System.Text.Encoding.ASCII.GetString(chk, pos, 4);
            uint size32 = BitConverter.ToUInt32(chk, pos + 4);
            if (size32 > Int32.MaxValue)
            {
                return false;
            }

            int size = (int)size32;
            if (pos + 8 + size > chk.Length)
            {
                return false;
            }

            if (name == sectionName)
            {
                data = new byte[size];
                Buffer.BlockCopy(chk, pos + 8, data, 0, size);
                return true;
            }

            pos += 8 + size;
        }

        return false;
    }

    private static FreezeTriggerSummary SummarizeFreezeTriggers(byte[] trigData)
    {
        var summary = new FreezeTriggerSummary();
        if (trigData == null || trigData.Length % FreezeTrigSize != 0)
        {
            return summary;
        }

        summary.TotalTriggers = trigData.Length / FreezeTrigSize;
        for (int t = 0; t < summary.TotalTriggers; t++)
        {
            int off = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, off + 2368);
            if (flag >= 0x80000000u)
            {
                summary.EncryptedTriggers++;
                if (summary.FirstEncrypted.Count < 32)
                {
                    summary.FirstEncrypted.Add(t);
                }

                for (int p = 0; p < summary.EncryptedByExecPlayer.Length; p++)
                {
                    if (trigData[off + 2372 + p] != 0)
                    {
                        summary.EncryptedByExecPlayer[p]++;
                    }
                }
            }

            bool setDeathsOnlyEud = IsFreezeEudTrigger(trigData, off);
            if (setDeathsOnlyEud)
            {
                summary.SetDeathsOnlyEudTriggers++;
                summary.FreezeOnlyCandidates++;
                if (summary.FirstFreezeOnly.Count < 64)
                {
                    summary.FirstFreezeOnly.Add(t);
                }
            }

            for (int a = 0; a < 64; a++)
            {
                int actionOffset = off + 320 + a * 32;
                byte actionType = trigData[actionOffset + 26];
                if (actionType != 45)
                {
                    continue;
                }

                uint player = BitConverter.ToUInt32(trigData, actionOffset + 16);
                uint value = BitConverter.ToUInt32(trigData, actionOffset + 20);
                ushort unit = BitConverter.ToUInt16(trigData, actionOffset + 24);
                byte modifier = trigData[actionOffset + 27];
                if (player > 27)
                {
                    summary.EudSetDeathsActions++;
                    if (summary.FirstWrites.Count < 80)
                    {
                        uint epd = unchecked(player + (uint)unit * 12u);
                        summary.FirstWrites.Add(new FreezeWriteCandidate
                        {
                            TriggerIndex = t,
                            ActionIndex = a,
                            Player = player,
                            Unit = unit,
                            Value = value,
                            Modifier = modifier,
                            Epd = epd,
                            Address = unchecked(DeathTableBase + epd * 4u)
                        });
                    }
                }

                if (player == 13)
                {
                    summary.CurrentPlayerWrites++;
                }
            }
        }

        return summary;
    }

    private static void PrintFreezeTriggerSummary(FreezeTriggerSummary summary)
    {
        Console.WriteLine("TRIG total triggers    : " + summary.TotalTriggers);
        Console.WriteLine("encrypted trigger count: " + summary.EncryptedTriggers);
        Console.WriteLine("encrypted by exec slot : " + string.Join(", ",
            summary.EncryptedByExecPlayer.Select((v, i) => "P" + i + "=" + v).ToArray()));
        Console.WriteLine("first encrypted indexes: " + FormatIntList(summary.FirstEncrypted));
        Console.WriteLine("Freeze-only candidates : " + summary.FreezeOnlyCandidates);
        Console.WriteLine("SetDeaths-only EUD     : " + summary.SetDeathsOnlyEudTriggers);
        Console.WriteLine("first Freeze-only idx  : " + FormatIntList(summary.FirstFreezeOnly));
        Console.WriteLine("EUD SetDeaths actions  : " + summary.EudSetDeathsActions);
        Console.WriteLine("CurrentPlayer writes   : " + summary.CurrentPlayerWrites);

        if (summary.FirstWrites.Count > 0)
        {
            Console.WriteLine("nextptr/write target candidates (first " + summary.FirstWrites.Count + "):");
            foreach (FreezeWriteCandidate write in summary.FirstWrites)
            {
                Console.WriteLine("  T" + write.TriggerIndex + "/A" + write.ActionIndex +
                                  " player=0x" + write.Player.ToString("X8") +
                                  " unit=" + write.Unit +
                                  " epd=0x" + write.Epd.ToString("X8") +
                                  " addr=0x" + write.Address.ToString("X8") +
                                  " mod=" + write.Modifier +
                                  " value=0x" + write.Value.ToString("X8"));
            }
        }
    }

    private static void PrintMpqStructureDiagnostics(byte[] inputBytes)
    {
        try
        {
            MpqTableLocation tables = LocateScenarioTablesForPatch(inputBytes);
            if (tables == null)
            {
                Console.WriteLine("MPQ scenario table     : not located");
                return;
            }

            BlockTable block = tables.Blocks[tables.ScenarioBlockIndex];
            Console.WriteLine("MPQ header base        : 0x" + tables.Header.BaseOffset.ToString("X"));
            Console.WriteLine("MPQ hash/block offset  : 0x" + tables.HashOffset.ToString("X") +
                              " / 0x" + tables.BlockOffset.ToString("X"));
            Console.WriteLine("MPQ hash/block count   : " + tables.Header.HashCount +
                              " / " + tables.Header.BlockCount);
            Console.WriteLine("scenario block index   : " + tables.ScenarioBlockIndex);
            Console.WriteLine("scenario fileOffset    : 0x" + block.FileOffset.ToString("X8"));
            Console.WriteLine("scenario comp/fileSize : " + block.CompSize + " / " + block.FileSize);
            Console.WriteLine("scenario flags         : 0x" + ((uint)block.Flags).ToString("X8"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("MPQ scenario table     : error: " + ex.Message);
        }
    }

    private static string FormatIntList(List<int> values)
    {
        if (values == null || values.Count == 0)
        {
            return "(none)";
        }

        return string.Join(", ", values.Select(v => v.ToString()).ToArray());
    }

    private static uint FreezeUnT2(uint y)
    {
        uint x = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            uint mask = bit == 31 ? 0xFFFFFFFFu : ((2u << bit) - 1u);
            if (((y - FreezeT2(x)) & mask) != 0)
            {
                x += 1u << bit;
            }
        }

        return x;
    }

    private static uint FreezeUnmix2(uint z, uint y)
    {
        return FreezeUnT2(unchecked(z - FreezeMixConst - y));
    }
}
