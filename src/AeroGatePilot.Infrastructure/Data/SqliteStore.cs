using System.Globalization;
using AeroGatePilot.Core.Access;
using AeroGatePilot.Core.Filtering;
using AeroGatePilot.Core.Models;
using AeroGatePilot.Core.Security;
using Microsoft.Data.Sqlite;

namespace AeroGatePilot.Infrastructure.Data;

public sealed class SqliteStore : IAccountStore
{
    private readonly string _connectionString;

    public SqliteStore(string databaseFile)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public event Action? DataChanged;

    // ---------- Plans ----------

    public IReadOnlyList<Plan> GetPlans()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM plans ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var list = new List<Plan>();
        while (reader.Read())
            list.Add(ReadPlan(reader));
        return list;
    }

    public Plan? GetPlan(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM plans WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPlan(reader) : null;
    }

    public Plan SavePlan(Plan plan)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = plan.Id == 0
            ? """
              INSERT INTO plans (name, data_limit_mb, time_limit_minutes, session_limit_minutes, validity_days, download_kbps, upload_kbps, max_devices,
                site_filter, site_list, use_proxy)
              VALUES ($name, $data, $time, $session, $validity, $down, $up, $devices, $filter, $sites, $proxy);
              SELECT last_insert_rowid();
              """
            : """
              UPDATE plans SET name = $name, data_limit_mb = $data, time_limit_minutes = $time, session_limit_minutes = $session,
                validity_days = $validity, download_kbps = $down, upload_kbps = $up, max_devices = $devices,
                site_filter = $filter, site_list = $sites, use_proxy = $proxy
              WHERE id = $id;
              SELECT $id;
              """;
        cmd.Parameters.AddWithValue("$id", plan.Id);
        cmd.Parameters.AddWithValue("$name", plan.Name.Trim());
        cmd.Parameters.AddWithValue("$data", Math.Max(0, plan.DataLimitMb));
        cmd.Parameters.AddWithValue("$time", Math.Max(0, plan.TimeLimitMinutes));
        cmd.Parameters.AddWithValue("$session", Math.Max(0, plan.SessionLimitMinutes));
        cmd.Parameters.AddWithValue("$validity", Math.Max(0, plan.ValidityDays));
        cmd.Parameters.AddWithValue("$down", Math.Max(0, plan.DownloadKbps));
        cmd.Parameters.AddWithValue("$up", Math.Max(0, plan.UploadKbps));
        cmd.Parameters.AddWithValue("$devices", Math.Max(0, plan.MaxDevices));
        cmd.Parameters.AddWithValue("$filter", (int)plan.SiteFilter);
        cmd.Parameters.AddWithValue("$sites", plan.SiteList?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$proxy", plan.UseUpstreamProxy ? 1 : 0);
        plan.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        DataChanged?.Invoke();
        return plan;
    }

    /// <summary>Deletes a plan. Returns false when accounts still use it.</summary>
    public bool DeletePlan(long id)
    {
        using var conn = Open();
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM users WHERE plan_id = $id AND is_guest = 0";
            check.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                return false;
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE plan_id = $id AND is_guest = 1; DELETE FROM plans WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        DataChanged?.Invoke();
        return true;
    }

    // ---------- Users ----------

    public IReadOnlyList<UserAccount> GetUsers(bool includeGuests)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = includeGuests
            ? "SELECT * FROM users ORDER BY is_guest, username COLLATE NOCASE"
            : "SELECT * FROM users WHERE is_guest = 0 ORDER BY username COLLATE NOCASE";
        using var reader = cmd.ExecuteReader();
        var list = new List<UserAccount>();
        while (reader.Read())
            list.Add(ReadUser(reader));
        return list;
    }

    public UserAccount? GetUser(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public UserAccount? FindUserByName(string username)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE username = $u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$u", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public bool UsernameExists(string username, long exceptId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE username = $u COLLATE NOCASE AND id <> $id";
        cmd.Parameters.AddWithValue("$u", username.Trim());
        cmd.Parameters.AddWithValue("$id", exceptId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Creates or updates an account. <paramref name="newPassword"/> replaces the password when not empty.</summary>
    public UserAccount SaveUser(UserAccount user, string? newPassword)
    {
        if (!string.IsNullOrEmpty(newPassword))
            user.PasswordHash = PasswordHasher.Hash(newPassword);
        if (user.Id == 0 && string.IsNullOrEmpty(user.PasswordHash))
            throw new InvalidOperationException("A new account needs a password.");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = user.Id == 0
            ? """
              INSERT INTO users (username, password_hash, display_name, plan_id, enabled, is_guest, used_download, used_upload, used_seconds, first_login_utc, created_utc, notes)
              VALUES ($username, $hash, $display, $plan, $enabled, $guest, 0, 0, 0, NULL, $created, $notes);
              SELECT last_insert_rowid();
              """
            : """
              UPDATE users SET username = $username, password_hash = $hash, display_name = $display, plan_id = $plan,
                enabled = $enabled, notes = $notes
              WHERE id = $id;
              SELECT $id;
              """;
        cmd.Parameters.AddWithValue("$id", user.Id);
        cmd.Parameters.AddWithValue("$username", user.Username.Trim());
        cmd.Parameters.AddWithValue("$hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("$display", user.DisplayName.Trim());
        cmd.Parameters.AddWithValue("$plan", user.PlanId);
        cmd.Parameters.AddWithValue("$enabled", user.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$guest", user.IsGuest ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", ToDb(user.CreatedUtc));
        cmd.Parameters.AddWithValue("$notes", user.Notes ?? "");
        user.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        DataChanged?.Invoke();
        return user;
    }

    public void DeleteUser(long id)
    {
        Execute("DELETE FROM users WHERE id = $id", ("$id", id));
        DataChanged?.Invoke();
    }

    /// <summary>Clears consumed data/time and restarts the validity period on next login.</summary>
    public void ResetUsage(long id)
    {
        Execute("UPDATE users SET used_download = 0, used_upload = 0, used_seconds = 0, first_login_utc = NULL WHERE id = $id", ("$id", id));
        DataChanged?.Invoke();
    }

    public void DeleteGuests()
    {
        Execute("DELETE FROM users WHERE is_guest = 1");
        DataChanged?.Invoke();
    }

    // ---------- IAccountStore ----------

    public void AddUsage(long userId, long downloadBytes, long uploadBytes, long seconds) =>
        Execute(
            "UPDATE users SET used_download = used_download + $d, used_upload = used_upload + $u, used_seconds = used_seconds + $s WHERE id = $id",
            ("$d", downloadBytes), ("$u", uploadBytes), ("$s", seconds), ("$id", userId));

    public void MarkFirstLogin(long userId, DateTime utc) =>
        Execute("UPDATE users SET first_login_utc = $t WHERE id = $id AND first_login_utc IS NULL", ("$t", ToDb(utc)), ("$id", userId));

    public UserAccount GetOrCreateGuest(string deviceKey, long planId)
    {
        var username = "guest:" + deviceKey;
        var existing = FindUserByName(username);
        if (existing is not null)
        {
            if (existing.PlanId != planId)
            {
                existing.PlanId = planId;
                SaveUser(existing, null);
            }
            return existing;
        }

        var guest = new UserAccount
        {
            Username = username,
            DisplayName = "Guest",
            PlanId = planId,
            IsGuest = true,
            PasswordHash = "!",
            Notes = "Created automatically by guest login",
        };
        return SaveUser(guest, null);
    }

    public void AddHistory(SessionHistoryEntry entry)
    {
        Execute(
            """
            INSERT INTO session_history (user_id, username, ip, mac, started_utc, ended_utc, download_bytes, upload_bytes, end_reason)
            VALUES ($user, $name, $ip, $mac, $start, $end, $down, $up, $reason)
            """,
            ("$user", entry.UserId), ("$name", entry.Username), ("$ip", entry.Ip), ("$mac", entry.Mac),
            ("$start", ToDb(entry.StartedUtc)), ("$end", ToDb(entry.EndedUtc)),
            ("$down", entry.DownloadBytes), ("$up", entry.UploadBytes), ("$reason", entry.EndReason));
        DataChanged?.Invoke();
    }

    public IReadOnlyList<SessionHistoryEntry> GetHistory(int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM session_history ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<SessionHistoryEntry>();
        while (reader.Read())
        {
            list.Add(new SessionHistoryEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                UserId = reader.GetInt64(reader.GetOrdinal("user_id")),
                Username = reader.GetString(reader.GetOrdinal("username")),
                Ip = reader.GetString(reader.GetOrdinal("ip")),
                Mac = reader.GetString(reader.GetOrdinal("mac")),
                StartedUtc = FromDb(reader.GetString(reader.GetOrdinal("started_utc"))),
                EndedUtc = FromDb(reader.GetString(reader.GetOrdinal("ended_utc"))),
                DownloadBytes = reader.GetInt64(reader.GetOrdinal("download_bytes")),
                UploadBytes = reader.GetInt64(reader.GetOrdinal("upload_bytes")),
                EndReason = reader.GetString(reader.GetOrdinal("end_reason")),
            });
        }
        return list;
    }

    // ---------- Internals ----------

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS plans (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                data_limit_mb INTEGER NOT NULL DEFAULT 0,
                time_limit_minutes INTEGER NOT NULL DEFAULT 0,
                session_limit_minutes INTEGER NOT NULL DEFAULT 0,
                validity_days INTEGER NOT NULL DEFAULT 0,
                download_kbps INTEGER NOT NULL DEFAULT 0,
                upload_kbps INTEGER NOT NULL DEFAULT 0,
                max_devices INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                plan_id INTEGER NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                is_guest INTEGER NOT NULL DEFAULT 0,
                used_download INTEGER NOT NULL DEFAULT 0,
                used_upload INTEGER NOT NULL DEFAULT 0,
                used_seconds INTEGER NOT NULL DEFAULT 0,
                first_login_utc TEXT NULL,
                created_utc TEXT NOT NULL,
                notes TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS session_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                username TEXT NOT NULL,
                ip TEXT NOT NULL,
                mac TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                ended_utc TEXT NOT NULL,
                download_bytes INTEGER NOT NULL,
                upload_bytes INTEGER NOT NULL,
                end_reason TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_user ON session_history(user_id);
            """;
        cmd.ExecuteNonQuery();

        AddColumnIfMissing(conn, "plans", "site_filter", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "plans", "site_list", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "plans", "use_proxy", "INTEGER NOT NULL DEFAULT 0");

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM plans";
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            SeedPlans();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        check.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            return;
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private void SeedPlans()
    {
        SavePlan(new Plan { Name = "Guest · 30 min", SessionLimitMinutes = 30, DataLimitMb = 300, DownloadKbps = 4000, UploadKbps = 1000, MaxDevices = 1 });
        SavePlan(new Plan { Name = "Daily · 2 GB", DataLimitMb = 2048, ValidityDays = 1, DownloadKbps = 10000, UploadKbps = 3000, MaxDevices = 2 });
        SavePlan(new Plan { Name = "Monthly · 30 GB", DataLimitMb = 30720, ValidityDays = 30, DownloadKbps = 20000, UploadKbps = 5000, MaxDevices = 3 });
        SavePlan(new Plan { Name = "Unlimited", MaxDevices = 5 });
    }

    private static Plan ReadPlan(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        DataLimitMb = r.GetInt64(r.GetOrdinal("data_limit_mb")),
        TimeLimitMinutes = r.GetInt32(r.GetOrdinal("time_limit_minutes")),
        SessionLimitMinutes = r.GetInt32(r.GetOrdinal("session_limit_minutes")),
        ValidityDays = r.GetInt32(r.GetOrdinal("validity_days")),
        DownloadKbps = r.GetInt32(r.GetOrdinal("download_kbps")),
        UploadKbps = r.GetInt32(r.GetOrdinal("upload_kbps")),
        MaxDevices = r.GetInt32(r.GetOrdinal("max_devices")),
        SiteFilter = Enum.IsDefined(typeof(SiteFilterMode), r.GetInt32(r.GetOrdinal("site_filter")))
            ? (SiteFilterMode)r.GetInt32(r.GetOrdinal("site_filter"))
            : SiteFilterMode.Off,
        SiteList = r.GetString(r.GetOrdinal("site_list")),
        UseUpstreamProxy = r.GetInt32(r.GetOrdinal("use_proxy")) != 0,
    };

    private static UserAccount ReadUser(SqliteDataReader r)
    {
        var firstLogin = r.GetOrdinal("first_login_utc");
        return new UserAccount
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Username = r.GetString(r.GetOrdinal("username")),
            PasswordHash = r.GetString(r.GetOrdinal("password_hash")),
            DisplayName = r.GetString(r.GetOrdinal("display_name")),
            PlanId = r.GetInt64(r.GetOrdinal("plan_id")),
            Enabled = r.GetInt64(r.GetOrdinal("enabled")) != 0,
            IsGuest = r.GetInt64(r.GetOrdinal("is_guest")) != 0,
            UsedDownloadBytes = r.GetInt64(r.GetOrdinal("used_download")),
            UsedUploadBytes = r.GetInt64(r.GetOrdinal("used_upload")),
            UsedSeconds = r.GetInt64(r.GetOrdinal("used_seconds")),
            FirstLoginUtc = r.IsDBNull(firstLogin) ? null : FromDb(r.GetString(firstLogin)),
            CreatedUtc = FromDb(r.GetString(r.GetOrdinal("created_utc"))),
            Notes = r.GetString(r.GetOrdinal("notes")),
        };
    }

    private static string ToDb(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromDb(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
