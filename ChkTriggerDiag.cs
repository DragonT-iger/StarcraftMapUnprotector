using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TkMPQLib;

internal static class ChkTriggerDiag
{
    // Writes to both Console.Out and a file simultaneously.
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override Encoding Encoding { get { return _a.Encoding; } }
        public override void Write(char v) { _a.Write(v); _b.Write(v); }
        public override void Write(string v) { _a.Write(v); _b.Write(v); }
        public override void Write(char[] buf, int idx, int cnt) { _a.Write(buf, idx, cnt); _b.Write(buf, idx, cnt); }
        public override void WriteLine() { _a.WriteLine(); _b.WriteLine(); }
        public override void WriteLine(string v) { _a.WriteLine(v); _b.WriteLine(v); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: Diag.exe <map.scx|.scm|.chk> [output.txt]");
            Console.WriteLine("  Default output: <map>_trigdiag.txt next to the input file");
            return 1;
        }

        byte[] chk = ReadScenarioChk(args[0]);
        if (chk == null)
        {
            Console.Error.WriteLine("ERROR: could not read scenario.chk from: " + args[0]);
            return 2;
        }

        string fullInputPath = Path.GetFullPath(args[0]);
        string outPath;
        if (args.Length > 1)
        {
            outPath = args[1];
        }
        else
        {
            string dir = Path.GetDirectoryName(fullInputPath);
            string fileBase = Path.GetFileNameWithoutExtension(fullInputPath);
            outPath = Path.Combine(dir, fileBase + "_trigdiag.txt");
        }

        var sections = ParseChk(chk);
        ushort strCount = 0;
        if (sections.ContainsKey("STR ") && sections["STR "].Length >= 2)
            strCount = BitConverter.ToUInt16(sections["STR "], 0);

        var origOut = Console.Out;
        using (var fw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
        {
            var w = new TeeWriter(origOut, fw);
            w.WriteLine("=== EUD TRIGGER DIAGNOSTIC REPORT ===");
            w.WriteLine("Map  : " + fullInputPath);
            w.WriteLine("Date : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            w.WriteLine("CHK  : " + chk.Length + " bytes  string_count=" + strCount);
            w.WriteLine();
            PrintSectionIndex(w, sections);
            w.WriteLine();
            PrintSectionLength(w, sections, "UPRP");
            PrintSectionLength(w, sections, "UPUS");
            PrintSectionLength(w, sections, "MRGN");
            w.WriteLine();
            PrintUnitSection(w, sections);
            w.WriteLine();
            AnalyzeTrigSection(w, sections, "TRIG", strCount);
            AnalyzeTrigSection(w, sections, "MBRF", strCount);
            w.Flush();
        }

        origOut.WriteLine();
        origOut.WriteLine("Output written to: " + outPath);
        return 0;
    }

    // -------------------------------------------------------------------------
    // Main section analysis
    // -------------------------------------------------------------------------

    private static void AnalyzeTrigSection(TextWriter w, Dictionary<string, byte[]> sections, string name, ushort strCount)
    {
        if (!sections.ContainsKey(name))
        {
            w.WriteLine("=== " + name + ": missing ===");
            return;
        }

        byte[] data = sections[name];
        int total = data.Length / 2400;
        int rem = data.Length % 2400;

        w.WriteLine("=== " + name + " (" + total + " triggers, " + data.Length + " bytes"
            + (rem != 0 ? ", +" + rem + " remainder" : "") + ") ===");
        w.WriteLine();

        // ---- category counts ----
        int cntFake = 0, cntFreezeEud = 0, cntEudGameplay = 0, cntUnknownOnly = 0, cntNormal = 0;
        int cntEudWithUnknown = 0;
        var eudGameplayList = new List<int>();

        // ---- aggregate stats ----
        var actionTypeCount = new SortedDictionary<int, int>();
        int badActType = 0, commentActs = 0, badActStr = 0, nonZeroActPad = 0;
        int nonZeroCondPad = 0, badCondType = 0;
        int badCondLoc = 0, badActLoc = 0, badCondUnit = 0, badActUnit = 0;
        int badPlayerFlag = 0, badCondFlags = 0, badActFlags = 0;
        int badCondCmp = 0, badActMod = 0, badPropSlot = 0, badSecondLoc = 0;
        var badCondUnitByType = new SortedDictionary<int, int>();
        var badActUnitByType = new SortedDictionary<int, int>();
        var badActModByType = new SortedDictionary<int, int>();
        var condFlagVals = new SortedDictionary<int, int>();
        var actFlagVals = new SortedDictionary<int, int>();

        for (int ti = 0; ti < total; ti++)
        {
            int off = ti * 2400;

            if (IsFakeTrigger(data, off))
            {
                cntFake++;
                continue;
            }

            // EUD classification
            bool hasEudSD = false, hasNonSD = false, hasUnknown = false;
            for (int i = 0; i < 64; i++)
            {
                int a = off + 320 + i * 32;
                byte at = data[a + 26];
                if (at == 0) continue;
                if (at > 57) hasUnknown = true;
                if (at == 45 && BitConverter.ToUInt32(data, a + 16) > 27) hasEudSD = true;
                if (at != 45) hasNonSD = true;
            }

            if (hasEudSD && !hasNonSD)
            {
                cntFreezeEud++;
            }
            else if (hasEudSD && hasNonSD)
            {
                cntEudGameplay++;
                if (hasUnknown) cntEudWithUnknown++;
                eudGameplayList.Add(ti);
            }
            else if (hasUnknown)
            {
                cntUnknownOnly++;
            }
            else
            {
                cntNormal++;
            }

            // Aggregate stats per condition
            for (int i = 0; i < 16; i++)
            {
                int c = off + i * 20;
                byte ct = data[c + 15];
                if (ct > 23) badCondType++;
                if (data[c + 18] != 0 || data[c + 19] != 0) nonZeroCondPad++;
                Increment(condFlagVals, data[c + 17]);
                if ((data[c + 17] & 0xE0) != 0) badCondFlags++;
                if (data[c + 14] > 10) badCondCmp++;
                if (IsBadLoc(data, c)) badCondLoc++;
                ushort cu = BitConverter.ToUInt16(data, c + 12);
                if (cu > 232 && cu != 0xFFFF) { badCondUnit++; Increment(badCondUnitByType, ct); }
            }

            // Aggregate stats per action
            for (int i = 0; i < 64; i++)
            {
                int a = off + 320 + i * 32;
                byte at = data[a + 26];
                Increment(actionTypeCount, at);
                if (at > 57) badActType++;
                if (at == 47) commentActs++;
                if (IsBadStr(data, a + 4, strCount) || IsBadStr(data, a + 8, strCount)) badActStr++;
                if (data[a + 29] != 0 || data[a + 30] != 0 || data[a + 31] != 0) nonZeroActPad++;
                Increment(actFlagVals, data[a + 28]);
                if ((data[a + 28] & 0xE0) != 0) badActFlags++;
                if (data[a + 27] > 10) { badActMod++; Increment(badActModByType, at); }
                if (IsBadLoc(data, a)) badActLoc++;
                if (IsSecondLocAction(at) && IsBadLoc(data, a + 20)) badSecondLoc++;
                if (at == 11 && BitConverter.ToUInt32(data, a + 20) > 63) { badPropSlot++; }
                ushort au = BitConverter.ToUInt16(data, a + 24);
                if (au > 232 && au != 0xFFFF) { badActUnit++; Increment(badActUnitByType, at); }
            }

            // Player execution flags
            for (int i = 0; i < 28; i++)
            {
                byte v = data[off + 2372 + i];
                if (v != 0 && v != 1) badPlayerFlag++;
            }
        }

        // ---- Print category summary ----
        w.WriteLine("--- CATEGORIES ---");
        w.WriteLine(F("Total             :", total));
        w.WriteLine(F("  Fake            :", cntFake) + "  (all-zero / all-FF / SMLP tail)");
        w.WriteLine(F("  Freeze-EUD      :", cntFreezeEud) + "  (SetDeaths-only w/ EPD addr; Freeze05 protection)");
        w.WriteLine(F("  EUD gameplay    :", cntEudGameplay) + "  (EPD SetDeaths + other actions)"
            + (cntEudWithUnknown > 0 ? "  [" + cntEudWithUnknown + " also have unknown action type(s)]" : ""));
        w.WriteLine(F("  Unknown action  :", cntUnknownOnly) + "  (action type >57, no EPD)");
        w.WriteLine(F("  Normal          :", cntNormal));
        w.WriteLine();

        // ---- Print aggregate stats ----
        w.WriteLine("--- AGGREGATE STATS ---");
        w.WriteLine("condType_bad=" + badCondType + "  condPad=" + nonZeroCondPad
            + "  actType_bad=" + badActType + "  comments=" + commentActs
            + "  actStr_bad=" + badActStr + "  actPad=" + nonZeroActPad);
        w.WriteLine("condLoc_bad=" + badCondLoc + "  actLoc_bad=" + badActLoc
            + "  condUnit_bad=" + badCondUnit + "  actUnit_bad=" + badActUnit
            + "  playerFlag_bad=" + badPlayerFlag);
        w.WriteLine("condFlags_bad=" + badCondFlags + "  actFlags_bad=" + badActFlags
            + "  condCmp_bad=" + badCondCmp + "  actMod_bad=" + badActMod);
        w.WriteLine("propSlot_bad=" + badPropSlot + "  secondLoc_bad=" + badSecondLoc);
        PrintCounts(w, "condUnit_badByType", badCondUnitByType);
        PrintCounts(w, "actUnit_badByType", badActUnitByType);
        PrintCounts(w, "actMod_badByType", badActModByType);
        PrintCounts(w, "condFlags", condFlagVals);
        PrintCounts(w, "actFlags", actFlagVals);

        var sb = new StringBuilder("actionTypes:");
        foreach (var kv in actionTypeCount)
            sb.Append("  " + ActTypeName((byte)kv.Key) + "(" + kv.Key + ")=" + kv.Value);
        w.WriteLine(sb.ToString());
        w.WriteLine();

        // ---- EUD gameplay trigger detailed dump ----
        if (eudGameplayList.Count > 0)
        {
            w.WriteLine("--- EUD GAMEPLAY TRIGGER DETAILS (" + eudGameplayList.Count + " triggers) ---");
            foreach (int ti in eudGameplayList)
                DumpTrigger(w, data, ti, strCount);
            w.WriteLine();
        }
    }

    // -------------------------------------------------------------------------
    // Per-trigger dump
    // -------------------------------------------------------------------------

    private static void DumpTrigger(TextWriter w, byte[] data, int ti, ushort strCount)
    {
        int off = ti * 2400;
        w.WriteLine();
        w.WriteLine("  *** Trigger #" + ti + "  (byte offset 0x" + off.ToString("X6") + ") ***");

        // Execution info
        uint execFlags = BitConverter.ToUInt32(data, off + 2368);
        w.Write("  ExecFlags=0x" + execFlags.ToString("X8") + "  RunFor:");
        bool any = false;
        for (int i = 0; i < 28; i++)
        {
            if (data[off + 2372 + i] != 0)
            {
                w.Write(" " + ExecSlotName(i));
                any = true;
            }
        }
        if (!any) w.Write(" (none)");
        w.WriteLine();

        // Conditions
        w.WriteLine();
        w.WriteLine("  Conditions:");
        bool hadCond = false;
        for (int i = 0; i < 16; i++)
        {
            int c = off + i * 20;
            byte ct = data[c + 15];
            if (ct == 0) continue;
            hadCond = true;
            uint loc = BitConverter.ToUInt32(data, c + 0);
            uint val = BitConverter.ToUInt32(data, c + 4);
            uint grp = BitConverter.ToUInt32(data, c + 8);
            ushort unit = BitConverter.ToUInt16(data, c + 12);
            byte cmp = data[c + 14];
            byte res = data[c + 16];
            byte fl = data[c + 17];
            ushort pad = BitConverter.ToUInt16(data, c + 18);

            string unitStr = (unit == 0xFFFF) ? "Any" : unit.ToString();
            string padStr = (pad != 0) ? "  pad=0x" + pad.ToString("X4") : "";

            w.WriteLine(string.Format("    [{0:D2}] {1,-24} type={2,3}  loc={3,5}  val={4,10}  grp={5,-16}  unit={6,-5}  cmp={7,-8}  res={8}  fl=0x{9:X2}{10}",
                i, CondTypeName(ct), ct,
                loc, val,
                grp <= 27 ? ActionPlayerName(grp) : ("0x" + grp.ToString("X8")),
                unitStr, CmpName(cmp), res, fl, padStr));
        }
        if (!hadCond)
            w.WriteLine("    (none)");

        // Actions
        w.WriteLine();
        w.WriteLine("  Actions:");
        bool hadAct = false;
        for (int i = 0; i < 64; i++)
        {
            int a = off + 320 + i * 32;
            byte at = data[a + 26];
            if (at == 0) continue;
            hadAct = true;

            uint loc  = BitConverter.ToUInt32(data, a +  0);
            uint str  = BitConverter.ToUInt32(data, a +  4);
            uint wav  = BitConverter.ToUInt32(data, a +  8);
            uint time = BitConverter.ToUInt32(data, a + 12);
            uint plr  = BitConverter.ToUInt32(data, a + 16);
            uint amt  = BitConverter.ToUInt32(data, a + 20);
            ushort unit = BitConverter.ToUInt16(data, a + 24);
            byte mod  = data[a + 27];
            byte fl   = data[a + 28];
            uint pad  = (uint)(data[a + 29] | (data[a + 30] << 8) | (data[a + 31] << 16));

            string typeName = ActTypeName(at);
            string plrStr;
            string epdLine = "";

            if (at == 45 && plr > 27)
            {
                // EUD SetDeaths: compute target memory address
                uint epd = unchecked(plr * 228u + (uint)unit);
                uint mem = unchecked(0x0058A364u + epd * 4u);
                plrStr = "0x" + plr.ToString("X8") + "(EUD)";
                epdLine = "  → EPD=0x" + epd.ToString("X8") + "  mem=0x" + mem.ToString("X8");
            }
            else
            {
                plrStr = plr <= 27 ? ActionPlayerName(plr) : ("0x" + plr.ToString("X8"));
            }

            string unitStr = (unit == 0xFFFF) ? "Any" : unit.ToString();
            string padStr = (pad != 0) ? "  pad=0x" + pad.ToString("X6") : "";

            // Build field string: show all fields; mark non-default ones more clearly
            w.WriteLine(string.Format(
                "    [{0:D2}] {1,-24} type={2,3}  loc={3,5}  str={4,5}  wav={5,5}  time={6,8}  player={7,-20}  amt={8,10}  unit={9,-5}  mod={10,-12}  fl=0x{11:X2}{12}{13}",
                i, typeName, at,
                loc, str, wav, time,
                plrStr, amt, unitStr, ModName(mod), fl, padStr, epdLine));
        }
        if (!hadAct)
            w.WriteLine("    (none)");
    }

    // -------------------------------------------------------------------------
    // Trigger classification helpers
    // -------------------------------------------------------------------------

    private static bool IsFakeTrigger(byte[] data, int off)
    {
        bool allZero = true, allFF = true;
        for (int i = 0; i < 2400; i++)
        {
            if (data[off + i] != 0x00) allZero = false;
            if (data[off + i] != 0xFF) allFF = false;
            if (!allZero && !allFF) break;
        }
        if (allZero || allFF) return true;

        // SMLP fake trigger: player tail bytes are all 0xFF
        for (int i = 2368; i < 2400; i++)
            if (data[off + i] != 0xFF) return false;
        return true;
    }

    private static bool IsBadLoc(byte[] data, int off)
    {
        uint v = BitConverter.ToUInt32(data, off);
        return v == 0xFFFFFFFF || (v > 255 && v < 0x00010000);
    }

    private static bool IsBadStr(byte[] data, int off, ushort strCount)
    {
        uint v = BitConverter.ToUInt32(data, off);
        return v == 0xFFFFFFFF || (strCount > 0 && v > strCount && v < 0x00010000);
    }

    // Actions that use a second location field (at offset +20)
    private static bool IsSecondLocAction(byte type)
    {
        return type == 39 || type == 45 || type == 46 || type == 48 || type == 49
            || type == 50 || type == 51 || type == 52 || type == 53;
    }

    // -------------------------------------------------------------------------
    // Name lookup tables
    // -------------------------------------------------------------------------

    private static string CondTypeName(byte t)
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

    private static string ActTypeName(byte t)
    {
        string[] n = {
            /*  0 */ "None",
            /*  1 */ "Victory",
            /*  2 */ "Defeat",
            /*  3 */ "PreserveTrigger",
            /*  4 */ "Wait",
            /*  5 */ "PauseGame",
            /*  6 */ "UnpauseGame",
            /*  7 */ "Transmission",
            /*  8 */ "PlayWAV",
            /*  9 */ "DisplayText",
            /* 10 */ "CenterView",
            /* 11 */ "CreateUnitWithProps",
            /* 12 */ "SetMissionObjectives",
            /* 13 */ "SetSwitch",
            /* 14 */ "SetCountdownTimer",
            /* 15 */ "RunAIScript",
            /* 16 */ "RunAIScriptAtLoc",
            /* 17 */ "LeaderboardCtrlAtLoc",
            /* 18 */ "LeaderboardCtrl",
            /* 19 */ "LeaderboardResources",
            /* 20 */ "LeaderboardKills",
            /* 21 */ "LeaderboardPoints",
            /* 22 */ "KillUnit",
            /* 23 */ "KillUnitAtLoc",
            /* 24 */ "RemoveUnit",
            /* 25 */ "RemoveUnitAtLoc",
            /* 26 */ "SetResources",
            /* 27 */ "SetScore",
            /* 28 */ "MinimapPing",
            /* 29 */ "TalkingPortrait",
            /* 30 */ "MuteUnitSpeech",
            /* 31 */ "UnmuteUnitSpeech",
            /* 32 */ "LeaderboardCompPlayers",
            /* 33 */ "LeaderboardGoalCtrl",
            /* 34 */ "LeaderboardGoalCtrlAtLoc",
            /* 35 */ "LeaderboardGoalResources",
            /* 36 */ "LeaderboardGoalKills",
            /* 37 */ "LeaderboardGoalPoints",
            /* 38 */ "MoveLocation",
            /* 39 */ "MoveUnit",
            /* 40 */ "LeaderboardGreed",
            /* 41 */ "SetNextScenario",
            /* 42 */ "SetDoodadState",
            /* 43 */ "SetInvincibility",
            /* 44 */ "CreateUnit",
            /* 45 */ "SetDeaths",
            /* 46 */ "Order",
            /* 47 */ "Comment",
            /* 48 */ "GiveUnitsToPlayer",
            /* 49 */ "ModifyUnitHP",
            /* 50 */ "ModifyUnitEnergy",
            /* 51 */ "ModifyUnitShield",
            /* 52 */ "ModifyUnitResource",
            /* 53 */ "ModifyUnitHangar",
            /* 54 */ "PauseTimer",
            /* 55 */ "UnpauseTimer",
            /* 56 */ "Draw",
            /* 57 */ "SetAllianceStatus",
        };
        return t < n.Length ? n[t] : "Unknown(" + t + ")";
    }

    private static string CmpName(byte c)
    {
        if (c == 0)  return "AtLeast";
        if (c == 1)  return "AtMost";
        if (c == 10) return "Exactly";
        return "Cmp(" + c + ")";
    }

    private static string ModName(byte m)
    {
        if (m == 7)  return "SetTo";
        if (m == 8)  return "Add";
        if (m == 9)  return "Subtract";
        if (m == 4)  return "Set";
        if (m == 5)  return "Clear";
        if (m == 6)  return "Toggle";
        if (m == 11) return "Random";
        if (m == 0)  return "-";
        return "Mod(" + m + ")";
    }

    // Names for the 28 player-execution slots at trigger offset 2372
    private static string ExecSlotName(int slot)
    {
        string[] n = {
            "P1","P2","P3","P4","P5","P6","P7","P8","P9","P10","P11","P12",
            "Slot12","Force1","Force2","Force3","Force4","AllPlayers",
            "CurrentPlayer","Foes","Allies","Neutral","AllPlayers2","NoPeers",
            "PlayersByRace","Enemies","NonAlliedVP","Slot27"
        };
        return slot < n.Length ? n[slot] : "Slot" + slot;
    }

    // Names for player field in conditions/actions (0–27 = standard groups)
    private static string ActionPlayerName(uint p)
    {
        string[] n = {
            "P1","P2","P3","P4","P5","P6","P7","P8","P9","P10","P11","P12",
            "NeutralAll","CurrentPlayer","Foes","Allies","Neutral","AllPlayers",
            "Force1","Force2","Force3","Force4","NonAlliedVP","Slot22","Slot23",
            "Slot24","Slot25","Slot26"
        };
        if (p < (uint)n.Length) return n[(int)p];
        if (p == 0xFFFFFFFF) return "Any/None";
        return "Grp(0x" + p.ToString("X") + ")";
    }

    // -------------------------------------------------------------------------
    // Section printing helpers
    // -------------------------------------------------------------------------

    private static void PrintSectionIndex(TextWriter w, Dictionary<string, byte[]> sections)
    {
        var sb = new StringBuilder("Sections:");
        foreach (var kv in sections)
            sb.Append("  " + kv.Key.Replace(" ", "_") + ":" + kv.Value.Length);
        w.WriteLine(sb.ToString());
    }

    private static void PrintSectionLength(TextWriter w, Dictionary<string, byte[]> sections, string name)
    {
        if (sections.ContainsKey(name))
            w.WriteLine(name + ": " + sections[name].Length + " bytes");
        else
            w.WriteLine(name + ": missing");
    }

    private static void PrintUnitSection(TextWriter w, Dictionary<string, byte[]> sections)
    {
        if (!sections.ContainsKey("UNIT")) { w.WriteLine("UNIT: missing"); return; }
        byte[] data = sections["UNIT"];
        int total = data.Length / 36;
        int allFF = 0, allZero = 0, badId = 0, badPl = 0;
        var players = new SortedDictionary<int, int>();
        var unitIds = new SortedDictionary<int, int>();

        for (int off = 0; off + 35 < data.Length; off += 36)
        {
            bool ff = true, zero = true;
            for (int i = 0; i < 36; i++)
            {
                if (data[off + i] != 0xFF) ff = false;
                if (data[off + i] != 0x00) zero = false;
            }
            if (ff) allFF++;
            if (zero) allZero++;
            ushort uid = BitConverter.ToUInt16(data, off + 8);
            byte pl = data[off + 16];
            Increment(unitIds, uid);
            Increment(players, pl);
            if (uid > 232) badId++;
            if (pl > 11) badPl++;
        }
        w.WriteLine("UNIT: bytes=" + data.Length + " records=" + total + " rem=" + (data.Length % 36)
            + " allFF=" + allFF + " allZero=" + allZero + " badId=" + badId + " badPlayer=" + badPl);
        PrintCounts(w, "UNIT players", players);
        PrintCounts(w, "UNIT unitIds", unitIds);
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    private static string F(string label, int value)
    {
        return string.Format("{0,-20} {1,6}", label, value);
    }

    private static void PrintCounts(TextWriter w, string label, SortedDictionary<int, int> d)
    {
        var sb = new StringBuilder(label + ":");
        foreach (var kv in d) sb.Append("  " + kv.Key + "=" + kv.Value);
        w.WriteLine(sb.ToString());
    }

    private static void Increment(SortedDictionary<int, int> d, int key)
    {
        int v; d.TryGetValue(key, out v); d[key] = v + 1;
    }

    // -------------------------------------------------------------------------
    // CHK parsing
    // -------------------------------------------------------------------------

    private static Dictionary<string, byte[]> ParseChk(byte[] chk)
    {
        var sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int p = 0; p + 8 <= chk.Length;)
        {
            string nm = Encoding.ASCII.GetString(chk, p, 4);
            int len = BitConverter.ToInt32(chk, p + 4);
            p += 8;
            if (len < 0 || p + len > chk.Length) break;
            var d = new byte[len];
            Buffer.BlockCopy(chk, p, d, 0, len);
            sections[nm] = d;
            p += len;
        }
        return sections;
    }

    private static byte[] ReadScenarioChk(string path)
    {
        if (Path.GetExtension(path).Equals(".chk", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(path);

        string[] names = { "staredit\\scenario.chk", "staredit/scenario.chk", "scenario.chk" };
        Locale[] locales = {
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
                        using (var reader = mpq.GetFile(name, locale))
                        {
                            if (reader != null)
                                return reader.ToArray();
                        }
                    }
                    catch { }
                }
            }
        }
        return null;
    }
}
