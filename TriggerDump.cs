using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static partial class StarcraftMapUnprotector
{
    private sealed class DecryptedTriggerInfo
    {
        public int Index;
        public uint OriginalFlag;
    }

    private static bool DumpDecryptedFreezeTriggers(string input, string outputText)
    {
        try
        {
            var stats = new Stats { FreezeBruteforceKey = true };
            List<MpqFileEntry> extraFiles;
            byte[] inputBytes = File.ReadAllBytes(input);

            uint[] freezeSeedKey, freezeDestKey;
            if (DetectFreezeProtection(inputBytes, out freezeSeedKey, out freezeDestKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = freezeSeedKey;
                stats.FreezeDestKey = freezeDestKey;
            }

            byte[] chk = LooksLikeChk(inputBytes)
                ? inputBytes
                : ExtractScenarioChk(input, stats, out extraFiles);

            List<Section> sections = ParseChk(chk);
            var grouped = GroupSectionsForTriggerDump(sections, stats);

            List<byte[]> trigList;
            if (!grouped.TryGetValue("TRIG", out trigList) || trigList.Count == 0)
            {
                Console.Error.WriteLine("TRIG section not found.");
                return false;
            }

            byte[] trigData = (byte[])trigList[0].Clone();
            int totalTriggers = trigData.Length / FreezeTrigSize;
            List<DecryptedTriggerInfo> encrypted = CollectEncryptedFreezeTriggerInfos(trigData, totalTriggers);

            if (encrypted.Count > 0)
            {
                uint recoveredKey;
                if (!TryRecoverFreezeKeyByFastBruteforce(trigData, totalTriggers, out recoveredKey))
                {
                    Console.Error.WriteLine("Freeze triggerKey recovery failed; trigger dump was not written.");
                    return false;
                }

                Console.WriteLine("  Key 0x" + recoveredKey.ToString("X8") +
                                  " validated. Decrypting TRIG records for dump...");
                DecryptAllFreezeTriggers(trigData, recoveredKey, true);
            }

            ushort strCount = ReadStringCount(grouped);
            WriteDecryptedTriggerTextDump(outputText, input, stats, trigData, encrypted, strCount);
            Console.WriteLine("Decrypted trigger dump written: " + outputText);
            Console.WriteLine("Dumped decrypted trigger records: " + encrypted.Count +
                              " / " + totalTriggers);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed: " + ex.Message);
            return false;
        }
    }

    private static bool DumpAllTriggers(string input, string outputText)
    {
        try
        {
            var stats = new Stats { FreezeBruteforceKey = true };
            List<MpqFileEntry> extraFiles;
            byte[] inputBytes = File.ReadAllBytes(input);

            uint[] freezeSeedKey, freezeDestKey;
            if (DetectFreezeProtection(inputBytes, out freezeSeedKey, out freezeDestKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = freezeSeedKey;
                stats.FreezeDestKey = freezeDestKey;
            }

            byte[] chk = LooksLikeChk(inputBytes)
                ? inputBytes
                : ExtractScenarioChk(input, stats, out extraFiles);

            List<Section> sections = ParseChk(chk);
            var grouped = GroupSectionsForTriggerDump(sections, stats);

            List<byte[]> trigList;
            if (!grouped.TryGetValue("TRIG", out trigList) || trigList.Count == 0)
            {
                Console.Error.WriteLine("TRIG section not found.");
                return false;
            }

            byte[] trigData = (byte[])trigList[0].Clone();
            int totalTriggers = trigData.Length / FreezeTrigSize;

            List<DecryptedTriggerInfo> encrypted = CollectEncryptedFreezeTriggerInfos(trigData, totalTriggers);
            uint recoveredKey = 0;
            if (encrypted.Count > 0)
            {
                if (TryRecoverFreezeKeyByFastBruteforce(trigData, totalTriggers, out recoveredKey))
                {
                    Console.WriteLine("  Key 0x" + recoveredKey.ToString("X8") +
                                      " validated. Decrypting for full dump...");
                    DecryptAllFreezeTriggers(trigData, recoveredKey, true);
                }
            }

            ushort strCount = ReadStringCount(grouped);

            var encryptedSet = new HashSet<int>();
            foreach (var info in encrypted)
                encryptedSet.Add(info.Index);

            string dir = Path.GetDirectoryName(outputText);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var w = new StreamWriter(outputText, false, new UTF8Encoding(true)))
            {
                w.WriteLine("=== FULL TRIG DUMP (ALL TRIGGERS) ===");
                w.WriteLine("Input              : " + Path.GetFullPath(input));
                w.WriteLine("Total TRIG records : " + totalTriggers);
                w.WriteLine("Encrypted records  : " + encrypted.Count);
                if (stats.IsFreezeProtected)
                {
                    w.WriteLine("Freeze05 marker    : detected");
                    w.WriteLine("seedKey            : " + FormatKey(stats.FreezeSeedKey));
                    w.WriteLine("destKey            : " + FormatKey(stats.FreezeDestKey));
                }
                w.WriteLine();

                for (int t = 0; t < totalTriggers; t++)
                {
                    int off = t * FreezeTrigSize;
                    uint execFlags = BitConverter.ToUInt32(trigData, off + 2368);

                    bool isEncrypted = encryptedSet.Contains(t);
                    bool isEud = IsFreezeEudTrigger(trigData, off);
                    bool isDisabled = true;
                    for (int p = 0; p < 28; p++)
                    {
                        if (trigData[off + 2372 + p] != 0) { isDisabled = false; break; }
                    }
                    if (execFlags != 0) isDisabled = false;

                    string tag;
                    if (isEncrypted) tag = "[DECRYPTED]";
                    else if (isDisabled) tag = "[DISABLED]";
                    else if (isEud) tag = "[EUD-VM]";
                    else tag = "[GAME]";

                    w.Write("--- T" + t + " " + tag + " flags=0x" + execFlags.ToString("X8") + " RunFor:");
                    bool any = false;
                    for (int p = 0; p < 28; p++)
                    {
                        if (trigData[off + 2372 + p] != 0)
                        {
                            w.Write(" " + TriggerDumpExecSlotName(p));
                            any = true;
                        }
                    }
                    if (!any) w.Write(" (none)");
                    w.WriteLine();

                    int condCount = 0;
                    for (int c = 0; c < 16; c++)
                        if (trigData[off + c * 20 + 15] != 0) condCount++;

                    int actCount = 0;
                    var actTypes = new List<string>();
                    for (int a = 0; a < 64; a++)
                    {
                        byte atype = trigData[off + 320 + a * 32 + 26];
                        if (atype != 0)
                        {
                            actCount++;
                            if (actTypes.Count < 12)
                                actTypes.Add(TriggerDumpActTypeName(atype));
                        }
                    }

                    w.WriteLine("  Conditions: " + condCount + "  Actions: " + actCount +
                                "  [" + string.Join(", ", actTypes.ToArray()) + "]");

                    DumpOneTriggerRecord(w, trigData, t, isEncrypted ? encrypted.Find(e => e.Index == t).OriginalFlag : execFlags, strCount);
                }
            }

            Console.WriteLine("Full trigger dump written: " + outputText);
            Console.WriteLine("Total: " + totalTriggers + " triggers");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed: " + ex.Message);
            return false;
        }
    }

    private static Dictionary<string, List<byte[]>> GroupSectionsForTriggerDump(
        List<Section> sections,
        Stats stats)
    {
        var grouped = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        foreach (Section section in sections)
        {
            if (section.Name == "SMLP")
            {
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

        MergeRepeated(grouped, "TRIG", FreezeTrigSize, stats);
        TrimRecordSection(grouped, "TRIG", FreezeTrigSize);
        return grouped;
    }

    private static List<DecryptedTriggerInfo> CollectEncryptedFreezeTriggerInfos(
        byte[] trigData,
        int totalTriggers)
    {
        var result = new List<DecryptedTriggerInfo>();
        for (int t = 0; t < totalTriggers; t++)
        {
            int offset = t * FreezeTrigSize;
            uint flag = BitConverter.ToUInt32(trigData, offset + 2368);
            if (flag >= 0x80000000u)
            {
                result.Add(new DecryptedTriggerInfo
                {
                    Index = t,
                    OriginalFlag = flag
                });
            }
        }

        return result;
    }

    private static ushort ReadStringCount(Dictionary<string, List<byte[]>> grouped)
    {
        List<byte[]> strList;
        if (grouped.TryGetValue("STR ", out strList) &&
            strList.Count > 0 &&
            strList[0].Length >= 2)
        {
            return BitConverter.ToUInt16(strList[0], 0);
        }

        return 0;
    }

    private static void WriteDecryptedTriggerTextDump(
        string outputText,
        string input,
        Stats stats,
        byte[] trigData,
        List<DecryptedTriggerInfo> decrypted,
        ushort strCount)
    {
        string dir = Path.GetDirectoryName(outputText);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var w = new StreamWriter(outputText, false, new UTF8Encoding(true)))
        {
            w.WriteLine("=== FREEZE05 DECRYPTED TRIG DUMP ===");
            w.WriteLine("Input              : " + Path.GetFullPath(input));
            w.WriteLine("Total TRIG records : " + (trigData.Length / FreezeTrigSize));
            w.WriteLine("Decrypted records  : " + decrypted.Count);
            if (stats.IsFreezeProtected)
            {
                w.WriteLine("Freeze05 marker    : detected");
                w.WriteLine("seedKey            : " + FormatKey(stats.FreezeSeedKey));
                w.WriteLine("destKey            : " + FormatKey(stats.FreezeDestKey));
            }
            else
            {
                w.WriteLine("Freeze05 marker    : not detected");
            }
            w.WriteLine();

            if (decrypted.Count == 0)
            {
                w.WriteLine("(No encrypted Freeze05 TRIG records were found. Use the original protected map, not an already-decrypted output, to dump only decrypted records.)");
                return;
            }

            foreach (DecryptedTriggerInfo info in decrypted)
            {
                DumpOneTriggerRecord(w, trigData, info.Index, info.OriginalFlag, strCount);
            }
        }
    }

    private static void DumpOneTriggerRecord(
        TextWriter w,
        byte[] data,
        int triggerIndex,
        uint originalFlag,
        ushort strCount)
    {
        int off = triggerIndex * FreezeTrigSize;
        uint execFlags = BitConverter.ToUInt32(data, off + 2368);

        w.WriteLine("============================================================");
        w.WriteLine("Trigger #" + triggerIndex + "  byteOffset=0x" + off.ToString("X6"));
        w.WriteLine("OriginalEncryptedFlag=0x" + originalFlag.ToString("X8") +
                    "  DecryptedExecFlags=0x" + execFlags.ToString("X8"));
        w.Write("RunFor:");
        bool any = false;
        for (int i = 0; i < 28; i++)
        {
            if (data[off + 2372 + i] != 0)
            {
                w.Write(" " + TriggerDumpExecSlotName(i));
                any = true;
            }
        }
        if (!any) w.Write(" (none)");
        w.WriteLine();
        w.WriteLine();

        w.WriteLine("Conditions:");
        bool hadCond = false;
        for (int i = 0; i < 16; i++)
        {
            int c = off + i * 20;
            byte type = data[c + 15];
            if (type == 0) continue;

            hadCond = true;
            uint loc = BitConverter.ToUInt32(data, c + 0);
            uint val = BitConverter.ToUInt32(data, c + 4);
            uint grp = BitConverter.ToUInt32(data, c + 8);
            ushort unit = BitConverter.ToUInt16(data, c + 12);
            byte cmp = data[c + 14];
            byte res = data[c + 16];
            byte flags = data[c + 17];
            ushort pad = BitConverter.ToUInt16(data, c + 18);

            string condEudText = "";
            if (type == 15 && (grp > 27 || grp == 13))
            {
                uint condEpd = unchecked(grp + (uint)unit * 12u);
                uint condMem = unchecked(0x0058A364u + condEpd * 4u);
                string condRegion = DescribeScMemoryAddress(condMem);
                condEudText = " EPD=" + condEpd + " mem=0x" + condMem.ToString("X8");
                if (condRegion != null) condEudText += " [" + condRegion + "]";
                if (grp == 13) condEudText += " (via CP)";
            }

            w.WriteLine(string.Format(
                "  [{0:D2}] {1,-24} type={2,3} loc={3,5} val={4,10} group={5,-16} unit={6,-5} cmp={7,-8} res={8} flags=0x{9:X2}{10}{11}",
                i,
                TriggerDumpCondTypeName(type),
                type,
                loc,
                val,
                TriggerDumpPlayerName(grp),
                unit == 0xFFFF ? "Any" : unit.ToString(),
                TriggerDumpCmpName(cmp),
                res,
                flags,
                pad != 0 ? " pad=0x" + pad.ToString("X4") : "",
                condEudText));
        }
        if (!hadCond) w.WriteLine("  (none)");
        w.WriteLine();

        w.WriteLine("Actions:");
        bool hadAct = false;
        for (int i = 0; i < 64; i++)
        {
            int a = off + 320 + i * 32;
            byte type = data[a + 26];
            if (type == 0) continue;

            hadAct = true;
            uint loc = BitConverter.ToUInt32(data, a + 0);
            uint str = BitConverter.ToUInt32(data, a + 4);
            uint wav = BitConverter.ToUInt32(data, a + 8);
            uint time = BitConverter.ToUInt32(data, a + 12);
            uint player = BitConverter.ToUInt32(data, a + 16);
            uint amount = BitConverter.ToUInt32(data, a + 20);
            ushort unit = BitConverter.ToUInt16(data, a + 24);
            byte modifier = data[a + 27];
            byte flags = data[a + 28];
            uint pad = (uint)(data[a + 29] | (data[a + 30] << 8) | (data[a + 31] << 16));

            string playerText = TriggerDumpPlayerName(player);
            string eudText = "";
            if (type == 45)
            {
                uint epd = unchecked(player + (uint)unit * 12u);
                uint mem = unchecked(0x0058A364u + epd * 4u);
                if (player > 27 || player == 13)
                {
                    string region = DescribeScMemoryAddress(mem);
                    eudText = " EPD=" + epd + " mem=0x" + mem.ToString("X8");
                    if (region != null) eudText += " [" + region + "]";
                    if (player == 13) eudText += " (via CP)";
                }
            }

            string strNote = "";
            if (strCount > 0 && str > strCount && str < 0x00010000u)
            {
                strNote = " strOutOfRange";
            }

            w.WriteLine(string.Format(
                "  [{0:D2}] {1,-24} type={2,3} loc={3,5} str={4,5}{5} wav={6,5} time={7,8} player={8,-16} amount={9,10} unit={10,-5} mod={11,-8} flags=0x{12:X2}{13}{14}",
                i,
                TriggerDumpActTypeName(type),
                type,
                loc,
                str,
                strNote,
                wav,
                time,
                playerText,
                amount,
                unit == 0xFFFF ? "Any" : unit.ToString(),
                TriggerDumpModName(modifier),
                flags,
                pad != 0 ? " pad=0x" + pad.ToString("X6") : "",
                eudText));
        }
        if (!hadAct) w.WriteLine("  (none)");
        w.WriteLine();
    }

    private static string TriggerDumpCondTypeName(byte t)
    {
        string[] n = {
            "NoCondition", "CountdownTimer", "Command", "Bring", "Accumulate", "Kill",
            "CommandTheMost", "CommandTheMostAt", "MostKills", "HighestScore", "MostResources",
            "Switch", "ElapsedTime", "MissionBriefing", "Opponents", "Deaths",
            "CommandLeastAt", "CommandTheLeast", "LeastKills", "LowestScore", "LeastResources",
            "Score", "Always", "Never"
        };
        return t < n.Length ? n[t] : "Cond(" + t + ")";
    }

    private static string TriggerDumpActTypeName(byte t)
    {
        string[] n = {
            "None", "Victory", "Defeat", "PreserveTrigger", "Wait", "PauseGame",
            "UnpauseGame", "Transmission", "PlayWAV", "DisplayText", "CenterView",
            "CreateUnitWithProps", "SetMissionObjectives", "SetSwitch", "SetCountdownTimer",
            "RunAIScript", "RunAIScriptAtLoc", "LeaderboardCtrlAtLoc", "LeaderboardCtrl",
            "LeaderboardResources", "LeaderboardKills", "LeaderboardPoints", "KillUnit",
            "KillUnitAtLoc", "RemoveUnit", "RemoveUnitAtLoc", "SetResources", "SetScore",
            "MinimapPing", "TalkingPortrait", "MuteUnitSpeech", "UnmuteUnitSpeech",
            "LeaderboardComputerPlayers", "LeaderboardGoalCtrlAtLoc", "LeaderboardGoalCtrl",
            "LeaderboardGoalResources", "LeaderboardGoalKills", "LeaderboardGoalPoints",
            "MoveLocation", "MoveUnit", "LeaderboardGreed", "SetNextScenario",
            "SetDoodadState", "SetInvincibility", "CreateUnit", "SetDeaths",
            "Order", "Comment", "GiveUnits", "ModifyUnitHP", "ModifyUnitEnergy",
            "ModifyUnitShield", "ModifyUnitResource", "ModifyUnitHangar", "PauseTimer",
            "UnpauseTimer", "Draw", "SetAllianceStatus"
        };
        return t < n.Length ? n[t] : "Action(" + t + ")";
    }

    private static string TriggerDumpCmpName(byte c)
    {
        if (c == 0) return "AtLeast";
        if (c == 1) return "AtMost";
        if (c == 10) return "Exactly";
        return "Cmp(" + c + ")";
    }

    private static string TriggerDumpModName(byte m)
    {
        if (m == 7) return "SetTo";
        if (m == 8) return "Add";
        if (m == 9) return "Subtract";
        if (m == 4) return "Set";
        if (m == 5) return "Clear";
        if (m == 6) return "Toggle";
        if (m == 11) return "Random";
        if (m == 0) return "-";
        return "Mod(" + m + ")";
    }

    private static string TriggerDumpExecSlotName(int slot)
    {
        string[] n = {
            "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9", "P10", "P11", "P12",
            "Slot12", "Force1", "Force2", "Force3", "Force4", "AllPlayers",
            "CurrentPlayer", "Foes", "Allies", "Neutral", "AllPlayers2", "NoPeers",
            "PlayersByRace", "Enemies", "NonAlliedVP", "Slot27"
        };
        return slot < n.Length ? n[slot] : "Slot" + slot;
    }

    private static string DescribeScMemoryAddress(uint addr)
    {
        if (addr >= 0x006509B0u && addr < 0x006509B4u) return "CurrentPlayer";
        if (addr >= 0x0058A364u && addr < 0x0058A364u + 12 * 228 * 4) return "DeathTable";
        if (addr >= 0x0059CCA8u && addr < 0x006283E8u) return "UnitTable";
        if (addr >= 0x00628430u && addr < 0x00628430u + 0x2400 * 400) return "TriggerList";
        if (addr >= 0x0057F0F0u && addr < 0x0058A364u) return "UnitNodeTable";
        if (addr >= 0x006284B8u && addr < 0x006284B8u + 4) return "TrigNextPtr";
        if (addr >= 0x0051A280u && addr < 0x0051A280u + 0x10000) return "StringTable";
        if (addr >= 0x006D1238u && addr < 0x006D1238u + 12 * 4) return "PlayerResources";
        if (addr >= 0x0057EE60u && addr < 0x0057EE60u + 12) return "PlayerForce";
        if (addr >= 0x0068C100u && addr < 0x0068C100u + 256) return "SwitchTable";
        if (addr >= 0x0051CE98u && addr < 0x0051CE98u + 0x8000) return "MapTileData";
        if (addr >= 0x006D0F18u && addr < 0x006D0F18u + 24 * 4) return "PlayerOre/Gas";
        if (addr >= 0x00512680u && addr < 0x00512680u + 256 * 4) return "LocationTable";
        if (addr >= 0x0066FF78u && addr < 0x0066FF78u + 0x150 * 12) return "SpriteTable";
        if (addr >= 0x006BEE80u && addr < 0x006BEE80u + 256) return "DisplayText";
        if (addr >= 0x006C9C18u && addr < 0x006C9C18u + 4) return "ScreenX";
        if (addr >= 0x006C9C1Cu && addr < 0x006C9C1Cu + 4) return "ScreenY";
        if (addr >= 0x0057F1ECu && addr < 0x0057F1ECu + 12 * 4) return "SupplyUsed";
        if (addr >= 0x0057F244u && addr < 0x0057F244u + 12 * 4) return "SupplyMax";
        if (addr >= 0x006509A0u && addr < 0x006509A0u + 4) return "CountdownTimer";
        if (addr >= 0x0058D740u && addr < 0x0058D740u + 228 * 4) return "CompletedUnitCount";
        if (addr >= 0x0058DC60u && addr < 0x0058DC60u + 12 * 4) return "TotalUnitScore";
        // death table(0x0058A364..0x0058CE24) 바로 뒤에 이어지는 유닛-인덱스 게임상태
        // 테이블군(유닛 카운트/스코어/생산 등). 개별 테이블 경계는 미확정이지만 여러 EUD
        // 맵에서 이 구간을 반복적으로 긁어쓰는 게 관측됨 → 한 덩어리로 식별.
        if (addr >= 0x0058CE24u && addr < 0x0058F000u) return "UnitStateTables";
        return null;
    }

    private static string TriggerDumpPlayerName(uint p)
    {
        string[] n = {
            "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9", "P10", "P11", "P12",
            "NeutralAll", "CurrentPlayer", "Foes", "Allies", "Neutral", "AllPlayers",
            "Force1", "Force2", "Force3", "Force4", "NonAlliedVP", "Slot22", "Slot23",
            "Slot24", "Slot25", "Slot26"
        };
        if (p < (uint)n.Length) return n[(int)p];
        if (p == 0xFFFFFFFFu) return "Any/None";
        return "0x" + p.ToString("X8");
    }
}
