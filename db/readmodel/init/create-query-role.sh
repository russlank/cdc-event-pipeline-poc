#!/bin/sh
# Runs once, when the PostgreSQL container initialises an empty data directory.
# The projector's login (POSTGRES_USER) owns the read model and creates its tables; this adds
# the Query API's read-only login, which gets SELECT on every table that owner creates.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v query_password="$PAYQUERY_DB_PASSWORD" -v owner="$POSTGRES_USER" -v database="$POSTGRES_DB" <<'SQL'
CREATE ROLE pay_query LOGIN PASSWORD :'query_password';
GRANT CONNECT ON DATABASE :"database" TO pay_query;
GRANT USAGE ON SCHEMA public TO pay_query;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public GRANT SELECT ON TABLES TO pay_query;
SQL
