using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
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
        if (delta < 0)
        {
            if (-delta > 4096)
            {
                return null;
            }

            byte[] trimmed = new byte[expectedBytes];
            Buffer.BlockCopy(data, 0, trimmed, 0, expectedBytes);
            return trimmed;
        }

        if (delta > 4096 || (data.Length % 2) != 0)
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

    private static void EnsureMinimumSize(Dictionary<string, List<byte[]>> grouped, string name, int minSize, Stats stats)
    {
        List<byte[]> list;
        if (!grouped.TryGetValue(name, out list) || list.Count == 0)
        {
            grouped[name] = new List<byte[]> { new byte[minSize] };
            stats.AddedDefaultSections++;
            return;
        }

        if (list[0].Length < minSize)
        {
            byte[] padded = new byte[minSize];
            Buffer.BlockCopy(list[0], 0, padded, 0, list[0].Length);
            list[0] = padded;
        }
    }

    private static void WriteStandardMpq(string output, byte[] chk, List<MpqFileEntry> extraFiles, byte[] trailingBlob)
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

        // Freeze05 stores [seedKey][marker][destKey] past the MPQ tables; its EUD
        // runtime reads it back. Re-append so the protection can find it again.
        if (trailingBlob != null && trailingBlob.Length > 0)
        {
            using (var fs = new FileStream(output, FileMode.Append, FileAccess.Write))
            {
                fs.Write(trailingBlob, 0, trailingBlob.Length);
            }
        }
    }
}
