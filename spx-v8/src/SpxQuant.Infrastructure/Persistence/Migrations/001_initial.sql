-- SPX Quant v8 market-data store. Insert-only by convention: rows are never updated in place.
CREATE TABLE IF NOT EXISTS bars (
    symbol      text        NOT NULL,
    timeframe   text        NOT NULL CHECK (timeframe IN ('1m','5m')),
    bar_start   timestamptz NOT NULL,
    open        numeric(14,4) NOT NULL CHECK (open > 0),
    high        numeric(14,4) NOT NULL CHECK (high > 0),
    low         numeric(14,4) NOT NULL CHECK (low > 0),
    close       numeric(14,4) NOT NULL CHECK (close > 0),
    volume      bigint      NULL CHECK (volume IS NULL OR volume >= 0),
    source      text        NOT NULL,
    provenance  text        NOT NULL,
    ingested_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (symbol, timeframe, bar_start),
    CHECK (high >= low AND high >= open AND high >= close AND low <= open AND low <= close)
);

CREATE TABLE IF NOT EXISTS chain_snapshots (
    id             bigserial   NOT NULL,
    underlying     text        NOT NULL,
    expiration     date        NOT NULL,
    captured_at    timestamptz NOT NULL,
    source         text        NOT NULL,
    provenance     text        NOT NULL,
    option_count   integer     NOT NULL,
    index_last     numeric(14,4) NULL,
    content_sha256 text        NOT NULL,
    payload        jsonb       NOT NULL,
    PRIMARY KEY (id, captured_at)
);
CREATE INDEX IF NOT EXISTS chain_snapshots_lookup ON chain_snapshots (underlying, expiration, captured_at DESC);

CREATE TABLE IF NOT EXISTS audit_log (
    sequence      bigint      PRIMARY KEY,
    at            timestamptz NOT NULL,
    kind          text        NOT NULL,
    subject       text        NULL,
    payload_json  text        NOT NULL,
    previous_hash text        NOT NULL,
    hash          text        NOT NULL UNIQUE
);
-- Append-only enforcement at the database level (defense in depth; a superuser can still bypass it).
CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'audit_log is append-only'; END $$;
DROP TRIGGER IF EXISTS audit_log_no_update ON audit_log;
CREATE TRIGGER audit_log_no_update BEFORE UPDATE OR DELETE OR TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable();

-- TimescaleDB is optional: hypertables when the extension is available, plain tables otherwise.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'timescaledb') THEN
        CREATE EXTENSION IF NOT EXISTS timescaledb;
        PERFORM create_hypertable('bars', 'bar_start', if_not_exists => TRUE, migrate_data => TRUE);
        PERFORM create_hypertable('chain_snapshots', 'captured_at', if_not_exists => TRUE, migrate_data => TRUE);
    END IF;
END $$;
