using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static partial class StarcraftMapUnprotector
{
    // Human-facing map analysis report. Consolidates scattered info into one
    // Markdown file so a Freeze/EUD map can be inspected at a glance.
    // Design: Docs/Formats/Freeze05/report.md
    private static bool GenerateMapReport(string input, string reportPath)
    {
        try
        {
            var stats = new Stats { FreezeBruteforceKey = true };
            byte[] inputBytes = File.ReadAllBytes(input);

            uint[] seedKey, destKey;
            if (DetectFreezeProtection(inputBytes, out seedKey, out destKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = seedKey;
                stats.FreezeDestKey = destKey;
            }

            List<MpqFileEntry> extraFiles;
            byte[] chk = LooksLikeChk(inputBytes)
                ? inputBytes
                : ExtractScenarioChk(input, stats, out extraFiles);

            List<Section> sections = ParseChk(chk);
            var grouped = GroupSectionsForTriggerDump(sections, stats);

            // Decrypt encrypted TRIG records so trigger classification is meaningful.
            byte[] trigData = null;
            int totalTriggers = 0;
            int encryptedCount = 0;
            uint recoveredKey = 0;
            bool keyRecovered = false;

            List<byte[]> trigList;
            if (grouped.TryGetValue("TRIG", out trigList) && trigList.Count > 0)
            {
                trigData = (byte[])trigList[0].Clone();
                totalTriggers = trigData.Length / FreezeTrigSize;
                var encrypted = CollectEncryptedFreezeTriggerInfos(trigData, totalTriggers);
                encryptedCount = encrypted.Count;
                if (encryptedCount > 0)
                {
                    if (TryRecoverFreezeKeyByFastBruteforce(trigData, totalTriggers, out recoveredKey))
                    {
                        keyRecovered = true;
                        DecryptAllFreezeTriggers(trigData, recoveredKey, true);
                    }
                }
            }

            string[] strings = ReadBestStringTable(grouped);

            string dir = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var w = new StreamWriter(reportPath, false, new UTF8Encoding(true)))
            {
                w.WriteLine("# 맵 분석 리포트: " + Path.GetFileName(input));
                w.WriteLine();

                WriteReportOverview(w, input, chk, grouped, strings);
                WriteReportProtection(w, stats, totalTriggers, encryptedCount, keyRecovered, recoveredKey);
                WriteReportPlayers(w, grouped, strings);
                WriteReportTriggers(w, trigData, totalTriggers, encryptedCount, keyRecovered);
                WriteReportEudActivity(w, trigData, totalTriggers);
                WriteReportSignalTriggers(w, trigData, totalTriggers, grouped, strings);
                WriteReportLocations(w, grouped, strings);
                WriteReportUnits(w, grouped);
                WriteReportStrings(w, strings);
            }

            Console.WriteLine("Map analysis report written: " + reportPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Report failed: " + ex.Message);
            return false;
        }
    }

    private static byte[] GetFirstSection(Dictionary<string, List<byte[]>> grouped, string name)
    {
        List<byte[]> list;
        if (grouped.TryGetValue(name, out list) && list.Count > 0)
            return list[list.Count - 1];
        return null;
    }

    private static string ReportString(string[] strings, int id)
    {
        if (strings == null || id <= 0 || id >= strings.Length)
            return null;
        string s = strings[id];
        return string.IsNullOrEmpty(s) ? null : s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static void WriteReportOverview(
        TextWriter w, string input, byte[] chk,
        Dictionary<string, List<byte[]>> grouped, string[] strings)
    {
        w.WriteLine("## 개요");
        w.WriteLine();
        w.WriteLine("- 입력 파일: `" + Path.GetFullPath(input) + "`");
        w.WriteLine("- scenario.chk 크기: " + chk.Length.ToString("N0") + " bytes");

        byte[] sprp = GetFirstSection(grouped, "SPRP");
        if (sprp != null && sprp.Length >= 4)
        {
            string name = ReportString(strings, BitConverter.ToUInt16(sprp, 0));
            string desc = ReportString(strings, BitConverter.ToUInt16(sprp, 2));
            w.WriteLine("- 맵 이름: " + (name ?? "(없음)"));
            w.WriteLine("- 맵 설명: " + (desc ?? "(없음)"));
        }

        byte[] dim = GetFirstSection(grouped, "DIM ");
        if (dim != null && dim.Length >= 4)
        {
            ushort width = BitConverter.ToUInt16(dim, 0);
            ushort height = BitConverter.ToUInt16(dim, 2);
            w.WriteLine("- 맵 크기: " + width + " x " + height + " 타일");
        }

        byte[] era = GetFirstSection(grouped, "ERA ");
        if (era != null && era.Length >= 2)
        {
            ushort t = BitConverter.ToUInt16(era, 0);
            w.WriteLine("- 타일셋: " + TilesetName(t) + " (" + t + ")");
        }

        w.WriteLine();
    }

    private static void WriteReportProtection(
        TextWriter w, Stats stats, int totalTriggers, int encryptedCount,
        bool keyRecovered, uint recoveredKey)
    {
        w.WriteLine("## 보호");
        w.WriteLine();
        if (stats.IsFreezeProtected)
        {
            w.WriteLine("- Freeze05 marker: **DETECTED**");
            w.WriteLine("- seedKey: `" + FormatKey(stats.FreezeSeedKey) + "`");
            w.WriteLine("- destKey: `" + FormatKey(stats.FreezeDestKey) + "`");
        }
        else
        {
            w.WriteLine("- Freeze05 marker: not detected");
        }

        w.WriteLine("- 암호화 트리거: " + encryptedCount + " / " + totalTriggers);
        if (encryptedCount > 0)
        {
            w.WriteLine(keyRecovered
                ? "- triggerKey: `0x" + recoveredKey.ToString("X8") + "` (복구 성공)"
                : "- triggerKey: 복구 실패 (트리거 분류가 부정확할 수 있음)");
        }
        w.WriteLine();
    }

    private static void WriteReportPlayers(
        TextWriter w, Dictionary<string, List<byte[]>> grouped, string[] strings)
    {
        byte[] ownr = GetFirstSection(grouped, "OWNR");
        byte[] side = GetFirstSection(grouped, "SIDE");

        w.WriteLine("## 플레이어");
        w.WriteLine();

        if (ownr != null && ownr.Length >= 12)
        {
            w.WriteLine("| 슬롯 | 소유 유형 | 종족 |");
            w.WriteLine("|------|-----------|------|");
            for (int i = 0; i < 12; i++)
            {
                byte o = ownr[i];
                if (o == 0) continue; // inactive slot
                string race = (side != null && side.Length >= 12) ? RaceName(side[i]) : "?";
                w.WriteLine("| P" + (i + 1) + " | " + OwnerName(o) + " | " + race + " |");
            }
            w.WriteLine();
        }

        byte[] forc = GetFirstSection(grouped, "FORC");
        if (forc != null && forc.Length >= 16)
        {
            w.WriteLine("세력(FORC):");
            w.WriteLine();
            for (int f = 0; f < 4; f++)
            {
                var members = new List<string>();
                for (int p = 0; p < 8; p++)
                    if (forc[p] == f) members.Add("P" + (p + 1));
                if (members.Count == 0) continue;
                string fname = ReportString(strings, BitConverter.ToUInt16(forc, 8 + f * 2));
                w.WriteLine("- " + (fname ?? ("Force " + (f + 1))) + ": " + string.Join(", ", members));
            }
            w.WriteLine();
        }
    }

    private static void WriteReportTriggers(
        TextWriter w, byte[] trigData, int totalTriggers, int encryptedCount, bool keyRecovered)
    {
        w.WriteLine("## 트리거 요약");
        w.WriteLine();

        if (trigData == null || totalTriggers == 0)
        {
            w.WriteLine("- TRIG 섹션 없음");
            w.WriteLine();
            return;
        }

        var encryptedSet = new HashSet<int>();
        foreach (var info in CollectEncryptedFreezeTriggerInfos(trigData, totalTriggers))
            encryptedSet.Add(info.Index);

        int game = 0, eudVm = 0, decrypted = 0, disabled = 0;
        var regionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int t = 0; t < totalTriggers; t++)
        {
            int off = t * FreezeTrigSize;
            uint execFlags = BitConverter.ToUInt32(trigData, off + 2368);
            bool isEncrypted = encryptedSet.Contains(t);
            bool isEud = IsFreezeEudTrigger(trigData, off);
            bool isDisabled = execFlags == 0;
            if (isDisabled)
                for (int p = 0; p < 28; p++)
                    if (trigData[off + 2372 + p] != 0) { isDisabled = false; break; }

            if (isEncrypted) decrypted++;
            else if (isDisabled) disabled++;
            else if (isEud) eudVm++;
            else game++;

            TallyTriggerEudRegions(trigData, off, regionCounts);
        }

        w.WriteLine("- 전체 트리거: **" + totalTriggers + "**");
        w.WriteLine("  - GAME (일반 로직): " + game);
        w.WriteLine("  - EUD-VM (eudplib 잡음): " + eudVm);
        w.WriteLine("  - DECRYPTED (복호화됨): " + decrypted);
        w.WriteLine("  - DISABLED (비활성): " + disabled);
        if (encryptedCount > 0 && !keyRecovered)
            w.WriteLine("  - ⚠ triggerKey 미복구 — 위 분류는 부정확할 수 있음");
        w.WriteLine();

        if (regionCounts.Count > 0)
        {
            w.WriteLine("EUD가 건드리는 메모리 영역 (SetDeaths 기준):");
            w.WriteLine();
            foreach (var kv in regionCounts.OrderByDescending(k => k.Value))
                w.WriteLine("- " + kv.Key + ": " + kv.Value + "회");
            w.WriteLine();
        }
    }

    private static void TallyTriggerEudRegions(byte[] data, int off, Dictionary<string, int> counts)
    {
        for (int i = 0; i < 64; i++)
        {
            int a = off + 320 + i * 32;
            byte type = data[a + 26];
            if (type != 45) continue; // SetDeaths only
            uint player = BitConverter.ToUInt32(data, a + 16);
            ushort unit = BitConverter.ToUInt16(data, a + 24);
            if (player <= 27 && player != 13) continue; // not an EUD target
            uint epd = unchecked(player + (uint)unit * 12u);
            uint mem = unchecked(0x0058A364u + epd * 4u);
            string region = DescribeScMemoryAddress(mem) ?? "기타/미상";
            int cur;
            counts.TryGetValue(region, out cur);
            counts[region] = cur + 1;
        }
    }

    private static void WriteReportLocations(
        TextWriter w, Dictionary<string, List<byte[]>> grouped, string[] strings)
    {
        byte[] mrgn = GetFirstSection(grouped, "MRGN");
        w.WriteLine("## 위치 (MRGN)");
        w.WriteLine();
        if (mrgn == null || mrgn.Length < 20)
        {
            w.WriteLine("- 없음");
            w.WriteLine();
            return;
        }

        int count = mrgn.Length / 20;
        int named = 0;
        w.WriteLine("| # | 이름 | 위치(L,T,R,B) |");
        w.WriteLine("|---|------|----------------|");
        for (int i = 0; i < count; i++)
        {
            int o = i * 20;
            ushort nameId = BitConverter.ToUInt16(mrgn, o + 16);
            string name = ReportString(strings, nameId);
            if (name == null) continue; // skip unnamed locations
            uint l = BitConverter.ToUInt32(mrgn, o + 0);
            uint t = BitConverter.ToUInt32(mrgn, o + 4);
            uint r = BitConverter.ToUInt32(mrgn, o + 8);
            uint b = BitConverter.ToUInt32(mrgn, o + 12);
            w.WriteLine("| " + (i + 1) + " | " + name + " | " + l + "," + t + "," + r + "," + b + " |");
            named++;
        }
        if (named == 0)
            w.WriteLine("| - | (이름 있는 위치 없음) | - |");
        w.WriteLine();
        w.WriteLine("전체 location 슬롯: " + count + " (이름 있음: " + named + ")");
        w.WriteLine();
    }

    private static void WriteReportUnits(
        TextWriter w, Dictionary<string, List<byte[]>> grouped)
    {
        byte[] unit = GetFirstSection(grouped, "UNIT");
        w.WriteLine("## 유닛 (UNIT)");
        w.WriteLine();
        if (unit == null || unit.Length < 36)
        {
            w.WriteLine("- 배치 유닛 없음");
            w.WriteLine();
            return;
        }

        int count = unit.Length / 36;
        var byOwner = new int[16];
        var byType = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int o = i * 36;
            byte owner = unit[o + 16];
            ushort unitId = BitConverter.ToUInt16(unit, o + 8);
            if (owner < 16) byOwner[owner]++;
            int cur;
            byType.TryGetValue(unitId, out cur);
            byType[unitId] = cur + 1;
        }

        w.WriteLine("- 전체 배치 유닛: **" + count + "**");
        w.WriteLine();
        w.WriteLine("소유자별:");
        w.WriteLine();
        for (int p = 0; p < 12; p++)
            if (byOwner[p] > 0) w.WriteLine("- P" + (p + 1) + ": " + byOwner[p]);
        w.WriteLine();
        w.WriteLine("유닛 종류 상위 20:");
        w.WriteLine();
        foreach (var kv in byType.OrderByDescending(k => k.Value).Take(20))
            w.WriteLine("- " + UnitName(kv.Key) + " (#" + kv.Key + "): " + kv.Value);
        w.WriteLine();
    }

    private static void WriteReportStrings(TextWriter w, string[] strings)
    {
        w.WriteLine("## 문자열 (STR/STRx)");
        w.WriteLine();
        if (strings == null || strings.Length <= 1)
        {
            w.WriteLine("- 없음");
            w.WriteLine();
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<string>();
        for (int i = 1; i < strings.Length; i++)
        {
            string s = ReportString(strings, i);
            if (s == null || s.Length == 0) continue;
            if (!seen.Add(s)) continue;
            rows.Add("- [" + i + "] " + s);
        }

        int limit = 200;
        w.WriteLine("읽을 수 있는 문자열: " + rows.Count + "개 (중복 제거)");
        w.WriteLine();
        foreach (var row in rows.Take(limit))
            w.WriteLine(row);
        if (rows.Count > limit)
            w.WriteLine("- ... (" + (rows.Count - limit) + "개 더 — 전체는 --dump-all-triggers 참고)");
        w.WriteLine();
    }

    // 분석 도구: 모든 EUD 메모리 접근을 주소별 읽기/쓰기 횟수 CSV로 덤프한다.
    // 여러 맵의 CSV를 누적하면 알려지지 않은 SC 테이블 경계를 데이터로 확정할 수 있다.
    private static bool DumpEudHistogram(string input, string csvPath)
    {
        try
        {
            var stats = new Stats { FreezeBruteforceKey = true };
            byte[] inputBytes = File.ReadAllBytes(input);

            uint[] seedKey, destKey;
            if (DetectFreezeProtection(inputBytes, out seedKey, out destKey))
            {
                stats.IsFreezeProtected = true;
                stats.FreezeSeedKey = seedKey;
                stats.FreezeDestKey = destKey;
            }

            List<MpqFileEntry> extraFiles;
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
            var encrypted = CollectEncryptedFreezeTriggerInfos(trigData, totalTriggers);
            if (encrypted.Count > 0)
            {
                uint key;
                if (TryRecoverFreezeKeyByFastBruteforce(trigData, totalTriggers, out key))
                    DecryptAllFreezeTriggers(trigData, key, true);
            }

            var reads = new Dictionary<uint, int>();
            var writes = new Dictionary<uint, int>();

            for (int t = 0; t < totalTriggers; t++)
            {
                int off = t * FreezeTrigSize;
                for (int c = 0; c < 16; c++)
                {
                    int co = off + c * 20;
                    if (trigData[co + 15] != 15) continue; // Deaths
                    uint grp = BitConverter.ToUInt32(trigData, co + 8);
                    ushort unit = BitConverter.ToUInt16(trigData, co + 12);
                    if (grp <= 27 && grp != 13) continue;
                    uint mem = unchecked(0x0058A364u + (grp + (uint)unit * 12u) * 4u);
                    int v; reads.TryGetValue(mem, out v); reads[mem] = v + 1;
                }
                for (int i = 0; i < 64; i++)
                {
                    int ao = off + 320 + i * 32;
                    if (trigData[ao + 26] != 45) continue; // SetDeaths
                    uint player = BitConverter.ToUInt32(trigData, ao + 16);
                    ushort unit = BitConverter.ToUInt16(trigData, ao + 24);
                    if (player <= 27 && player != 13) continue;
                    uint mem = unchecked(0x0058A364u + (player + (uint)unit * 12u) * 4u);
                    int v; writes.TryGetValue(mem, out v); writes[mem] = v + 1;
                }
            }

            var all = new HashSet<uint>(reads.Keys);
            all.UnionWith(writes.Keys);

            string dir = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var w = new StreamWriter(csvPath, false, new UTF8Encoding(false)))
            {
                w.WriteLine("address,reads,writes,region,map");
                string mapName = Path.GetFileName(input).Replace(",", " ");
                foreach (uint addr in all.OrderBy(a => a))
                {
                    int r, ww; reads.TryGetValue(addr, out r); writes.TryGetValue(addr, out ww);
                    string region = DescribeScMemoryAddress(addr) ?? "";
                    w.WriteLine("0x" + addr.ToString("X8") + "," + r + "," + ww + "," + region + "," + mapName);
                }
            }

            Console.WriteLine("EUD histogram written: " + csvPath + " (" + all.Count + " unique addresses)");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("EUD histogram failed: " + ex.Message);
            return false;
        }
    }

    // v3: 모든 트리거의 EUD 메모리 접근(Deaths 읽기 + SetDeaths 쓰기)을 스캔해서
    // "이 맵이 어떤 게임 상태를 건드리는가"를 메모리 영역 단위로 추론한다.
    // EUD-VM 잡음 트리거까지 포함해야 실제로 뭘 하는지가 드러난다.
    private sealed class EudRegionStat
    {
        public int Read;       // Deaths 조건 (읽기)
        public int Set;        // SetDeaths SetTo
        public int Add;        // SetDeaths Add
        public int Sub;        // SetDeaths Subtract
        public int Writes { get { return Set + Add + Sub; } }
        public int Total { get { return Read + Writes; } }
    }

    private static void WriteReportEudActivity(TextWriter w, byte[] trigData, int totalTriggers)
    {
        w.WriteLine("## EUD 활동 분석 (이 맵이 뭘 하는가)");
        w.WriteLine();

        if (trigData == null || totalTriggers == 0)
        {
            w.WriteLine("- TRIG 섹션 없음");
            w.WriteLine();
            return;
        }

        var stat = new Dictionary<string, EudRegionStat>(StringComparer.Ordinal);
        int unknownRead = 0, unknownWrite = 0;
        var unkRead = new Dictionary<uint, int>();
        var unkWrite = new Dictionary<uint, int>();

        for (int t = 0; t < totalTriggers; t++)
        {
            int off = t * FreezeTrigSize;

            // 조건: Deaths(type 15) → EUD 읽기
            for (int c = 0; c < 16; c++)
            {
                int co = off + c * 20;
                if (trigData[co + 15] != 15) continue;
                uint grp = BitConverter.ToUInt32(trigData, co + 8);
                ushort unit = BitConverter.ToUInt16(trigData, co + 12);
                if (grp <= 27 && grp != 13) continue;
                uint mem = unchecked(0x0058A364u + (grp + (uint)unit * 12u) * 4u);
                string region = DescribeScMemoryAddress(mem);
                if (region == null) { unknownRead++; int rc; unkRead.TryGetValue(mem, out rc); unkRead[mem] = rc + 1; continue; }
                GetEudStat(stat, region).Read++;
            }

            // 액션: SetDeaths(type 45) → EUD 쓰기
            for (int i = 0; i < 64; i++)
            {
                int ao = off + 320 + i * 32;
                if (trigData[ao + 26] != 45) continue;
                uint player = BitConverter.ToUInt32(trigData, ao + 16);
                ushort unit = BitConverter.ToUInt16(trigData, ao + 24);
                byte mod = trigData[ao + 27];
                if (player <= 27 && player != 13) continue;
                uint mem = unchecked(0x0058A364u + (player + (uint)unit * 12u) * 4u);
                string region = DescribeScMemoryAddress(mem);
                if (region == null) { unknownWrite++; int wc; unkWrite.TryGetValue(mem, out wc); unkWrite[mem] = wc + 1; continue; }
                var s = GetEudStat(stat, region);
                if (mod == 8) s.Add++;
                else if (mod == 9) s.Sub++;
                else s.Set++;
            }
        }

        if (stat.Count == 0 && unknownRead == 0 && unknownWrite == 0)
        {
            w.WriteLine("EUD 메모리 접근이 감지되지 않음 (일반 트리거 맵으로 보임).");
            w.WriteLine();
            return;
        }

        // 1) 추론 요약 — 활동량 순으로 "무엇을 하는가"를 자연어로
        w.WriteLine("이 맵이 EUD로 직접 건드리는 게임 상태 (활동 많은 순):");
        w.WriteLine();
        foreach (var kv in stat.OrderByDescending(k => k.Value.Total))
        {
            var s = kv.Value;
            var ops = new List<string>();
            if (s.Read > 0) ops.Add("읽기 " + s.Read);
            if (s.Set > 0) ops.Add("Set " + s.Set);
            if (s.Add > 0) ops.Add("Add " + s.Add);
            if (s.Sub > 0) ops.Add("Sub " + s.Sub);
            w.WriteLine("- **" + EudRegionMeaning(kv.Key) + "** (" + kv.Key + ") — " +
                        string.Join(", ", ops));
        }
        if (unknownRead > 0 || unknownWrite > 0)
            w.WriteLine("- 미분류 영역 — 읽기 " + unknownRead + ", 쓰기 " + unknownWrite +
                        " (아래 상세)");
        w.WriteLine();

        // 2) 한 줄 결론
        w.WriteLine("**요약:** " + InferEudPurpose(stat));
        w.WriteLine();

        // 3) 미분류 주소 상세 — 데이터가 직접 어디를 가리키는지 보여준다
        WriteReportUnknownEud(w, unkRead, unkWrite);
    }

    // 알려진 SC 메모리 맵 밖 주소의 실제 분포를 보여준다. 이게 있으면
    // "미분류 1950건"이 한 곳에 뭉쳐있는지, 흩어져있는지가 드러난다.
    private static void WriteReportUnknownEud(
        TextWriter w, Dictionary<uint, int> unkRead, Dictionary<uint, int> unkWrite)
    {
        if (unkRead.Count == 0 && unkWrite.Count == 0) return;

        // 주소별 합산 (읽기+쓰기)
        var combined = new Dictionary<uint, int>();
        foreach (var kv in unkRead) { int c; combined.TryGetValue(kv.Key, out c); combined[kv.Key] = c + kv.Value; }
        foreach (var kv in unkWrite) { int c; combined.TryGetValue(kv.Key, out c); combined[kv.Key] = c + kv.Value; }

        w.WriteLine("### 미분류 EUD 주소 상세");
        w.WriteLine();
        w.WriteLine("고유 주소 " + combined.Count + "개. 알려진 SC 메모리 맵에 없는 접근들 — "
                    + "한 주소에 집중되면 카운터/변수, 넓게 퍼지면 배열/구조체 순회일 가능성.");
        w.WriteLine();

        // 4KB 페이지별 히스토그램 (어느 데이터 구조 근처인지)
        var pages = new Dictionary<uint, int>();
        foreach (var kv in combined)
        {
            uint page = kv.Key & 0xFFFFF000u;
            int c; pages.TryGetValue(page, out c); pages[page] = c + kv.Value;
        }
        w.WriteLine("페이지(4KB)별 접근 집중도 상위:");
        w.WriteLine();
        w.WriteLine("| 페이지 | 접근 | 인접 영역 추정 |");
        w.WriteLine("|--------|------|----------------|");
        foreach (var kv in pages.OrderByDescending(k => k.Value).Take(10))
            w.WriteLine("| 0x" + kv.Key.ToString("X8") + " | " + kv.Value + " | " +
                        GuessNearbyRegion(kv.Key) + " |");
        w.WriteLine();

        // 개별 핫 주소 상위
        w.WriteLine("핫 주소 상위 (단일 주소 집중 = EUD 변수일 확률 높음):");
        w.WriteLine();
        w.WriteLine("| 주소 | 읽기 | 쓰기 |");
        w.WriteLine("|------|------|------|");
        foreach (var kv in combined.OrderByDescending(k => k.Value).Take(15))
        {
            int r, ww; unkRead.TryGetValue(kv.Key, out r); unkWrite.TryGetValue(kv.Key, out ww);
            w.WriteLine("| 0x" + kv.Key.ToString("X8") + " | " + r + " | " + ww + " |");
        }
        w.WriteLine();
    }

    // 페이지 주소가 알려진 SC 데이터 구조 근처인지 대략 위치를 잡아준다.
    private static string GuessNearbyRegion(uint page)
    {
        if (page >= 0x00400000u && page < 0x00500000u) return ".text (코드 영역 — 패치/후킹)";
        if (page >= 0x00500000u && page < 0x00580000u) return ".rdata/문자열·맵데이터 부근";
        if (page >= 0x00580000u && page < 0x00600000u) return "유닛/death/스코어 테이블 부근";
        if (page >= 0x00600000u && page < 0x00670000u) return "트리거/게임상태 부근";
        if (page >= 0x00670000u && page < 0x006D0000u) return "스프라이트/UI/입력 부근";
        if (page >= 0x006D0000u && page < 0x00700000u) return "플레이어 자원/세력 부근";
        if (page < 0x00400000u) return "이미지/PE 헤더 아래 (비정상 — EPD 계산 확인 필요)";
        return "알 수 없음";
    }

    private static EudRegionStat GetEudStat(Dictionary<string, EudRegionStat> d, string region)
    {
        EudRegionStat s;
        if (!d.TryGetValue(region, out s)) { s = new EudRegionStat(); d[region] = s; }
        return s;
    }

    private static string EudRegionMeaning(string region)
    {
        switch (region)
        {
            case "CurrentPlayer": return "플레이어 루프 (CP 트릭 — 플레이어별 반복 처리)";
            case "DeathTable": return "EUD 내부 변수/카운터 (범용 메모리 저장소)";
            case "UnitTable": return "유닛 구조체 직접 조작 (HP·위치·상태·소유주)";
            case "UnitNodeTable": return "유닛 링크드리스트 조작";
            case "TriggerList": return "런타임 트리거 메모리 (VM 제어 흐름·자기수정)";
            case "TrigNextPtr": return "트리거 next 포인터 (제어 흐름 점프)";
            case "StringTable": return "문자열 테이블 동적 수정";
            case "PlayerResources": return "플레이어 자원 조작 (미네랄/가스)";
            case "PlayerOre/Gas": return "플레이어 자원량 조작";
            case "PlayerForce": return "플레이어 세력/동맹 조작";
            case "SwitchTable": return "스위치 상태 조작";
            case "MapTileData": return "맵 타일/지형 조작";
            case "LocationTable": return "로케이션 좌표 조작 (위치 이동)";
            case "SpriteTable": return "스프라이트/그래픽 직접 출력 (커스텀 그래픽)";
            case "DisplayText": return "화면 텍스트 출력 (커스텀 HUD/메시지)";
            case "ScreenX": return "카메라 X 좌표 조작 (화면 스크롤)";
            case "ScreenY": return "카메라 Y 좌표 조작 (화면 스크롤)";
            case "SupplyUsed": return "사용 보급(인구) 조작";
            case "SupplyMax": return "최대 보급(인구) 조작";
            case "CountdownTimer": return "카운트다운 타이머 조작";
            case "CompletedUnitCount": return "완성 유닛 수 조작";
            case "TotalUnitScore": return "유닛 점수 조작";
            case "UnitStateTables": return "유닛 카운트/스코어 테이블군 조작 (death table 인접 — 자원·진행도 추적)";
            case "PlayerForce ": return "플레이어 세력 조작";
            default: return region;
        }
    }

    // 어떤 영역들이 활성인지로 맵 성격을 한 줄로 추론한다.
    private static string InferEudPurpose(Dictionary<string, EudRegionStat> stat)
    {
        var parts = new List<string>();
        bool cp = stat.ContainsKey("CurrentPlayer");
        bool vm = stat.ContainsKey("TriggerList") || stat.ContainsKey("TrigNextPtr");
        bool vars = stat.ContainsKey("DeathTable");

        if (vm) parts.Add("런타임에 트리거 메모리를 직접 고쳐 제어 흐름을 만드는 **EUD VM 기반 맵**");
        else if (cp && vars) parts.Add("CP 루프 + death 변수로 동작하는 **EUD 스크립트 맵**");

        if (stat.ContainsKey("UnitTable") || stat.ContainsKey("UnitNodeTable"))
            parts.Add("유닛을 구조체 수준에서 직접 제어");
        if (stat.ContainsKey("UnitStateTables"))
            parts.Add("유닛 카운트/스코어 테이블로 진행도·자원 추적");
        if (stat.ContainsKey("SpriteTable"))
            parts.Add("커스텀 그래픽/스프라이트 출력");
        if (stat.ContainsKey("DisplayText"))
            parts.Add("커스텀 HUD/텍스트 출력");
        if (stat.ContainsKey("ScreenX") || stat.ContainsKey("ScreenY"))
            parts.Add("카메라 직접 조작");
        if (stat.ContainsKey("MapTileData"))
            parts.Add("지형 동적 변경");
        if (stat.ContainsKey("PlayerResources") || stat.ContainsKey("PlayerOre/Gas"))
            parts.Add("자원 직접 조작");

        if (parts.Count == 0)
            return "표준 트리거 범위 내 EUD 접근만 감지됨 (단순 변수 활용 수준).";
        return string.Join("; ", parts) + ".";
    }

    // v2: 사람이 짠 로직(GAME/DECRYPTED 트리거)의 조건/액션을 한 장에 풀어서 보여준다.
    // EUD-VM 잡음은 개수만 접어둔다.
    private static void WriteReportSignalTriggers(
        TextWriter w, byte[] trigData, int totalTriggers,
        Dictionary<string, List<byte[]>> grouped, string[] strings)
    {
        w.WriteLine("## 트리거 상세 (신호만)");
        w.WriteLine();

        if (trigData == null || totalTriggers == 0)
        {
            w.WriteLine("- TRIG 섹션 없음");
            w.WriteLine();
            return;
        }

        byte[] mrgn = GetFirstSection(grouped, "MRGN");

        var encryptedSet = new HashSet<int>();
        foreach (var info in CollectEncryptedFreezeTriggerInfos(trigData, totalTriggers))
            encryptedSet.Add(info.Index);

        int shown = 0, eudCollapsed = 0, disabledCollapsed = 0;

        for (int t = 0; t < totalTriggers; t++)
        {
            int off = t * FreezeTrigSize;
            uint execFlags = BitConverter.ToUInt32(trigData, off + 2368);
            bool isEncrypted = encryptedSet.Contains(t);
            bool isEud = IsFreezeEudTrigger(trigData, off);
            bool isDisabled = execFlags == 0;
            if (isDisabled)
                for (int p = 0; p < 28; p++)
                    if (trigData[off + 2372 + p] != 0) { isDisabled = false; break; }

            if (!isEncrypted && isDisabled) { disabledCollapsed++; continue; }
            if (!isEncrypted && isEud) { eudCollapsed++; continue; }

            // GAME 또는 DECRYPTED 트리거만 풀어서 출력
            shown++;
            var runFor = new List<string>();
            for (int p = 0; p < 28; p++)
                if (trigData[off + 2372 + p] != 0) runFor.Add(TriggerDumpExecSlotName(p));

            string tag = isEncrypted ? "DECRYPTED" : "GAME";
            w.WriteLine("### T" + t + " [" + tag + "]" +
                        (runFor.Count > 0 ? "  실행: " + string.Join(", ", runFor) : ""));
            w.WriteLine();

            bool hadCond = false;
            for (int c = 0; c < 16; c++)
            {
                int co = off + c * 20;
                if (trigData[co + 15] == 0) continue;
                if (!hadCond) { w.WriteLine("조건:"); hadCond = true; }
                w.WriteLine("- " + RenderCondition(trigData, co, strings, mrgn));
            }

            bool hadAct = false;
            for (int a = 0; a < 64; a++)
            {
                int ao = off + 320 + a * 32;
                if (trigData[ao + 26] == 0) continue;
                if (!hadAct) { w.WriteLine(hadCond ? "" : ""); w.WriteLine("액션:"); hadAct = true; }
                w.WriteLine("- " + RenderAction(trigData, ao, strings, mrgn));
            }

            if (!hadCond && !hadAct) w.WriteLine("(빈 트리거)");
            w.WriteLine();
        }

        if (shown == 0)
            w.WriteLine("표시할 GAME/DECRYPTED 트리거 없음.");
        w.WriteLine();
        w.WriteLine("접힘: EUD-VM " + eudCollapsed + "개, DISABLED " + disabledCollapsed +
                    "개 (전체 디코딩은 `--dump-all-triggers`).");
        w.WriteLine();
    }

    private static string ReportStrRef(string[] strings, uint id)
    {
        if (id == 0) return "(없음)";
        string s = ReportString(strings, (int)id);
        if (s == null) return "str#" + id;
        if (s.Length > 50) s = s.Substring(0, 50) + "…";
        return "\"" + s + "\"";
    }

    private static string ReportLocRef(uint id, byte[] mrgn, string[] strings)
    {
        if (id == 0) return "(no loc)";
        if (id == 64) return "Anywhere";
        if (mrgn != null)
        {
            int idx = (int)id - 1;
            int o = idx * 20;
            if (idx >= 0 && o + 18 <= mrgn.Length)
            {
                string nm = ReportString(strings, BitConverter.ToUInt16(mrgn, o + 16));
                if (nm != null) return "loc \"" + nm + "\"";
            }
        }
        return "loc#" + id;
    }

    private static string ReportEudNote(uint player, ushort unit)
    {
        if (player <= 27 && player != 13) return "";
        uint epd = unchecked(player + (uint)unit * 12u);
        uint mem = unchecked(0x0058A364u + epd * 4u);
        string region = DescribeScMemoryAddress(mem);
        string s = "  ⟶ EPD=" + epd + " mem=0x" + mem.ToString("X8");
        if (region != null) s += " [" + region + "]";
        if (player == 13) s += " (via CP)";
        return s;
    }

    private static string RenderCondition(byte[] d, int c, string[] strings, byte[] mrgn)
    {
        uint loc = BitConverter.ToUInt32(d, c + 0);
        uint val = BitConverter.ToUInt32(d, c + 4);
        uint grp = BitConverter.ToUInt32(d, c + 8);
        ushort unit = BitConverter.ToUInt16(d, c + 12);
        byte cmp = d[c + 14];
        byte type = d[c + 15];
        string P = TriggerDumpPlayerName(grp);
        string C = TriggerDumpCmpName(cmp);
        switch (type)
        {
            case 1: return "카운트다운 타이머 " + C + " " + val;
            case 2: return "Command: " + P + " " + C + " " + val + " x " + UnitName(unit);
            case 3: return "Bring: " + P + " " + C + " " + val + " x " + UnitName(unit) + " @ " + ReportLocRef(loc, mrgn, strings);
            case 4: return "Accumulate: " + P + " " + C + " " + val;
            case 5: return "Kill: " + P + " " + C + " " + val + " x " + UnitName(unit);
            case 11: return "Switch#" + val + " 상태 검사";
            case 12: return "ElapsedTime " + C + " " + val + "s";
            case 14: return "Opponents: " + P + " " + C + " " + val;
            case 15: return "Deaths: " + P + " " + UnitName(unit) + " " + C + " " + val + ReportEudNote(grp, unit);
            case 21: return "Score: " + P + " " + C + " " + val;
            case 22: return "Always";
            case 23: return "Never";
            default:
                return TriggerDumpCondTypeName(type) + " (type=" + type + " player=" + P +
                       " val=" + val + " unit=" + unit + " loc=" + loc + " cmp=" + cmp + ")";
        }
    }

    private static string RenderAction(byte[] d, int a, string[] strings, byte[] mrgn)
    {
        uint loc = BitConverter.ToUInt32(d, a + 0);
        uint str = BitConverter.ToUInt32(d, a + 4);
        uint wav = BitConverter.ToUInt32(d, a + 8);
        uint time = BitConverter.ToUInt32(d, a + 12);
        uint player = BitConverter.ToUInt32(d, a + 16);
        uint amount = BitConverter.ToUInt32(d, a + 20);
        ushort unit = BitConverter.ToUInt16(d, a + 24);
        byte type = d[a + 26];
        byte mod = d[a + 27];
        string P = TriggerDumpPlayerName(player);
        string M = TriggerDumpModName(mod);
        switch (type)
        {
            case 1: return "■ Victory";
            case 2: return "■ Defeat";
            case 3: return "PreserveTrigger";
            case 4: return "Wait " + time + "ms";
            case 5: return "PauseGame";
            case 6: return "UnpauseGame";
            case 7: return "Transmission: " + P + " " + ReportStrRef(strings, str) + " @ " + ReportLocRef(loc, mrgn, strings);
            case 8: return "PlayWAV " + ReportStrRef(strings, wav);
            case 9: return "DisplayText " + ReportStrRef(strings, str);
            case 10: return "CenterView @ " + ReportLocRef(loc, mrgn, strings);
            case 11: return "CreateUnitWithProps: " + amount + " x " + UnitName(unit) + " → " + P + " @ " + ReportLocRef(loc, mrgn, strings);
            case 12: return "SetMissionObjectives " + ReportStrRef(strings, str);
            case 13: return "SetSwitch#" + amount + " " + M;
            case 14: return "SetCountdownTimer " + M + " " + time;
            case 15: return "RunAIScript";
            case 22: return "KillUnit: " + P + " 모든 " + UnitName(unit);
            case 23: return "KillUnitAtLoc: " + P + " " + amount + " x " + UnitName(unit) + " @ " + ReportLocRef(loc, mrgn, strings);
            case 24: return "RemoveUnit: " + P + " 모든 " + UnitName(unit);
            case 25: return "RemoveUnitAtLoc: " + P + " " + amount + " x " + UnitName(unit) + " @ " + ReportLocRef(loc, mrgn, strings);
            case 26: return "SetResources: " + P + " " + M + " " + amount;
            case 27: return "SetScore: " + P + " " + M + " " + amount;
            case 28: return "MinimapPing @ " + ReportLocRef(loc, mrgn, strings);
            case 38: return "MoveLocation: loc#" + loc;
            case 39: return "MoveUnit: " + P + " " + amount + " x " + UnitName(unit);
            case 41: return "SetNextScenario " + ReportStrRef(strings, str);
            case 43: return "SetInvincibility: " + P + " " + UnitName(unit) + " " + M;
            case 44: return "CreateUnit: " + amount + " x " + UnitName(unit) + " → " + P + " @ " + ReportLocRef(loc, mrgn, strings);
            case 45: return "SetDeaths: " + P + " " + UnitName(unit) + " " + M + " " + amount + ReportEudNote(player, unit);
            case 46: return "Order: " + P + " " + UnitName(unit);
            case 47: return "// " + ReportStrRef(strings, str);
            case 48: return "GiveUnits: " + amount + " x " + UnitName(unit) + " → " + P;
            case 49: return "ModifyUnitHP: " + P + " " + UnitName(unit) + " " + amount + "%";
            default:
                return TriggerDumpActTypeName(type) + " (type=" + type + " player=" + P +
                       " unit=" + unit + " amount=" + amount + " loc=" + loc +
                       " str=" + str + " mod=" + mod + ")";
        }
    }

    private static string UnitName(int id)
    {
        if (id >= 0 && id < FreezeUnitNames.Length) return FreezeUnitNames[id];
        if (id == 0xFFFF) return "Any";
        return "unit#" + id;
    }

    private static readonly string[] FreezeUnitNames = {
        "Marine", "Ghost", "Vulture", "Goliath", "Goliath Turret",
        "Siege Tank(Tank)", "Tank Turret(Tank)", "SCV", "Wraith", "Science Vessel",
        "Gui Montag", "Dropship", "Battlecruiser", "Spider Mine", "Nuclear Missile",
        "Civilian", "Sarah Kerrigan", "Alan Schezar", "Schezar Turret", "Jim Raynor(Vulture)",
        "Jim Raynor(Marine)", "Tom Kazansky", "Magellan", "Edmund Duke(Tank)", "Duke Turret(Tank)",
        "Edmund Duke(Siege)", "Duke Turret(Siege)", "Arcturus Mengsk", "Hyperion", "Norad II",
        "Siege Tank(Siege)", "Tank Turret(Siege)", "Firebat", "Scanner Sweep", "Medic",
        "Larva", "Egg", "Zergling", "Hydralisk", "Ultralisk",
        "Broodling", "Drone", "Overlord", "Mutalisk", "Guardian",
        "Queen", "Defiler", "Scourge", "Torrasque", "Matriarch",
        "Infested Terran", "Infested Kerrigan", "Unclean One", "Hunter Killer", "Devouring One",
        "Kukulza(Muta)", "Kukulza(Guardian)", "Yggdrasill", "Valkyrie", "Cocoon",
        "Corsair", "Dark Templar", "Devourer", "Dark Archon", "Probe",
        "Zealot", "Dragoon", "High Templar", "Archon", "Shuttle",
        "Scout", "Arbiter", "Carrier", "Interceptor", "Dark Templar(Hero)",
        "Zeratul", "Tassadar/Zeratul(Archon)", "Fenix(Zealot)", "Fenix(Dragoon)", "Tassadar",
        "Mojo", "Warbringer", "Gantrithor", "Reaver", "Observer",
        "Scarab", "Danimoth", "Aldaris", "Artanis", "Rhynadon",
        "Bengalaas", "Cargo Ship(Unused)", "Mercenary Gunship(Unused)", "Scantid", "Kakaru",
        "Ragnasaur", "Ursadon", "Lurker Egg", "Raszagal", "Samir Duran",
        "Alexei Stukov", "Map Revealer", "Gerard DuGalle", "Lurker", "Infested Duran",
        "Disruption Web", "Command Center", "Comsat Station", "Nuclear Silo", "Supply Depot",
        "Refinery", "Barracks", "Academy", "Factory", "Starport",
        "Control Tower", "Science Facility", "Covert Ops", "Physics Lab", "Starbase(Unused)",
        "Machine Shop", "Repair Bay(Unused)", "Engineering Bay", "Armory", "Missile Turret",
        "Bunker", "Norad II(Crashed)", "Ion Cannon", "Uraj Crystal", "Khalis Crystal",
        "Infested Command Center", "Hatchery", "Lair", "Hive", "Nydus Canal",
        "Hydralisk Den", "Defiler Mound", "Greater Spire", "Queen's Nest", "Evolution Chamber",
        "Ultralisk Cavern", "Spire", "Spawning Pool", "Creep Colony", "Spore Colony",
        "Unused Zerg Bldg1", "Sunken Colony", "Overmind(Shell)", "Overmind", "Extractor",
        "Mature Chrysalis", "Cerebrate", "Cerebrate Daggoth", "Unused Zerg Bldg2", "Nexus",
        "Robotics Facility", "Pylon", "Assimilator", "Unused Protoss Bldg1", "Observatory",
        "Gateway", "Unused Protoss Bldg2", "Photon Cannon", "Citadel of Adun", "Cybernetics Core",
        "Templar Archives", "Forge", "Stargate", "Stasis Cell/Prison", "Fleet Beacon",
        "Arbiter Tribunal", "Robotics Support Bay", "Shield Battery", "Khaydarin Crystal Form.", "Temple",
        "Xel'Naga Temple", "Mineral Field 1", "Mineral Field 2", "Mineral Field 3", "Cave(Unused)",
        "Cave-in(Unused)", "Cantina(Unused)", "Mining Platform(Unused)", "Independent CC(Unused)", "Independent Starport(Unused)",
        "Independent Jump Gate(Unused)", "Ruins(Unused)", "Kyadarin Crystal(Unused)", "Vespene Geyser", "Warp Gate",
        "Psi Disrupter", "Zerg Marker", "Terran Marker", "Protoss Marker", "Zerg Beacon",
        "Terran Beacon", "Protoss Beacon", "Zerg Flag Beacon", "Terran Flag Beacon", "Protoss Flag Beacon",
        "Power Generator", "Overmind Cocoon", "Dark Swarm", "Floor Missile Trap", "Floor Hatch(Unused)",
        "Left Upper Level Door", "Right Upper Level Door", "Left Pit Door", "Right Pit Door", "Floor Gun Trap",
        "Left Wall Missile Trap", "Left Wall Flame Trap", "Right Wall Missile Trap", "Right Wall Flame Trap", "Start Location",
        "Flag", "Young Chrysalis", "Psi Emitter", "Data Disc", "Khaydarin Crystal",
        "Mineral Cluster 1", "Mineral Cluster 2", "Protoss Gas Orb 1", "Protoss Gas Orb 2", "Zerg Gas Sac 1",
        "Zerg Gas Sac 2", "Terran Gas Tank 1", "Terran Gas Tank 2"
    };

    private static string TilesetName(int t)
    {
        string[] n = { "Badlands", "Space Platform", "Installation", "Ashworld",
                       "Jungle", "Desert", "Ice", "Twilight" };
        return t >= 0 && t < n.Length ? n[t] : "Tileset(" + t + ")";
    }

    private static string OwnerName(byte o)
    {
        switch (o)
        {
            case 0: return "Inactive";
            case 3: return "Rescuable";
            case 5: return "Computer";
            case 6: return "Human";
            case 7: return "Neutral";
            default: return "Owner(" + o + ")";
        }
    }

    private static string RaceName(byte r)
    {
        switch (r)
        {
            case 0: return "Zerg";
            case 1: return "Terran";
            case 2: return "Protoss";
            case 3: return "Independent";
            case 4: return "Neutral";
            case 5: return "UserSelectable";
            case 6: return "Random";
            case 7: return "Inactive";
            default: return "Race(" + r + ")";
        }
    }
}
