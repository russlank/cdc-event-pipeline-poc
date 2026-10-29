-- Read model of the Pay History Projector (PostgreSQL).
-- Applied by the projector at startup, in file-name order; every statement is idempotent.

CREATE EXTENSION IF NOT EXISTS btree_gist;

-- One row per business event the projector has applied: the idempotency guard.
CREATE TABLE IF NOT EXISTS processed_event (
    event_id     uuid        PRIMARY KEY,
    event_type   text        NOT NULL,
    tenant_id    integer     NOT NULL,
    employee_id  integer     NOT NULL,
    recorded_at  timestamptz NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS processed_event_employee ON processed_event (tenant_id, employee_id);

-- Bitemporal pay history. Valid time [valid_from, valid_to) is when the amount applies;
-- recorded time [recorded_from, recorded_to) is when the pipeline believed it, taken from the
-- event's recordedAt only. Null ends are open. Rows are never updated except to set recorded_to.
CREATE TABLE IF NOT EXISTS pay_history (
    pay_history_id bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id      integer       NOT NULL,
    employee_id    integer       NOT NULL,
    pay_component  text          NOT NULL,
    salary_id      integer       NOT NULL,
    monthly_amount numeric(12,2) NOT NULL,
    currency       text          NOT NULL,
    valid_from     date          NOT NULL,
    valid_to       date,
    recorded_from  timestamptz   NOT NULL,
    recorded_to    timestamptz,
    event_id       uuid          NOT NULL,
    CONSTRAINT pay_history_valid_period CHECK (valid_to IS NULL OR valid_to > valid_from),
    CONSTRAINT pay_history_recorded_period CHECK (recorded_to IS NULL OR recorded_to > recorded_from),
    -- As currently known, a pay component has at most one amount per day.
    CONSTRAINT pay_history_current_no_overlap EXCLUDE USING gist (
        tenant_id WITH =,
        employee_id WITH =,
        pay_component WITH =,
        daterange(valid_from, valid_to) WITH &&
    ) WHERE (recorded_to IS NULL)
);

CREATE INDEX IF NOT EXISTS pay_history_employee ON pay_history (tenant_id, employee_id, valid_from, recorded_from);
