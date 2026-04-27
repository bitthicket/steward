-- 0023-oauth-state.sql
-- Short-lived PKCE state rows for Akoya FDX OAuth redirect handoff.
-- See STE-35.

CREATE TABLE oauth_state (
    state         text        PRIMARY KEY,
    code_verifier text        NOT NULL,
    tenant_id     uuid        NOT NULL,
    user_id       uuid        NOT NULL,
    redirect_uri  text        NOT NULL,
    institution_id text       NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL
);

CREATE INDEX oauth_state_expires_at_idx
    ON oauth_state (expires_at);

-- No RLS needed because rows are keyed by opaque state param,
-- but we tenant-scope for defense in depth.
ALTER TABLE oauth_state ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON oauth_state
    FOR ALL
    TO tenant_app
    USING (tenant_id = current_setting('steward.tenant_id')::uuid)
    WITH CHECK (tenant_id = current_setting('steward.tenant_id')::uuid);

GRANT SELECT, INSERT, DELETE ON TABLE oauth_state TO tenant_app;
