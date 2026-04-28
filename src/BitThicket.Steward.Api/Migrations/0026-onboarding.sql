-- Migration 0025: Tenant onboarding state

CREATE TABLE tenant_onboarding (
    tenant_id UUID PRIMARY KEY REFERENCES tenants(id) ON DELETE CASCADE,
    current_step INT NOT NULL DEFAULT 2,
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    completed_steps JSONB NOT NULL DEFAULT '[1, 2]',
    skipped BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_tenant_onboarding_current_step ON tenant_onboarding(current_step);

ALTER TABLE tenant_onboarding ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_onboarding_isolation ON tenant_onboarding
    USING (tenant_id = current_setting('steward.tenant_id', true)::UUID);

CREATE TRIGGER update_tenant_onboarding_updated_at
    BEFORE UPDATE ON tenant_onboarding
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();
