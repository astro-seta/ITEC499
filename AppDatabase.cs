// =====================================================================
// MedReminder: data layer for the MAUI app (offline-first)
// NuGet: sqlite-net-pcl, SQLitePCLRaw.bundle_green
// Pairs with medreminder_server.sql (PostgreSQL). The [JsonPropertyName]
// values in the *Dto classes are exactly the server's column names.
//
// How the two sides line up
//   App (SQLite, via sqlite-net)      Wire (JSON)               Server (PostgreSQL)
//   Guid  (stored as TEXT)        ->  "id": "uuid-string"   ->  UUID
//   DateTime UTC (stored as ticks)->  ISO-8601 with 'Z'     ->  TIMESTAMPTZ
//   DateTime date part            ->  "yyyy-MM-dd"          ->  DATE
//   TimeSpan (stored as ticks)    ->  "HH:mm:ss"            ->  TIME
//   enum DoseStatus (INTEGER)     ->  0..3                  ->  SMALLINT
//   bool (INTEGER 0/1)            ->  true/false            ->  BOOLEAN
//
// App-only columns (never sent):    IsDirty
// Server-only columns (never in the app): user_id (from the login token),
//                                         server_updated_at (sync cursor)
// =====================================================================

using System.Globalization;
using System.Text.Json.Serialization;
using SQLite;

namespace MedReminder.Data;

// ---------- Field limits: must match the server's VARCHAR sizes ----------
public static class Limits
{
    public const int MedicineName = 150;   // medicines.name, dose_logs.medicine_name
    public const int Dosage = 100;         // medicines.dosage, dose_logs.dosage
}

// =====================================================================
// 1. SQLite tables (sqlite-net creates these with CreateTableAsync<T>())
// =====================================================================

public abstract class SyncEntity
{
    [PrimaryKey] public Guid Id { get; set; } = Guid.NewGuid();

    // Always UTC. Call Touch() before every save so the row syncs.
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Soft delete, so deletions can sync to the server.
    public bool IsDeleted { get; set; }

    // true = changed on the phone, not yet pushed.
    [Indexed] public bool IsDirty { get; set; } = true;

    public void Touch()
    {
        UpdatedAt = DateTime.UtcNow;
        IsDirty = true;
    }
}

public class Medicine : SyncEntity
{
    [NotNull, MaxLength(Limits.MedicineName)] public string Name { get; set; } = "";
    [MaxLength(Limits.Dosage)] public string? Dosage { get; set; }
    public string? Notes { get; set; }
    public DateTime StartDate { get; set; } = DateTime.Today;   // only the date part is synced
    public DateTime? EndDate { get; set; }                       // only the date part is synced
    public bool IsActive { get; set; } = true;
}

public class Reminder : SyncEntity
{
    [Indexed] public Guid MedicineId { get; set; }
    public TimeSpan TimeOfDay { get; set; }                      // e.g. 08:00:00
    public int DaysOfWeek { get; set; } = 127;                   // bitmask 1..127: Mon=1 Tue=2 Wed=4 Thu=8 Fri=16 Sat=32 Sun=64
    public bool IsEnabled { get; set; } = true;
}

public enum DoseStatus { Pending = 0, Taken = 1, Skipped = 2, Missed = 3 }

public class DoseLog : SyncEntity
{
    [Indexed] public Guid MedicineId { get; set; }
    public Guid? ReminderId { get; set; }

    // Snapshot of the medicine at scheduling time, so history stays correct
    // even if the medicine is renamed, changed or deleted later.
    [NotNull, MaxLength(Limits.MedicineName)] public string MedicineName { get; set; } = "";
    [MaxLength(Limits.Dosage)] public string? Dosage { get; set; }

    [Indexed] public DateTime ScheduledAt { get; set; }          // UTC
    public DateTime? TakenAt { get; set; }                       // UTC, null until taken
    public DoseStatus Status { get; set; } = DoseStatus.Pending;
}

// One row (Id = 1): where the last pull from the server ended.
public class SyncState
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public string? LastPullCursor { get; set; }
}

// =====================================================================
// 2. Sync DTOs: JSON shape sent to / received from the server.
//    Property names = server column names.
// =====================================================================

public class MedicineDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dosage")] public string? Dosage { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("start_date")] public string StartDate { get; set; } = "";
    [JsonPropertyName("end_date")] public string? EndDate { get; set; }
    [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class ReminderDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("medicine_id")] public Guid MedicineId { get; set; }
    [JsonPropertyName("time_of_day")] public string TimeOfDay { get; set; } = "";
    [JsonPropertyName("days_of_week")] public int DaysOfWeek { get; set; }
    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class DoseLogDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("medicine_id")] public Guid MedicineId { get; set; }
    [JsonPropertyName("reminder_id")] public Guid? ReminderId { get; set; }
    [JsonPropertyName("medicine_name")] public string MedicineName { get; set; } = "";
    [JsonPropertyName("dosage")] public string? Dosage { get; set; }
    [JsonPropertyName("scheduled_at")] public DateTime ScheduledAt { get; set; }
    [JsonPropertyName("taken_at")] public DateTime? TakenAt { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

// Push body, and the rows inside a pull response.
// Always push in this order: medicines, reminders, dose_logs.
public class SyncPayload
{
    [JsonPropertyName("medicines")] public List<MedicineDto> Medicines { get; set; } = new();
    [JsonPropertyName("reminders")] public List<ReminderDto> Reminders { get; set; } = new();
    [JsonPropertyName("dose_logs")] public List<DoseLogDto> DoseLogs { get; set; } = new();
}

public class SyncPullResponse : SyncPayload
{
    // Save into SyncState.LastPullCursor and send back as ?since=
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

// =====================================================================
// 3. Mapping between SQLite rows and DTOs
//    - Dates are normalised to UTC (sqlite-net returns ticks as Kind=Unspecified,
//      which would serialize WITHOUT 'Z' and be misread by the server).
//    - Rows coming FROM the server are saved with IsDirty = false.
//    - When applying a pulled row, if the local row is dirty and its
//      UpdatedAt is newer, keep the local row (newest change wins).
// =====================================================================

public static class SyncMapper
{
    const string DateFmt = "yyyy-MM-dd";
    const string TimeFmt = @"hh\:mm\:ss";

    static DateTime ToUtc(DateTime d) => d.Kind == DateTimeKind.Local
        ? d.ToUniversalTime()
        : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    static DateTime? ToUtc(DateTime? d) => d.HasValue ? ToUtc(d.Value) : null;

    static string FormatDate(DateTime d) => d.ToString(DateFmt, CultureInfo.InvariantCulture);
    static DateTime ParseDate(string s) => DateTime.ParseExact(s, DateFmt, CultureInfo.InvariantCulture);

    public static MedicineDto ToDto(this Medicine m) => new()
    {
        Id = m.Id, Name = m.Name, Dosage = m.Dosage, Notes = m.Notes,
        StartDate = FormatDate(m.StartDate),
        EndDate = m.EndDate.HasValue ? FormatDate(m.EndDate.Value) : null,
        IsActive = m.IsActive, IsDeleted = m.IsDeleted, UpdatedAt = ToUtc(m.UpdatedAt)
    };

    public static Medicine ToEntity(this MedicineDto d) => new()
    {
        Id = d.Id, Name = d.Name, Dosage = d.Dosage, Notes = d.Notes,
        StartDate = ParseDate(d.StartDate),
        EndDate = string.IsNullOrEmpty(d.EndDate) ? null : ParseDate(d.EndDate),
        IsActive = d.IsActive, IsDeleted = d.IsDeleted, UpdatedAt = ToUtc(d.UpdatedAt), IsDirty = false
    };

    public static ReminderDto ToDto(this Reminder r) => new()
    {
        Id = r.Id, MedicineId = r.MedicineId,
        TimeOfDay = r.TimeOfDay.ToString(TimeFmt, CultureInfo.InvariantCulture),
        DaysOfWeek = r.DaysOfWeek, IsEnabled = r.IsEnabled,
        IsDeleted = r.IsDeleted, UpdatedAt = ToUtc(r.UpdatedAt)
    };

    public static Reminder ToEntity(this ReminderDto d) => new()
    {
        Id = d.Id, MedicineId = d.MedicineId,
        TimeOfDay = TimeSpan.Parse(d.TimeOfDay, CultureInfo.InvariantCulture),
        DaysOfWeek = d.DaysOfWeek, IsEnabled = d.IsEnabled,
        IsDeleted = d.IsDeleted, UpdatedAt = ToUtc(d.UpdatedAt), IsDirty = false
    };

    public static DoseLogDto ToDto(this DoseLog l) => new()
    {
        Id = l.Id, MedicineId = l.MedicineId, ReminderId = l.ReminderId,
        MedicineName = l.MedicineName, Dosage = l.Dosage,
        ScheduledAt = ToUtc(l.ScheduledAt), TakenAt = ToUtc(l.TakenAt),
        Status = (int)l.Status, IsDeleted = l.IsDeleted, UpdatedAt = ToUtc(l.UpdatedAt)
    };

    public static DoseLog ToEntity(this DoseLogDto d) => new()
    {
        Id = d.Id, MedicineId = d.MedicineId, ReminderId = d.ReminderId,
        MedicineName = d.MedicineName, Dosage = d.Dosage,
        ScheduledAt = ToUtc(d.ScheduledAt), TakenAt = ToUtc(d.TakenAt),
        Status = (DoseStatus)d.Status, IsDeleted = d.IsDeleted,
        UpdatedAt = ToUtc(d.UpdatedAt), IsDirty = false
    };
}

// =====================================================================
// 4. Database setup
//    Default sqlite-net settings are used on purpose (DateTime/TimeSpan as
//    ticks, Guid as TEXT, enum as INTEGER). Do NOT pass storeDateTimeAsTicks:false
//    unless you also change the models.
//    Register as a singleton in MauiProgram.cs:
//        builder.Services.AddSingleton<AppDatabase>();
// =====================================================================

public class AppDatabase
{
    const SQLiteOpenFlags Flags =
        SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache;

    readonly SemaphoreSlim _lock = new(1, 1);
    SQLiteAsyncConnection? _db;

    public async Task<SQLiteAsyncConnection> GetAsync()
    {
        if (_db != null) return _db;

        await _lock.WaitAsync();
        try
        {
            if (_db != null) return _db;

            var path = Path.Combine(FileSystem.AppDataDirectory, "medreminder.db3");
            var db = new SQLiteAsyncConnection(path, Flags);

            await db.CreateTableAsync<Medicine>();
            await db.CreateTableAsync<Reminder>();
            await db.CreateTableAsync<DoseLog>();
            await db.CreateTableAsync<SyncState>();
            await db.InsertOrReplaceAsync(
                (await db.FindAsync<SyncState>(1)) ?? new SyncState { Id = 1 });

            _db = db;
            return _db;
        }
        finally { _lock.Release(); }
    }

    // Call on logout, AFTER warning the user if any row has IsDirty = true.
    public async Task ClearAllAsync()
    {
        var db = await GetAsync();
        await db.DeleteAllAsync<DoseLog>();
        await db.DeleteAllAsync<Reminder>();
        await db.DeleteAllAsync<Medicine>();
        await db.InsertOrReplaceAsync(new SyncState { Id = 1, LastPullCursor = null });
    }
}
