using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TkMPQLib;

internal static partial class StarcraftMapUnprotector
{
    private const int UnitTypeCount = 228;
    private const int UnitNameStringOffset = 14 * UnitTypeCount;

    // When set, the extracted scenario.chk is repackaged as-is with no normalization.
    private static bool RawChkMode;

    private static readonly string[] CanonicalOrder =
    {
        "VER ", "TYPE", "IVE2", "VCOD", "IOWN", "OWNR", "SIDE", "COLR",
        "ERA ", "DIM ", "MTXM", "TILE", "ISOM", "UNIT", "PUNI", "UNIx",
        "PUPx", "UPGx", "DD2 ", "THG2", "MASK", "MRGN", "STR ", "STRx", "SPRP",
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
        public bool IsFreezeProtected;
        public uint[] FreezeSeedKey;
        public uint[] FreezeDestKey;
        public int RemovedFreezeEudTriggers;
        public string FreezeDumpPath;
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

            if (arg == "--raw-chk")
            {
                RawChkMode = true;
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
            Console.WriteLine("No arguments: unprotects every .scx/.scm file in Maps\\Originals to Maps\\Outputs.");
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

        string[] allowedExtensions = { ".scx", ".scm" };
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

            uint[] freezeSeedKey, freezeDestKey;
            if (DetectFreezeProtection(inputBytes, out freezeSeedKey, out freezeDestKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = freezeSeedKey;
                stats.FreezeDestKey = freezeDestKey;
                stats.FreezeDumpPath = output + ".freeze_dump.csv";
            }

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

            byte[] normalized = RawChkMode ? chk : BuildNormalizedChk(sections, stats);
            DumpChkSections(normalized);
            WriteStandardMpq(output, normalized, extraFiles, BuildFreezeBlob(stats));
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
            if (stats.IsFreezeProtected)
            {
                Console.WriteLine("Freeze05 protection      : DETECTED");
                Console.WriteLine("  seedKey: " + FormatKey(stats.FreezeSeedKey));
                Console.WriteLine("  destKey: " + FormatKey(stats.FreezeDestKey));
            }
            Console.WriteLine("Freeze05 EUD triggers removed: " + stats.RemovedFreezeEudTriggers);
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

    private static void DumpChkSections(byte[] chk)
    {
        int pos = 0;
        Console.WriteLine("CHK sections (" + chk.Length + " bytes total):");
        while (pos + 8 <= chk.Length)
        {
            string name = Encoding.ASCII.GetString(chk, pos, 4);
            uint sz = BitConverter.ToUInt32(chk, pos + 4);
            if (sz > (uint)(chk.Length - pos - 8)) break;
            string note = "";
            if (name == "UNIS") note = "  <-- vanilla unit settings";
            else if (name == "UNIx") note = "  <-- BW unit settings";
            else if (name == "TRIG") note = "  (" + (sz / 2400) + " triggers)";
            else if (name == "UPGS") note = "  <-- SC upgrade settings";
            else if (name == "TECS") note = "  <-- SC tech settings";
            Console.WriteLine("  " + name + "  " + sz + note);
            if ((name == "UPGR" || name == "UPGx" || name == "UPGS") && sz > 0)
            {
                int dumpLen = (int)Math.Min(sz, 192);
                for (int row = 0; row < dumpLen; row += 48)
                {
                    int rowEnd = Math.Min(row + 48, dumpLen);
                    var sb = new StringBuilder("    [" + row + ".." + (rowEnd - 1) + "] ");
                    for (int k = row; k < rowEnd; k++)
                        sb.AppendFormat("{0:X2} ", chk[pos + 8 + k]);
                    Console.WriteLine(sb.ToString());
                }
            }
            pos += 8 + (int)sz;
        }
    }

    private static byte[] BuildFreezeBlob(Stats stats)
    {
        if (!stats.IsFreezeProtected || stats.FreezeSeedKey == null || stats.FreezeDestKey == null)
        {
            return null;
        }

        byte[] blob = new byte[48];
        for (int j = 0; j < 4; j++)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(stats.FreezeSeedKey[j]), 0, blob, j * 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(stats.FreezeDestKey[j]), 0, blob, 32 + j * 4, 4);
        }
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("freeze05 protect"), 0, blob, 16, 16);
        return blob;
    }

    private static string FormatKey(uint[] key)
    {
        if (key == null) return "(none)";
        return string.Join(" ", key.Select(k => k.ToString("X8")));
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
}
