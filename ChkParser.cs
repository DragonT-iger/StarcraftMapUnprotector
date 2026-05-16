using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
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
}
