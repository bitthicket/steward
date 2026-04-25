-- 0001-baseline.sql
-- Baseline schema for Steward: tenants, users, and user_tenant_memberships.
-- See STE-15. RLS is intentionally deferred to a later ticket once the
-- persistence layer can pass tenant context.

CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE tenants (
    id           uuid        PRIMARY KEY,
    display_name text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
    id            uuid        PRIMARY KEY,
    email         citext      NOT NULL UNIQUE,
    password_hash text        NOT NULL,
    display_name  text,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE user_tenant_memberships (
    user_id    uuid        NOT NULL REFERENCES users(id)   ON DELETE CASCADE,
    tenant_id  uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    role       text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, tenant_id)
);

CREATE INDEX user_tenant_memberships_tenant_id_idx
    ON user_tenant_memberships (tenant_id);
