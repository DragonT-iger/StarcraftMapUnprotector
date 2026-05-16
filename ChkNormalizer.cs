using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static partial class StarcraftMapUnprotector
{
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

            strings[i] = Encoding.GetEncoding(949).GetString(str, offset, end - offset);
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

            strings[i] = Encoding.GetEncoding(949).GetString(str, offset, end - offset);
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
        using (var writer = new BinaryWriter(ms, Encoding.GetEncoding(949)))
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
                offset += Encoding.GetEncoding(949).GetByteCount(value) + 1;
            }

            for (int i = 0; i < count; i++)
            {
                string value = i < strings.Count ? strings[i] ?? "" : "";
                writer.Write(Encoding.GetEncoding(949).GetBytes(value));
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
}
