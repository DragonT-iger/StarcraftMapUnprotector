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

    // When set, apply this runtime memory dump (from freeze_dump.lua) instead of brute-force.
    private static string ApplyDumpPath;

    // When set, recover the Freeze trigger key by comparing CHK with this runtime dump.
    private static string FreezeRecoverDumpPath;

    // When set, brute-force the final Freeze triggerKey directly from encrypted TRIG data.
    private static bool FreezeBruteforceKey;

    // When set, output a game-playable file through the static Freeze restore pipeline.
    private static bool FreezeMode;

    // When set, print static Freeze restore diagnostics without writing an output file.
    private static bool FreezeDiagMode;

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
        public bool FreezeMode;            // output game-playable file via static Freeze restore
        public bool FreezeDiagMode;        // print static Freeze restore diagnostics only
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
            else if (args[i] == "--freeze")
            {
                FreezeMode = true;
            }
            else if (args[i] == "--diag")
            {
                FreezeDiagMode = true;
            }
            else if (args[i] == "--dump-decrypted-triggers" && i + 1 < args.Length)
            {
                DumpDecryptedTriggersPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--dump-all-triggers" && i + 1 < args.Length)
            {
                DumpAllTriggersPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--report" && i + 1 < args.Length)
            {
                ReportPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--eud-histogram" && i + 1 < args.Length)
            {
                EudHistogramPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "--inject-sound" && i + 1 < args.Length)
            {
                InjectSoundPath = Path.GetFullPath(args[++i]);
            }
            else if (args[i] == "-h" || args[i] == "--help")
            {
                argList.Add(args[i]);
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Unknown option: " + args[i]);
                return 2;
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
            Console.WriteLine("  --apply-dump <file>   Legacy diagnostic: apply a runtime trigger dump.");
            Console.WriteLine("                        Not reliable for Freeze because it re-encrypts every frame.");
            Console.WriteLine("  --freeze-bruteforce-key");
            Console.WriteLine("                        Enable the optional 2^32 key-search fallback.");
            Console.WriteLine("  --freeze              Force Freeze restore; protected maps are auto-detected by default.");
            Console.WriteLine("  --diag                Dry-run Freeze VM, key, defang, and MPQ readback checks.");
            Console.WriteLine("  --dump-decrypted-triggers <txt>");
            Console.WriteLine("                        Dump only Freeze05-decrypted TRIG records as text.");
            Console.WriteLine("  --report <md>         Write a human-facing map analysis report (Markdown).");
            Console.WriteLine("  --eud-histogram <csv> Dump per-address EUD access counts (analysis; aggregate across maps).");
            Console.WriteLine("  --inject-sound <out>  Inject a minimal PlayWAV trigger in-place (size-invariant) and exit.");
            return 0;
        }

        string input = Path.GetFullPath(args[0]);
        bool automaticOutputName = args.Length < 2;
        string output = !automaticOutputName
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
        return UnprotectOne(input, output, automaticOutputName, out usedDeepRecovery) ? 0 : 3;
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
            .Where(file => file.Name.IndexOf(".unprotected.", StringComparison.OrdinalIgnoreCase) < 0)
            .Where(file => file.Name.IndexOf(".unfreezed.", StringComparison.OrdinalIgnoreCase) < 0)
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
            string output = Path.Combine(outputDir, map.Name);

            Console.WriteLine("============================================================");
            Console.WriteLine(map.Name + " -> automatic output name");
            Console.WriteLine("============================================================");

            bool usedDeepRecovery;
            if (UnprotectOne(map.FullName, output, true, out usedDeepRecovery))
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

    private static bool UnprotectOne(
        string input,
        string output,
        bool automaticOutputName,
        out bool usedDeepRecovery)
    {
        usedDeepRecovery = false;
        try
        {
            var stats = new Stats
            {
                FreezeApplyDumpPath = ApplyDumpPath,
                FreezeBruteforceKey = FreezeBruteforceKey,
                FreezeMode = FreezeMode,
                FreezeDiagMode = FreezeDiagMode,
            };
            List<MpqFileEntry> extraFiles;
            byte[] inputBytes = File.ReadAllBytes(input);
            if ((FreezeMode || FreezeDiagMode) && LooksLikeChk(inputBytes))
                throw new InvalidDataException("--freeze and --diag require an MPQ .scx/.scm input, not raw CHK.");

            uint[] freezeSeedKey, freezeDestKey;
            if (DetectFreezeProtection(inputBytes, out freezeSeedKey, out freezeDestKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = freezeSeedKey;
                stats.FreezeDestKey = freezeDestKey;
            }
            stats.FreezeMode = stats.FreezeMode || stats.IsFreezeProtected;

            if (automaticOutputName)
            {
                string outputDirectory = Path.GetDirectoryName(output) ?? Path.GetDirectoryName(input) ?? ".";
                output = BuildAutomaticOutputPath(input, outputDirectory, stats.FreezeMode);
            }
            stats.FreezeDumpPath = output + ".freeze_dump.csv";

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

            if (FreezeDiagMode)
            {
                RunFreezeDiagnostics(input, inputBytes, chk, stats);
                usedDeepRecovery = stats.MpqDeepRecoveryUsed > 0;
                return true;
            }

            bool freezeInPlace = stats.FreezeMode && !LooksLikeChk(inputBytes);
            byte[] normalized = freezeInPlace
                ? BuildStaticFreezeChk(input, inputBytes, chk, stats)
                : BuildNormalizedChk(sections, stats);
            DumpChkSections(normalized);
            if (freezeInPlace)
            {
                WriteFreezeMpq(input, output, chk, normalized, BuildFreezeBlob(stats));
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
            if (string.Equals(Environment.GetEnvironmentVariable("SCMU_DEBUG"), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(ex.ToString());
            }
            return false;
        }
    }

    private static string BuildAutomaticOutputPath(string input, string outputDirectory, bool freezeMode)
    {
        string extension = Path.GetExtension(input);
        string baseName = Path.GetFileNameWithoutExtension(input);
        string suffix = freezeMode ? ".unfreezed" : ".unprotected";
        string outputName = baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? baseName + extension
            : baseName + suffix + extension;
        return Path.Combine(outputDirectory, outputName);
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
