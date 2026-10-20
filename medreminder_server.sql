-- =====================================================================
-- MedReminder: PostgreSQL schema (server; backs the history website)
-- Pairs with AppDatabase.cs. Column names below are exactly the
-- [JsonPropertyName] values of the C# DTOs.
--
-- Server-only columns (not in the app):
--   user_id            set by the API from the login token, never from the request body
--   server_updated_at  set by the database (trigger below); used as the sync cursor
-- App-only column (not here): IsDirty
--
-- Accounts: assumes Django's auth_user table (BIGINT id). If you build the
-- backend in Django, define these as models and let `makemigrations` create
-- them; this file is then the blueprint.
-- =====================================================================

-- Foreign keys are DEFERRABLE INITIALLY DEFERRED: a push is applied in one
-- transaction and the checks run at COMMIT, so row order inside a push never
-- causes an error. (The app still pushes medicines, reminders, dose_logs.)

CREATE TABLE medicines (
    id                UUID PRIMARY KEY,
    user_id           BIGINT       NOT NULL REFERENCES auth_user(id) ON DELETE CASCADE,
    name              VARCHAR(150) NOT NULL,                  -- Limits.MedicineName
    dosage            VARCHAR(100),                           -- Limits.Dosage
    notes             TEXT,
    start_date        DATE         NOT NULL,                  -- "yyyy-MM-dd"
    end_date          DATE,
    is_active         BOOLEAN      NOT NULL DEFAULT TRUE,
    is_deleted        BOOLEAN      NOT NULL DEFAULT FALSE,
    updated_at        TIMESTAMPTZ  NOT NULL,                  -- from the phone (UTC)
    server_updated_at TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE INDEX ix_medicines_user_sync ON medicines (user_id, server_updated_at);

CREATE TABLE reminders (
    id                UUID PRIMARY KEY,
    user_id           BIGINT      NOT NULL REFERENCES auth_user(id) ON DELETE CASCADE,
    medicine_id       UUID        NOT NULL REFERENCES medicines(id)
                                  ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    time_of_day       TIME        NOT NULL,                   -- "HH:mm:ss"
    days_of_week      SMALLINT    NOT NULL DEFAULT 127
                      CHECK (days_of_week BETWEEN 1 AND 127), -- bitmask: Mon=1 ... Sun=64
    is_enabled        BOOLEAN     NOT NULL DEFAULT TRUE,
    is_deleted        BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_at        TIMESTAMPTZ NOT NULL,
    server_updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_reminders_user_sync ON reminders (user_id, server_updated_at);
CREATE INDEX ix_reminders_medicine  ON reminders (medicine_id);

CREATE TABLE dose_logs (
    id                UUID PRIMARY KEY,
    user_id           BIGINT       NOT NULL REFERENCES auth_user(id) ON DELETE CASCADE,
    medicine_id       UUID         NOT NULL REFERENCES medicines(id)
                                   ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
    reminder_id       UUID         REFERENCES reminders(id)
                                   ON DELETE SET NULL DEFERRABLE INITIALLY DEFERRED,
    medicine_name     VARCHAR(150) NOT NULL,                  -- snapshot at scheduling time
    dosage            VARCHAR(100),                           -- snapshot at scheduling time
    scheduled_at      TIMESTAMPTZ  NOT NULL,
    taken_at          TIMESTAMPTZ,
    status            SMALLINT     NOT NULL DEFAULT 0
                      CHECK (status IN (0, 1, 2, 3)),         -- 0 Pending, 1 Taken, 2 Skipped, 3 Missed
    is_deleted        BOOLEAN      NOT NULL DEFAULT FALSE,
    updated_at        TIMESTAMPTZ  NOT NULL,
    server_updated_at TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE INDEX ix_dose_logs_user_sync ON dose_logs (user_id, server_updated_at);
CREATE INDEX ix_dose_logs_user_time ON dose_logs (user_id, scheduled_at DESC);
-- prefix search on medicine name (LIKE 'abc%') for the website
CREATE INDEX ix_dose_logs_user_name ON dose_logs (user_id, lower(medicine_name) text_pattern_ops);

-- The database stamps server_updated_at on every insert/update, so the API cannot forget it.
CREATE OR REPLACE FUNCTION set_server_updated_at() RETURNS trigger AS $$
BEGIN
    NEW.server_updated_at := now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_medicines_sync BEFORE INSERT OR UPDATE ON medicines
    FOR EACH ROW EXECUTE FUNCTION set_server_updated_at();
CREATE TRIGGER trg_reminders_sync BEFORE INSERT OR UPDATE ON reminders
    FOR EACH ROW EXECUTE FUNCTION set_server_updated_at();
CREATE TRIGGER trg_dose_logs_sync BEFORE INSERT OR UPDATE ON dose_logs
    FOR EACH ROW EXECUTE FUNCTION set_server_updated_at();

-- =====================================================================
-- Sync notes for the API (reference, not executed)
-- =====================================================================
-- PUSH (one transaction; same pattern for reminders and dose_logs):
--   INSERT INTO medicines (id, user_id, name, dosage, notes, start_date, end_date,
--                          is_active, is_deleted, updated_at)
--   VALUES (:id, :user_id, :name, :dosage, :notes, :start_date, :end_date,
--           :is_active, :is_deleted, :updated_at)
--   ON CONFLICT (id) DO UPDATE SET
--          name = EXCLUDED.name, dosage = EXCLUDED.dosage, notes = EXCLUDED.notes,
--          start_date = EXCLUDED.start_date, end_date = EXCLUDED.end_date,
--          is_active = EXCLUDED.is_active, is_deleted = EXCLUDED.is_deleted,
--          updated_at = EXCLUDED.updated_at
--   WHERE medicines.user_id = EXCLUDED.user_id          -- never touch another user's row
--     AND EXCLUDED.updated_at > medicines.updated_at;   -- newest change wins
--
-- PULL: rows WHERE user_id = :user_id AND server_updated_at > :since
--       ORDER BY server_updated_at. Return next_cursor = max(server_updated_at) as ISO-8601.
--       Tip: pull with `since` minus a few seconds; re-applying a row is harmless (upsert),
--       and it avoids missing rows from transactions that committed slightly out of order.

-- =====================================================================
-- Website queries
-- =====================================================================
-- "Have I ever taken X, and at what dose?"
-- SELECT medicine_name, dosage,
--        MIN(scheduled_at) AS first_taken,
--        MAX(scheduled_at) AS last_taken,
--        COUNT(*)          AS times_taken
-- FROM dose_logs
-- WHERE user_id = :user_id AND is_deleted = FALSE AND status = 1
--   AND lower(medicine_name) LIKE lower(:term) || '%'
-- GROUP BY medicine_name, dosage
-- ORDER BY last_taken DESC;

-- "What was I taking in a given period?"
-- SELECT DISTINCT medicine_name, dosage
-- FROM dose_logs
-- WHERE user_id = :user_id AND is_deleted = FALSE AND status = 1
--   AND scheduled_at >= :from AND scheduled_at < :to;
