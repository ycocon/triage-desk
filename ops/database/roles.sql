\set ON_ERROR_STOP on
-- Run as the database owner. Create the three login roles through the provider first.
BEGIN;
ALTER SCHEMA public OWNER TO :"migration_role";
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO :"runtime_role", :"backup_role";
GRANT SELECT ON ALL TABLES IN SCHEMA public TO :"backup_role";
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO :"backup_role";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"runtime_role";
REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory", "TeamAudit" FROM :"runtime_role";
GRANT INSERT ON "TeamAudit" TO :"runtime_role";
SELECT to_regclass('public."ProtectionKeys"') IS NOT NULL AS protection_keys_exist \gset
\if :protection_keys_exist
REVOKE UPDATE, DELETE ON "ProtectionKeys" FROM :"runtime_role";
\endif
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA public
    GRANT SELECT ON TABLES TO :"backup_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA public
    GRANT SELECT ON SEQUENCES TO :"backup_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO :"runtime_role";
COMMIT;
