#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# restore-test.sh — smoke-test a Northflank Postgres snapshot restore.
#
# Usage:
#   STEWARD_NF_API_TOKEN=xxx \
#   STEWARD_NF_PROJECT=project-id \
#   STEWARD_NF_POSTGRES_ADDON=addon-id \
#   ./tools/restore-test.sh
#
# Creates a scratch Postgres addon from the latest snapshot, runs a row-count
# smoke test against tenants/users/transactions, then tears the addon down.
#
# Designed to run monthly via Paperclip routines or on-demand in CI.
# ---------------------------------------------------------------------------

set -euo pipefail

TOKEN="${STEWARD_NF_API_TOKEN:-}"
PROJECT="${STEWARD_NF_PROJECT:-}"
ADDON="${STEWARD_NF_POSTGRES_ADDON:-}"

if [[ -z "$TOKEN" || -z "$PROJECT" || -z "$ADDON" ]]; then
    echo "Error: STEWARD_NF_API_TOKEN, STEWARD_NF_PROJECT and STEWARD_NF_POSTGRES_ADDON must be set."
    exit 1
fi

NF_API="https://api.northflank.com/v1"
AUTH_HEADER="Authorization: Bearer $TOKEN"

echo "=== Steward restore test ==="
echo "Project: $PROJECT"
echo "Source addon: $ADDON"

# 1. List snapshots and pick the latest
SNAPSHOTS=$(curl -s -H "$AUTH_HEADER" \
    "$NF_API/projects/$PROJECT/addons/$ADDON/snapshots")

LATEST=$(echo "$SNAPSHOTS" | jq -r '.data | sort_by(.createdAt) | last | .id')

if [[ "$LATEST" == "null" || -z "$LATEST" ]]; then
    echo "Error: no snapshots found for addon $ADDON"
    exit 1
fi

echo "Latest snapshot: $LATEST"

# 2. Create scratch addon from snapshot
SCRATCH_NAME="restore-test-$(date +%s)"
echo "Creating scratch addon: $SCRATCH_NAME"

CREATE_RESPONSE=$(curl -s -X POST -H "$AUTH_HEADER" -H "Content-Type: application/json" \
    -d "{\"name\":\"$SCRATCH_NAME\",\"type\":\"postgres\",\"version\":\"16\",\"snapshotId\":\"$LATEST\"}" \
    "$NF_API/projects/$PROJECT/addons")

SCRATCH_ID=$(echo "$CREATE_RESPONSE" | jq -r '.data.id')

if [[ "$SCRATCH_ID" == "null" || -z "$SCRATCH_ID" ]]; then
    echo "Error: failed to create scratch addon"
    echo "$CREATE_RESPONSE"
    exit 1
fi

# 3. Wait for addon to be ready (up to 5 minutes)
echo "Waiting for scratch addon to be ready..."
for i in {1..30}; do
    STATUS=$(curl -s -H "$AUTH_HEADER" \
        "$NF_API/projects/$PROJECT/addons/$SCRATCH_ID" | jq -r '.data.status.state')
    if [[ "$STATUS" == "ready" ]]; then
        echo "Scratch addon is ready."
        break
    fi
    if [[ "$STATUS" == "failed" ]]; then
        echo "Error: scratch addon failed to provision"
        exit 1
    fi
    echo "  status=$STATUS (wait $i/30)"
    sleep 10
done

# 4. Fetch connection string
CONN=$(curl -s -H "$AUTH_HEADER" \
    "$NF_API/projects/$PROJECT/addons/$SCRATCH_ID/credentials" | jq -r '.data.connectionString')

if [[ "$CONN" == "null" || -z "$CONN" ]]; then
    echo "Error: could not get connection string for scratch addon"
    exit 1
fi

# 5. Run smoke test
echo "Running smoke test..."

tenant_count=$(psql "$CONN" -t -c "SELECT count(*) FROM tenants;" | xargs)
user_count=$(psql "$CONN" -t -c "SELECT count(*) FROM users;" | xargs)
txn_count=$(psql "$CONN" -t -c "SELECT count(*) FROM transactions;" | xargs)

echo "  tenants:     $tenant_count"
echo "  users:       $user_count"
echo "  transactions: $txn_count"

# 6. Validate
FAIL=0
if ! [[ "$tenant_count" =~ ^[0-9]+$ ]]; then
    echo "ERROR: tenants query failed"
    FAIL=1
fi
if ! [[ "$user_count" =~ ^[0-9]+$ ]]; then
    echo "ERROR: users query failed"
    FAIL=1
fi
if ! [[ "$txn_count" =~ ^[0-9]+$ ]]; then
    echo "ERROR: transactions query failed"
    FAIL=1
fi

# 7. Tear down scratch addon
echo "Tearing down scratch addon $SCRATCH_ID..."
curl -s -X DELETE -H "$AUTH_HEADER" \
    "$NF_API/projects/$PROJECT/addons/$SCRATCH_ID" > /dev/null
echo "Scratch addon deleted."

if [[ "$FAIL" -eq 1 ]]; then
    echo "=== RESTORE TEST FAILED ==="
    exit 1
fi

echo "=== RESTORE TEST PASSED ==="
