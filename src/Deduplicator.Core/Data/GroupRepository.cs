using Microsoft.Data.Sqlite;
using Deduplicator.Core.Models;

namespace Deduplicator.Core.Data;

/// <summary>Группы дубликатов и их члены.</summary>
public sealed class GroupRepository
{
    private readonly Database _db;
    public GroupRepository(Database db) => _db = db;

    /// <summary>Удалить ранее построенные группы указанного типа (перед пересчётом).</summary>
    public void ClearByMatchType(MatchType type)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM duplicate_groups WHERE match_type=$t";
        cmd.Add("$t", Mappers.MatchTypeToDb(type));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Удалить группы ОДНОГО типа ТОЛЬКО текущей сессии (перед пересборкой этого типа в рамках скана).
    /// В отличие от глобального <see cref="ClearByMatchType"/> НЕ трогает результаты других сессий —
    /// поэтому пересборка в идущем скане не стирает результаты прошлой ЗАВЕРШЁННОЙ сессии до его успеха.
    /// </summary>
    public void ClearByMatchTypeInSession(long sessionId, MatchType type)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM duplicate_groups WHERE match_type=$t AND scan_session_id=$sid";
        cmd.Add("$t", Mappers.MatchTypeToDb(type));
        cmd.Add("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM duplicate_groups";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Удалить группы всех прошлых сессий, оставив только текущую. Вызывается ПОСЛЕ успешного скана,
    /// чтобы старые группы (напр. «похожие» из предыдущего запуска) не показывались в результатах
    /// нового скана и не накапливались в БД.
    /// </summary>
    public void ClearGroupsNotInSession(long sessionId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM duplicate_groups WHERE scan_session_id IS NULL OR scan_session_id <> $sid";
        cmd.Add("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Удалить группы ТЕКУЩЕЙ сессии — для отмены/сбоя скана: частично построенные группы прерванного
    /// скана не должны показываться как готовый результат (их можно было бы отметить/удалить).
    /// </summary>
    public void ClearGroupsInSession(long sessionId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM duplicate_groups WHERE scan_session_id = $sid";
        cmd.Add("$sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Старт приложения (P0.2): удалить группы, чья сессия НЕ завершилась успешно — частичные группы
    /// от убитого/упавшего скана не должны переживать рестарт. (Члены удаляются каскадом по FK.)
    /// </summary>
    public void ClearGroupsNotInCompletedSessions()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM duplicate_groups
            WHERE scan_session_id IS NULL
               OR scan_session_id NOT IN (SELECT id FROM scan_sessions WHERE stage=$done)
            """;
        cmd.Add("$done", (long)ScanStage.Completed);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сохранить группы пачкой в одной транзакции, помечая сессией скана (scope-aware результаты).</summary>
    public void InsertGroups(IReadOnlyList<DuplicateGroup> groups, long? sessionId = null)
    {
        if (groups.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using var gCmd = conn.CreateCommand();
        gCmd.Transaction = tx;
        gCmd.CommandText = """
            INSERT INTO duplicate_groups (match_type, confidence, representative, total_bytes, created_at, scan_session_id)
            VALUES ($mt, $conf, $rep, $tb, $ts, $sid);
            SELECT last_insert_rowid();
            """;
        var pMt = gCmd.CreateParameter(); pMt.ParameterName = "$mt"; gCmd.Parameters.Add(pMt);
        var pConf = gCmd.CreateParameter(); pConf.ParameterName = "$conf"; gCmd.Parameters.Add(pConf);
        var pRep = gCmd.CreateParameter(); pRep.ParameterName = "$rep"; gCmd.Parameters.Add(pRep);
        var pTb = gCmd.CreateParameter(); pTb.ParameterName = "$tb"; gCmd.Parameters.Add(pTb);
        var pTs = gCmd.CreateParameter(); pTs.ParameterName = "$ts"; gCmd.Parameters.Add(pTs);
        var pSid = gCmd.CreateParameter(); pSid.ParameterName = "$sid"; pSid.Value = (object?)sessionId ?? DBNull.Value; gCmd.Parameters.Add(pSid);

        using var mCmd = conn.CreateCommand();
        mCmd.Transaction = tx;
        mCmd.CommandText =
            "INSERT INTO duplicate_members (group_id, file_id, similarity) VALUES ($g, $f, $s)";
        var mG = mCmd.CreateParameter(); mG.ParameterName = "$g"; mCmd.Parameters.Add(mG);
        var mF = mCmd.CreateParameter(); mF.ParameterName = "$f"; mCmd.Parameters.Add(mF);
        var mS = mCmd.CreateParameter(); mS.ParameterName = "$s"; mCmd.Parameters.Add(mS);

        var now = Db.IsoNow();
        foreach (var g in groups)
        {
            pMt.Value = Mappers.MatchTypeToDb(g.MatchType);
            pConf.Value = g.Confidence;
            pRep.Value = (object?)g.RepresentativeId ?? DBNull.Value;
            pTb.Value = g.ReclaimableBytes;
            pTs.Value = now;
            var gid = (long)gCmd.ExecuteScalar()!;

            mG.Value = gid;
            foreach (var m in g.Members)
            {
                mF.Value = m.FileId;
                mS.Value = m.Similarity;
                mCmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>
    /// Загрузить группы заданного типа с членами и слепками файлов. Если задан <paramref name="sessionId"/>,
    /// читаем ТОЛЬКО группы этой сессии (P0.2: результаты — только последнего завершённого скана; даже если
    /// после краша остались частичные группы другой сессии, они не попадут в выдачу).
    /// </summary>
    public List<DuplicateGroup> GetGroups(MatchType type, long? sessionId = null)
    {
        using var conn = _db.Open();

        var groups = new List<DuplicateGroup>();
        var byId = new Dictionary<long, DuplicateGroup>();
        using (var gCmd = conn.CreateCommand())
        {
            gCmd.CommandText =
                "SELECT id, match_type, confidence, representative, total_bytes, created_at " +
                "FROM duplicate_groups WHERE match_type=$t" +
                (sessionId.HasValue ? " AND scan_session_id=$sid" : "") +
                " ORDER BY total_bytes DESC";
            gCmd.Add("$t", Mappers.MatchTypeToDb(type));
            if (sessionId.HasValue) gCmd.Add("$sid", sessionId.Value);
            using var r = gCmd.ExecuteReader();
            while (r.Read())
            {
                var g = new DuplicateGroup
                {
                    Id = r.Long(0),
                    MatchType = Mappers.ParseMatchType(r.Str(1)),
                    Confidence = r.Dbl(2),
                    RepresentativeId = r.LongN(3),
                    ReclaimableBytes = r.Long(4),
                    CreatedAt = Db.ParseDate(r.Str(5))
                };
                groups.Add(g);
                byId[g.Id] = g;
            }
        }
        if (groups.Count == 0) return groups;

        using (var mCmd = conn.CreateCommand())
        {
            mCmd.CommandText = """
                SELECT m.group_id, m.file_id, m.similarity,
                       f.id, f.volume_id, f.rel_path, f.name, f.ext, f.size, f.mtime, f.created, f.category,
                       f.partial_hash, f.full_hash, f.phash, f.pixel_hash, f.audio_fp, f.scan_state, f.last_indexed
                FROM duplicate_members m
                JOIN duplicate_groups g ON g.id = m.group_id
                JOIN files f ON f.id = m.file_id
                WHERE g.match_type = $t
                """ + (sessionId.HasValue ? " AND g.scan_session_id=$sid" : "");
            mCmd.Add("$t", Mappers.MatchTypeToDb(type));
            if (sessionId.HasValue) mCmd.Add("$sid", sessionId.Value);
            using var r = mCmd.ExecuteReader();
            while (r.Read())
            {
                var groupId = r.Long(0);
                if (!byId.TryGetValue(groupId, out var g)) continue;
                var file = new FileRecord
                {
                    Id = r.Long(3),
                    VolumeId = r.Long(4),
                    RelPath = r.Str(5) ?? string.Empty,
                    Name = r.Str(6) ?? string.Empty,
                    Ext = r.Str(7),
                    Size = r.Long(8),
                    Mtime = Db.ParseDate(r.Str(9)),
                    Created = Db.ParseDate(r.Str(10)),
                    Category = Mappers.ParseCategory(r.Str(11)),
                    PartialHash = r.Blob(12),
                    FullHash = r.Blob(13),
                    PHash = r.Blob(14),
                    PixelHash = r.Blob(15),
                    AudioFp = r.Blob(16),
                    ScanState = Mappers.ParseState(r.Str(17)),
                    LastIndexed = Db.ParseDateOrNull(r.Str(18))
                };
                g.Members.Add(new DuplicateMember
                {
                    GroupId = groupId,
                    FileId = file.Id,
                    Similarity = r.Dbl(2),
                    IsRepresentative = g.RepresentativeId == file.Id,
                    File = file
                });
            }
        }
        return groups;
    }

    /// <summary>Сколько групп указанных типов (для пагинации, P1.1).</summary>
    public int GetGroupCount(IReadOnlyList<MatchType> types, long? sessionId = null)
    {
        if (types.Count == 0) return 0;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var names = TypeParams(cmd, types);
        cmd.CommandText = $"SELECT COUNT(*) FROM duplicate_groups WHERE match_type IN ({names})" +
            (sessionId.HasValue ? " AND scan_session_id=$sid" : "");
        if (sessionId.HasValue) cmd.Add("$sid", sessionId.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Страница групп (P1.1): из БД берутся только offset..offset+limit групп указанных типов И их члены —
    /// память пропорциональна странице, а не всему результату. Порядок: по освобождаемому объёму.
    /// </summary>
    public List<DuplicateGroup> GetGroupsPage(IReadOnlyList<MatchType> types, long? sessionId, int offset, int limit)
    {
        var groups = new List<DuplicateGroup>();
        if (types.Count == 0) return groups;

        using var conn = _db.Open();
        var byId = new Dictionary<long, DuplicateGroup>();
        using (var gCmd = conn.CreateCommand())
        {
            var names = TypeParams(gCmd, types);
            gCmd.CommandText =
                "SELECT id, match_type, confidence, representative, total_bytes, created_at " +
                $"FROM duplicate_groups WHERE match_type IN ({names})" +
                (sessionId.HasValue ? " AND scan_session_id=$sid" : "") +
                " ORDER BY total_bytes DESC, id DESC LIMIT $lim OFFSET $off";
            if (sessionId.HasValue) gCmd.Add("$sid", sessionId.Value);
            gCmd.Add("$lim", limit);
            gCmd.Add("$off", offset);
            using var r = gCmd.ExecuteReader();
            while (r.Read())
            {
                var g = new DuplicateGroup
                {
                    Id = r.Long(0),
                    MatchType = Mappers.ParseMatchType(r.Str(1)),
                    Confidence = r.Dbl(2),
                    RepresentativeId = r.LongN(3),
                    ReclaimableBytes = r.Long(4),
                    CreatedAt = Db.ParseDate(r.Str(5))
                };
                groups.Add(g);
                byId[g.Id] = g;
            }
        }
        if (groups.Count == 0) return groups;

        // Члены только для групп этой страницы (id из БД — безопасные числа).
        using (var mCmd = conn.CreateCommand())
        {
            mCmd.CommandText = $"""
                SELECT m.group_id, m.file_id, m.similarity,
                       f.id, f.volume_id, f.rel_path, f.name, f.ext, f.size, f.mtime, f.created, f.category,
                       f.partial_hash, f.full_hash, f.phash, f.pixel_hash, f.audio_fp, f.scan_state, f.last_indexed
                FROM duplicate_members m JOIN files f ON f.id = m.file_id
                WHERE m.group_id IN ({string.Join(",", byId.Keys)})
                ORDER BY m.group_id, m.file_id
                """;
            using var r = mCmd.ExecuteReader();
            while (r.Read())
            {
                var groupId = r.Long(0);
                if (!byId.TryGetValue(groupId, out var g)) continue;
                var file = new FileRecord
                {
                    Id = r.Long(3), VolumeId = r.Long(4), RelPath = r.Str(5) ?? string.Empty,
                    Name = r.Str(6) ?? string.Empty, Ext = r.Str(7), Size = r.Long(8),
                    Mtime = Db.ParseDate(r.Str(9)), Created = Db.ParseDate(r.Str(10)),
                    Category = Mappers.ParseCategory(r.Str(11)), PartialHash = r.Blob(12),
                    FullHash = r.Blob(13), PHash = r.Blob(14), PixelHash = r.Blob(15),
                    AudioFp = r.Blob(16), ScanState = Mappers.ParseState(r.Str(17)),
                    LastIndexed = Db.ParseDateOrNull(r.Str(18))
                };
                g.Members.Add(new DuplicateMember
                {
                    GroupId = groupId, FileId = file.Id, Similarity = r.Dbl(2),
                    IsRepresentative = g.RepresentativeId == file.Id, File = file
                });
            }
        }
        return groups;
    }

    /// <summary>Перегрузка на ОДИН тип (похожие фото/музыка) — оборачивает версию со списком типов (P1 пагинация).</summary>
    public int GetGroupCount(MatchType type, long? sessionId = null) =>
        GetGroupCount(new[] { type }, sessionId);

    /// <summary>Страница групп одного типа (похожие фото/музыка) — для постраничной выдачи без загрузки всего набора.</summary>
    public List<DuplicateGroup> GetGroupsPage(MatchType type, long? sessionId, int offset, int limit) =>
        GetGroupsPage(new[] { type }, sessionId, offset, limit);

    private static string TypeParams(Microsoft.Data.Sqlite.SqliteCommand cmd, IReadOnlyList<MatchType> types)
    {
        var names = new List<string>(types.Count);
        for (int i = 0; i < types.Count; i++)
        {
            var n = "$t" + i;
            names.Add(n);
            cmd.Add(n, Mappers.MatchTypeToDb(types[i]));
        }
        return string.Join(",", names);
    }

    /// <summary>Удалить запись о члене группы (после физического удаления файла).</summary>
    public void RemoveMember(long groupId, long fileId)
    {
        using var conn = _db.Open();
        // Удаление члена и возможный роспуск группы — атомарно (без гонок).
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM duplicate_members WHERE group_id=$g AND file_id=$f";
            cmd.Add("$g", groupId);
            cmd.Add("$f", fileId);
            cmd.ExecuteNonQuery();
        }

        long remaining;
        using (var chk = conn.CreateCommand())
        {
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM duplicate_members WHERE group_id=$g";
            chk.Add("$g", groupId);
            remaining = (long)chk.ExecuteScalar()!;
        }

        // Если в группе осталось меньше двух членов — она больше не группа дубликатов.
        if (remaining < 2)
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM duplicate_groups WHERE id=$g";
            del.Add("$g", groupId);
            del.ExecuteNonQuery();
        }
        else
        {
            // Если удалили эталон — назначаем нового из оставшихся (иначе representative повиснет/обнулится
            // по ON DELETE SET NULL, и пакетная отметка «все кроме эталона» отметит ВСЕ файлы).
            using var fix = conn.CreateCommand();
            fix.Transaction = tx;
            fix.CommandText = """
                UPDATE duplicate_groups
                SET representative = (SELECT file_id FROM duplicate_members WHERE group_id=$g ORDER BY file_id LIMIT 1)
                WHERE id=$g AND (representative=$f OR representative IS NULL);
                """;
            fix.Add("$g", groupId);
            fix.Add("$f", fileId);
            fix.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Роспуск вырожденных групп после каскадного удаления файлов (например, «Забыть носитель»):
    /// FK-каскад удаляет files/duplicate_members, но строки duplicate_groups остаются — пустые и
    /// одиночные группы показывались в результатах, статистика считала устаревший total_bytes, а
    /// осиротевший эталон (ON DELETE SET NULL) позволял «Выбрать все» отметить ВСЕ файлы группы.
    /// Возвращает число распущенных групп.
    /// </summary>
    public int DissolveDegenerateGroups()
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        int removed;
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = """
                DELETE FROM duplicate_groups
                WHERE (SELECT COUNT(*) FROM duplicate_members m WHERE m.group_id = duplicate_groups.id) < 2
                """;
            removed = del.ExecuteNonQuery();
        }

        using (var fix = conn.CreateCommand())
        {
            fix.Transaction = tx;
            fix.CommandText = """
                UPDATE duplicate_groups
                SET representative = (SELECT file_id FROM duplicate_members m
                                      WHERE m.group_id = duplicate_groups.id ORDER BY file_id LIMIT 1)
                WHERE representative IS NULL
                """;
            fix.ExecuteNonQuery();
        }

        using (var tb = conn.CreateCommand())
        {
            tb.Transaction = tx;
            tb.CommandText = """
                UPDATE duplicate_groups
                SET total_bytes = (SELECT COALESCE(SUM(f.size), 0)
                                   FROM duplicate_members m JOIN files f ON f.id = m.file_id
                                   WHERE m.group_id = duplicate_groups.id)
                """;
            tb.ExecuteNonQuery();
        }

        tx.Commit();
        return removed;
    }

    // Подзапрос «последняя успешно завершённая сессия» — чтобы статистика считалась по ТЕМ ЖЕ группам,
    // что показывает экран результатов (Home/Статистика не должны расходиться с Results).
    private const string LatestCompletedSub =
        "(SELECT id FROM scan_sessions WHERE stage=$done ORDER BY id DESC LIMIT 1)"; // по id: finished_at — локальная строка, ломается при смене пояса

    /// <summary>Статистика по группам для сводного экрана — только последний завершённый скан.</summary>
    public DuplicateStatistics GetStatistics()
    {
        using var conn = _db.Open();
        var stats = new DuplicateStatistics();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT match_type, COUNT(*), COALESCE(SUM(total_bytes),0) FROM duplicate_groups " +
            "WHERE scan_session_id = " + LatestCompletedSub + " GROUP BY match_type";
        cmd.Add("$done", (long)ScanStage.Completed);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var mt = Mappers.ParseMatchType(r.Str(0));
            int count = (int)r.Long(1);
            long bytes = r.Long(2);
            switch (mt)
            {
                case MatchType.Exact: stats.ExactGroups = count; stats.ExactReclaimableBytes = bytes; break;
                case MatchType.SimilarImage: stats.SimilarImageGroups = count; stats.SimilarImageReclaimableBytes = bytes; break;
                case MatchType.SimilarAudio: stats.SimilarAudioGroups = count; stats.SimilarAudioReclaimableBytes = bytes; break;
                case MatchType.ExactVideo: stats.ExactVideoGroups = count; stats.ExactVideoReclaimableBytes = bytes; break;
            }
        }

        using var cnt = conn.CreateCommand();
        cnt.CommandText =
            "SELECT COUNT(*) FROM duplicate_members m JOIN duplicate_groups g ON g.id = m.group_id " +
            "WHERE g.scan_session_id = " + LatestCompletedSub;
        cnt.Add("$done", (long)ScanStage.Completed);
        stats.TotalDuplicateFiles = (long)cnt.ExecuteScalar()!;
        return stats;
    }
}
