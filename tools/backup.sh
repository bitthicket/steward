#!/usr/bin/env bash
set -euo pipefail

# Steward PostgreSQL backup script
# Usage: ./tools/backup.sh [output-dir]
# Requires: pg_dump, environment variable STEWARD_DATABASE_URL
#
# Northflank managed PostgreSQL handles automated daily backups.
# This script is for ad-hoc exports, local testing, or migration scenarios.

OUTPUT_DIR="${1:-./backups}"
TIMESTAMP=$(date -u +%Y%m%d_%H%M%S)
BACKUP_FILE="${OUTPUT_DIR}/steward_backup_${TIMESTAMP}.sql"

if [ -z "${STEWARD_DATABASE_URL:-}" ]; then
    echo "Error: STEWARD_DATABASE_URL is not set."
    echo "Set it to the full PostgreSQL connection string (e.g. postgres://user:pass@host/db)."
    exit 1
fi

mkdir -p "$OUTPUT_DIR"

echo "Starting backup to ${BACKUP_FILE} ..."

pg_dump \
    --dbname="$STEWARD_DATABASE_URL" \
    --format=plain \
    --no-owner \
    --no-privileges \
    --file="$BACKUP_FILE"

echo "Backup complete: ${BACKUP_FILE}"
echo "Size: $(du -h "$BACKUP_FILE" | cut -f1)"
