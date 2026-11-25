\set ON_ERROR_STOP on
-- Execute once on the isolated restore while application processes are stopped.
BEGIN;
UPDATE "AspNetUsers"
SET "SecurityStamp" = gen_random_uuid()::text, "ConcurrencyStamp" = gen_random_uuid()::text;
DELETE FROM "ProtectionKeys";
UPDATE "Invitations"
SET "State" = 'revoked', "ResolvedAt" = clock_timestamp()
WHERE "State" = 'pending';
UPDATE "ImportRuns"
SET "State" = 'cancelled', "EndedAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(),
    "LeaseId" = NULL, "LeaseUntil" = NULL, "RetryAt" = NULL
WHERE "EndedAt" IS NULL;
UPDATE "Repositories" SET "RequiresFullScan" = true;
UPDATE "IssueSnapshots"
SET "SourceAvailable" = false, "Title" = '', "Body" = NULL, "Labels" = ARRAY[]::text[],
    "NeedsRecheck" = true;
COMMIT;
