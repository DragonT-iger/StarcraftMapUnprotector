using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TkMPQLib;

internal static class ChkTriggerDiag
{
    private sealed class Section
    {
        public string Name;
        public byte[] Data;
    }

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ChkTriggerDiag.exe <map.scx>");
            return 1;
        }

        byte[] chk = ReadScenarioChk(args[0]);
        if (chk == null)
        {
            Console.Error.WriteLine("Could not read staredit\\scenario.chk");
            return 2;
        }

        var sections = ParseChk(chk);
        ushort strCount = 0;
        if (sections.ContainsKey("STR ") && sections["STR "].Length >= 2)
        {
            strCount = BitConverter.ToUInt16(sections["STR "], 0);
        }

        Console.WriteLine("strings=" + strCount);
        PrintSections(sections);
        PrintSectionLength(sections, "UPRP");
        PrintSectionLength(sections, "UPUS");
        PrintSectionLength(sections, "MRGN");
        PrintUnitSection(sections);
        PrintSection(sections, "TRIG", strCount);
        PrintSection(sections, "MBRF", strCount);
        return 0;
    }

    private static byte[] ReadScenarioChk(string path)
    {
        if (Path.GetExtension(path).Equals(".chk", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllBytes(path);
        }

        string[] names =
        {
            "staredit\\scenario.chk",
            "staredit/scenario.chk",
            "scenario.chk"
        };

        Locale[] locales =
        {
            Locale.English, Locale.Neutral, Locale.Korean, Locale.Japanese,
            Locale.Chinese, Locale.EnglishUK, Locale.German, Locale.French,
            Locale.Spanish, Locale.Italian, Locale.Polish, Locale.Portuguese,
            Locale.Russsuan, Locale.Czech
        };

        using (var mpq = new TkMPQ(path))
        {
            foreach (string name in names)
            {
                foreach (Locale locale in locales)
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
            }
        }

        return null;
    }

    private static void PrintSections(Dictionary<string, byte[]> sections)
    {
        Console.Write("sections");
        foreach (var pair in sections)
        {
            Console.Write(" " + pair.Key.Replace(" ", "_") + ":" + pair.Value.Length);
        }

        Console.WriteLine();
    }

    private static Dictionary<string, byte[]> ParseChk(byte[] chk)
    {
        var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int p = 0; p + 8 <= chk.Length;)
        {
            string name = Encoding.ASCII.GetString(chk, p, 4);
            int len = BitConverter.ToInt32(chk, p + 4);
            p += 8;
            if (len < 0 || p + len > chk.Length)
            {
                break;
            }

            var data = new byte[len];
            Buffer.BlockCopy(chk, p, data, 0, len);
            sections[name] = data;
            p += len;
        }

        return sections;
    }

    private static void PrintSection(Dictionary<string, byte[]> sections, string name, ushort strCount)
    {
        if (!sections.ContainsKey(name))
        {
            Console.WriteLine(name + ": missing");
            return;
        }

        byte[] data = sections[name];
        Console.WriteLine(name + ": bytes=" + data.Length + " records=" + (data.Length / 2400) + " rem=" + (data.Length % 2400));

        var actionTypes = new SortedDictionary<int, int>();
        int badActionType = 0;
        int commentActions = 0;
        int badActionString = 0;
        int nonZeroActionPad = 0;
        int nonZeroConditionPad = 0;
        int badConditionType = 0;
        int badConditionLocation = 0;
        int badActionLocation = 0;
        int badConditionUnit = 0;
        int badActionUnit = 0;
        int badTriggerPlayerFlag = 0;
        int badConditionFlags = 0;
        int badActionFlags = 0;
        int badConditionComparison = 0;
        int badActionModifier = 0;
        int badPropertySlot = 0;
        int badSecondLocation = 0;
        var badConditionUnitByType = new SortedDictionary<int, int>();
        var badActionUnitByType = new SortedDictionary<int, int>();
        var badActionModifierByType = new SortedDictionary<int, int>();
        var badPropertySlotByType = new SortedDictionary<int, int>();
        var badSecondLocationByType = new SortedDictionary<int, int>();
        var conditionFlagValues = new SortedDictionary<int, int>();
        var actionFlagValues = new SortedDictionary<int, int>();

        for (int trigger = 0; trigger + 2399 < data.Length; trigger += 2400)
        {
            for (int i = 0; i < 16; i++)
            {
                int c = trigger + i * 20;
                byte type = data[c + 15];
                if (type > 23)
                {
                    badConditionType++;
                }

                if (data[c + 18] != 0 || data[c + 19] != 0)
                {
                    nonZeroConditionPad++;
                }

                Increment(conditionFlagValues, data[c + 17]);
                if ((data[c + 17] & 0xE0) != 0)
                {
                    badConditionFlags++;
                }

                if (data[c + 14] > 10)
                {
                    badConditionComparison++;
                }

                if (IsBadLocation(data, c))
                {
                    badConditionLocation++;
                }

                if (BitConverter.ToUInt16(data, c + 12) > 232 && BitConverter.ToUInt16(data, c + 12) != UInt16.MaxValue)
                {
                    badConditionUnit++;
                    Increment(badConditionUnitByType, type);
                }
            }

            for (int i = 0; i < 64; i++)
            {
                int a = trigger + 320 + i * 32;
                byte type = data[a + 26];
                if (!actionTypes.ContainsKey(type))
                {
                    actionTypes[type] = 0;
                }

                actionTypes[type]++;
                if (type > 57)
                {
                    badActionType++;
                }

                if (type == 47)
                {
                    commentActions++;
                }

                if (IsBadString(data, a + 4, strCount) || IsBadString(data, a + 8, strCount))
                {
                    badActionString++;
                }

                if (data[a + 29] != 0 || data[a + 30] != 0 || data[a + 31] != 0)
                {
                    nonZeroActionPad++;
                }

                Increment(actionFlagValues, data[a + 28]);
                if ((data[a + 28] & 0xE0) != 0)
                {
                    badActionFlags++;
                }

                if (data[a + 27] > 10)
                {
                    badActionModifier++;
                    Increment(badActionModifierByType, type);
                }

                if (IsBadLocation(data, a))
                {
                    badActionLocation++;
                }

                if (IsLocationActionSecondField(type) && IsBadLocation(data, a + 20))
                {
                    badSecondLocation++;
                    Increment(badSecondLocationByType, type);
                }

                if (type == 11 && BitConverter.ToUInt32(data, a + 20) > 63)
                {
                    badPropertySlot++;
                    Increment(badPropertySlotByType, type);
                }

                if (BitConverter.ToUInt16(data, a + 24) > 232 && BitConverter.ToUInt16(data, a + 24) != UInt16.MaxValue)
                {
                    badActionUnit++;
                    Increment(badActionUnitByType, type);
                }
            }

            for (int i = 0; i < 28; i++)
            {
                byte value = data[trigger + 2372 + i];
                if (value != 0 && value != 1)
                {
                    badTriggerPlayerFlag++;
                }
            }
        }

        Console.WriteLine(name + ": badCondType=" + badConditionType + " condPad=" + nonZeroConditionPad +
            " badActType=" + badActionType + " comments=" + commentActions +
            " badActString=" + badActionString + " actPad=" + nonZeroActionPad);
        Console.WriteLine(name + ": badCondLoc=" + badConditionLocation + " badActLoc=" + badActionLocation +
            " badCondUnit=" + badConditionUnit + " badActUnit=" + badActionUnit +
            " badPlayerFlags=" + badTriggerPlayerFlag);
        Console.WriteLine(name + ": badCondFlags=" + badConditionFlags + " badActFlags=" + badActionFlags +
            " badCondComparison=" + badConditionComparison + " badActModifier=" + badActionModifier);
        Console.WriteLine(name + ": badPropertySlot=" + badPropertySlot + " badSecondLocation=" + badSecondLocation);
        PrintCounts(name + ": badCondUnitTypes", badConditionUnitByType);
        PrintCounts(name + ": badActUnitTypes", badActionUnitByType);
        PrintCounts(name + ": badActModifierTypes", badActionModifierByType);
        PrintCounts(name + ": badPropertySlotTypes", badPropertySlotByType);
        PrintCounts(name + ": badSecondLocationTypes", badSecondLocationByType);
        PrintCounts(name + ": condFlags", conditionFlagValues);
        PrintCounts(name + ": actFlags", actionFlagValues);
        Console.Write(name + ": actionTypes");
        foreach (var pair in actionTypes)
        {
            Console.Write(" " + pair.Key + ":" + pair.Value);
        }

        Console.WriteLine();
    }

    private static void PrintUnitSection(Dictionary<string, byte[]> sections)
    {
        if (!sections.ContainsKey("UNIT"))
        {
            Console.WriteLine("UNIT: missing");
            return;
        }

        byte[] data = sections["UNIT"];
        int badUnitId = 0;
        int badPlayer = 0;
        int badFlags = 0;
        int badRelation = 0;
        int allFF = 0;
        int zeroLike = 0;
        var players = new SortedDictionary<int, int>();
        var unitIds = new SortedDictionary<int, int>();

        for (int offset = 0; offset + 35 < data.Length; offset += 36)
        {
            bool recordFF = true;
            bool recordZero = true;
            for (int i = 0; i < 36; i++)
            {
                if (data[offset + i] != 0xFF)
                {
                    recordFF = false;
                }

                if (data[offset + i] != 0)
                {
                    recordZero = false;
                }
            }

            if (recordFF)
            {
                allFF++;
            }

            if (recordZero)
            {
                zeroLike++;
            }

            ushort x = BitConverter.ToUInt16(data, offset + 4);
            ushort y = BitConverter.ToUInt16(data, offset + 6);
            ushort unitId = BitConverter.ToUInt16(data, offset + 8);
            ushort relation = BitConverter.ToUInt16(data, offset + 10);
            ushort validFlags = BitConverter.ToUInt16(data, offset + 12);
            ushort propertyFlags = BitConverter.ToUInt16(data, offset + 14);
            byte player = data[offset + 16];

            Increment(unitIds, unitId);
            Increment(players, player);

            if (unitId > 232)
            {
                badUnitId++;
            }

            if (player > 11)
            {
                badPlayer++;
            }

            if (x > 8192 || y > 8192 || (validFlags & 0xFFE0) != 0 || (propertyFlags & 0xFE00) != 0)
            {
                badFlags++;
            }

            if (relation > 4 && relation != UInt16.MaxValue)
            {
                badRelation++;
            }
        }

        Console.WriteLine("UNIT: bytes=" + data.Length + " records=" + (data.Length / 36) + " rem=" + (data.Length % 36) +
            " allFF=" + allFF + " zero=" + zeroLike + " badUnitId=" + badUnitId + " badPlayer=" + badPlayer +
            " badFlags=" + badFlags + " badRelation=" + badRelation);
        PrintCounts("UNIT: players", players);
        PrintCounts("UNIT: unitIds", unitIds);
    }

    private static bool IsBadString(byte[] data, int offset, ushort strCount)
    {
        uint value = BitConverter.ToUInt32(data, offset);
        return value == UInt32.MaxValue || (strCount > 0 && value > strCount && value < 0x00010000);
    }

    private static bool IsBadLocation(byte[] data, int offset)
    {
        uint value = BitConverter.ToUInt32(data, offset);
        return value == UInt32.MaxValue || (value > 255 && value < 0x00010000);
    }

    private static bool IsLocationActionSecondField(byte type)
    {
        return type == 39 || type == 45 || type == 46 || type == 48 || type == 49 ||
            type == 50 || type == 51 || type == 52 || type == 53;
    }

    private static void PrintSectionLength(Dictionary<string, byte[]> sections, string name)
    {
        if (sections.ContainsKey(name))
        {
            Console.WriteLine(name + ": bytes=" + sections[name].Length);
        }
        else
        {
            Console.WriteLine(name + ": missing");
        }
    }

    private static void Increment(SortedDictionary<int, int> counts, int key)
    {
        if (!counts.ContainsKey(key))
        {
            counts[key] = 0;
        }

        counts[key]++;
    }

    private static void PrintCounts(string label, SortedDictionary<int, int> counts)
    {
        Console.Write(label);
        foreach (var pair in counts)
        {
            Console.Write(" " + pair.Key + ":" + pair.Value);
        }

        Console.WriteLine();
    }
}
