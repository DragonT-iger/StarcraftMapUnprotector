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

    private static byte[] BuildStaticLv2Chk(string input, byte[] inputBytes, byte[] chk, Stats stats)
    {
        RunLv2Diagnostics(input, inputBytes, chk, stats);

        throw new NotSupportedException(
            "Lv2 static restore is stopped before writing output: FreezeOffsetDecryptor still needs " +
            "oJumperArray/nextptr extraction. The old in-place Lv2 writer is intentionally disabled " +
            "because it produced maps that ScmDraft could not open or whose triggers did not run.");
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
