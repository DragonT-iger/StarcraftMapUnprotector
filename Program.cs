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

    // When set, apply this runtime memory dump (from freeze_dump.lua) instead of brute-force.
    private static string ApplyDumpPath;

    // When set, recover the Freeze trigger key by comparing CHK with this runtime dump.
    private static string FreezeRecoverDumpPath;

    // When set, brute-force the final Freeze triggerKey directly from encrypted TRIG data.
    private static bool FreezeBruteforceKey;

    // When set, output a Lv2 (game-playable) file through the static Freeze restore pipeline.
    private static bool Lv2Mode;

    // When set, print Lv2 static-restore diagnostics without writing an output file.
    private static bool Lv2DiagMode;

    // Experimental Lv2 mode: clear encrypted trigger exec flags after decrypting bodies.
    private static bool Lv2ClearExecFlags;

    // Experimental Lv2 mode: disable SetDeaths-only Freeze VM/control triggers after decrypting bodies.
    private static bool Lv2DisableVm;

    // Experimental Lv2 mode: force decrypted encrypted triggers to run for player slots 1-8.
    private static bool Lv2ForcePlayers;

    // When set, only dump Freeze05-decrypted TRIG records to this text file.
    private static string DumpDecryptedTriggersPath;

    // When set, dump ALL TRIG records (not just encrypted) to this text file.
    private static string DumpAllTriggersPath;

    // When set, write a human-facing map analysis report (Markdown) to this file.
    private static string ReportPath;

    // When set, write a per-address EUD access histogram (CSV) to this file.
    private static string EudHistogramPath;

    // When set, inject a minimal sound-effect trigger (in-place, size-invariant) and write here.
    private static string InjectSoundPath;

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
        public bool IsEudMap;
        public int EudAddressedTriggers;
        public uint[] FreezeSeedKey;
        public uint[] FreezeDestKey;
        public int RemovedFreezeEudTriggers;
        public int DecryptedFreezeTriggers;
        public string FreezeDumpPath;       // path for EUD trigger CSV debug dump
        public string FreezeApplyDumpPath;  // path for CE runtime binary dump (--apply-dump)
        public bool FreezeBruteforceKey;    // enable file-only triggerKey brute-force
        public bool Lv2Mode;               // output game-playable file via static Freeze restore
        public bool Lv2DiagMode;           // print static Freeze restore diagnostics only
        public bool Lv2ClearExecFlags;     // clear decrypted Freeze trigger exec flags to force execution
        public bool Lv2DisableVm;          // disable Freeze VM/control triggers after decrypting bodies
        public bool Lv2ForcePlayers;       // force decrypted Freeze triggers to execute for players 1-8
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

    private sealed class MpqTableLocation
    {
        public MpqHeaderCandidate Header;
        public int HashOffset;
        public int BlockOffset;
        public HashTable[] Hashes;
        public BlockTable[] Blocks;
        public int ScenarioBlockIndex;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        bool pauseOnExit = true;
        string applyDumpPath = null;
        string freezeRecoverDumpPath = null;
        var argList = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-pause")
            {
                pauseOnExit = false;
            }
            else if (args[i] == "--raw-chk")
            {
                RawChkMode = true;
            }
            else if (args[i] == "--apply-dump" && i + 1 < args.Length)
            {
                applyDumpPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--freeze-recover-key" && i + 1 < args.Length)
            {
                freezeRecoverDumpPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--freeze-bruteforce-key")
            {
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--lv2")
            {
                Lv2Mode = true;
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--lv2-diag")
            {
                Lv2DiagMode = true;
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--lv2-clear-flags")
            {
                Lv2ClearExecFlags = true;
            }
            else if (args[i] == "--lv2-disable-vm")
            {
                Lv2DisableVm = true;
            }
            else if (args[i] == "--lv2-force-players")
            {
                Lv2ForcePlayers = true;
            }
            else if (args[i] == "--dump-decrypted-triggers" && i + 1 < args.Length)
            {
                DumpDecryptedTriggersPath = Path.GetFullPath(args[++i]);
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--dump-all-triggers" && i + 1 < args.Length)
            {
                DumpAllTriggersPath = Path.GetFullPath(args[++i]);
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--report" && i + 1 < args.Length)
            {
                ReportPath = Path.GetFullPath(args[++i]);
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--eud-histogram" && i + 1 < args.Length)
            {
                EudHistogramPath = Path.GetFullPath(args[++i]);
                FreezeBruteforceKey = true;
            }
            else if (args[i] == "--inject-sound" && i + 1 < args.Length)
            {
                InjectSoundPath = Path.GetFullPath(args[++i]);
            }
            else
            {
                argList.Add(args[i]);
            }
        }
        args = argList.ToArray();
        ApplyDumpPath = applyDumpPath;
        FreezeRecoverDumpPath = freezeRecoverDumpPath;

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
            Console.WriteLine("Options:");
            Console.WriteLine("  --no-pause            Close immediately when finished.");
            Console.WriteLine("  --raw-chk             Repack CHK as-is without normalization.");
            Console.WriteLine("  --apply-dump <file>   Legacy diagnostic: apply a runtime trigger dump.");
            Console.WriteLine("                        Not reliable for Lv2 because Freeze re-encrypts every frame.");
            Console.WriteLine("  --freeze-bruteforce-key");
            Console.WriteLine("                        Brute-force the final Freeze triggerKey from encrypted TRIG data.");
            Console.WriteLine("  --lv2                 Output a game-playable (Lv2) file using static Freeze05 restore.");
            Console.WriteLine("  --lv2-diag            Print static Freeze05 restore diagnostics without writing output.");
            Console.WriteLine("  --lv2-clear-flags     Experimental: clear decrypted Freeze trigger exec flags to 0.");
            Console.WriteLine("  --lv2-disable-vm      Experimental: disable SetDeaths-only Freeze VM/control triggers.");
            Console.WriteLine("  --lv2-force-players   Experimental: set decrypted Freeze trigger player slots 1-8.");
            Console.WriteLine("  --dump-decrypted-triggers <txt>");
            Console.WriteLine("                        Dump only Freeze05-decrypted TRIG records as text.");
            Console.WriteLine("  --report <md>         Write a human-facing map analysis report (Markdown).");
            Console.WriteLine("  --eud-histogram <csv> Dump per-address EUD access counts (analysis; aggregate across maps).");
            Console.WriteLine("  --inject-sound <out>  Inject a minimal PlayWAV trigger in-place (size-invariant) and exit.");
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

        if (!string.IsNullOrEmpty(DumpDecryptedTriggersPath))
        {
            return DumpDecryptedFreezeTriggers(input, DumpDecryptedTriggersPath) ? 0 : 3;
        }

        if (!string.IsNullOrEmpty(DumpAllTriggersPath))
        {
            return DumpAllTriggers(input, DumpAllTriggersPath) ? 0 : 3;
        }

        if (!string.IsNullOrEmpty(ReportPath))
        {
            return GenerateMapReport(input, ReportPath) ? 0 : 3;
        }

        if (!string.IsNullOrEmpty(EudHistogramPath))
        {
            return DumpEudHistogram(input, EudHistogramPath) ? 0 : 3;
        }

        if (!string.IsNullOrEmpty(InjectSoundPath))
        {
            return InjectSound(input, InjectSoundPath) ? 0 : 3;
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
            var stats = new Stats
            {
                FreezeApplyDumpPath = ApplyDumpPath,
                FreezeBruteforceKey = FreezeBruteforceKey,
                Lv2Mode = Lv2Mode,
                Lv2DiagMode = Lv2DiagMode,
                Lv2ClearExecFlags = Lv2ClearExecFlags,
                Lv2DisableVm = Lv2DisableVm,
                Lv2ForcePlayers = Lv2ForcePlayers
            };
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

            if (Lv2DiagMode)
            {
                RunLv2Diagnostics(input, inputBytes, chk, stats);
                usedDeepRecovery = stats.MpqDeepRecoveryUsed > 0;
                return true;
            }

            bool lv2InPlace = Lv2Mode && stats.IsFreezeProtected && !LooksLikeChk(inputBytes);
            byte[] normalized = RawChkMode
                ? chk
                : lv2InPlace
                    ? BuildStaticLv2Chk(input, inputBytes, chk, stats)
                    : BuildNormalizedChk(sections, stats);
            DumpChkSections(normalized);
            if (lv2InPlace)
            {
                WriteLv2Mpq(input, output, normalized, BuildFreezeBlob(stats));
            }
            else
            {
                WriteStandardMpq(output, normalized, extraFiles, BuildFreezeBlob(stats));
            }
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
            Console.WriteLine("EUD map detected         : " + (stats.IsEudMap ? "YES" : "no") +
                              (stats.EudAddressedTriggers > 0 ? " (" + stats.EudAddressedTriggers + " trigger(s))" : ""));
            Console.WriteLine("Freeze05 EUD triggers disabled: " + stats.RemovedFreezeEudTriggers);
            Console.WriteLine("Freeze05 triggers decrypted : " + stats.DecryptedFreezeTriggers);
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

        if (stats.RemovedFreezeEudTriggers > 0)
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
