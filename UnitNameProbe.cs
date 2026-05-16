using System;
using System.Collections.Generic;
using System.Text;
using TkMPQLib;

internal static class UnitNameProbe
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: UnitNameProbe.exe <map.scx>");
            return 1;
        }

        Console.OutputEncoding = Encoding.UTF8;
        byte[] chk = ReadScenarioChk(args[0]);
        Dictionary<string, byte[]> sections = Parse(chk);
        byte[] str = sections["STR "];
        byte[] unix;
        if (!sections.TryGetValue("UNIx", out unix))
        {
            Console.WriteLine("UNIx=missing");
            return 0;
        }

        int named = 0;
        for (int i = 0, offset = 14 * 228; i < 228 && offset + 1 < unix.Length; i++, offset += 2)
        {
            ushort stringId = BitConverter.ToUInt16(unix, offset);
            string value = GetString(str, stringId);
            if (value.Length == 0)
            {
                continue;
            }

            named++;
            if (named <= 25)
            {
                Console.WriteLine(i + ": " + stringId + " = " + value);
            }
        }

        Console.WriteLine("STR count=" + BitConverter.ToUInt16(str, 0) + " UNIx named=" + named);
        return 0;
    }

    private static string GetString(byte[] str, ushort id)
    {
        if (id == 0 || id * 2 + 1 >= str.Length)
        {
            return "";
        }

        ushort offset = BitConverter.ToUInt16(str, id * 2);
        if (offset == 0 || offset >= str.Length || str[offset] == 0)
        {
            return "";
        }

        int end = offset;
        while (end < str.Length && str[end] != 0)
        {
            end++;
        }

        return Encoding.GetEncoding(949).GetString(str, offset, end - offset);
    }

    private static Dictionary<string, byte[]> Parse(byte[] chk)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int p = 0;
        while (p + 8 <= chk.Length)
        {
            string name = Encoding.ASCII.GetString(chk, p, 4);
            int length = BitConverter.ToInt32(chk, p + 4);
            p += 8;
            if (length < 0 || p + length > chk.Length)
            {
                break;
            }

            byte[] data = new byte[length];
            Buffer.BlockCopy(chk, p, data, 0, length);
            p += length;
            result[name] = data;
        }

        return result;
    }

    private static byte[] ReadScenarioChk(string path)
    {
        string[] names =
        {
            "staredit\\scenario.chk",
            "staredit/scenario.chk",
            "scenario.chk"
        };

        Locale[] locales =
        {
            Locale.English, Locale.Neutral, Locale.Korean, Locale.Japanese, Locale.Chinese
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

        throw new InvalidOperationException("scenario.chk not found");
    }
}
