-- 0001: extensions, runtime roles and tenant-context helpers (ADR-014).
-- Roles are created NOLOGIN; logins for each environment are provisioned
-- outside migrations, so no password ever lives in this repository.

create extension if not exists btree_gist;

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'rrf5_app') then
    create role rrf5_app nologin nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'rrf5_worker') then
    create role rrf5_worker nologin nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
end
$$;

revoke create on schema public from public;
grant usage on schema public to rrf5_app, rrf5_worker;

-- Tenant context is transaction-local: set_config('app.tenant_id', id, true).
-- An absent or reset setting yields NULL, which no tenant_id equals (fail closed).
-- A malformed value raises invalid_text_representation instead of matching.
create function app_current_tenant() returns uuid
  language sql stable
  as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

-- Platform mode lets the platform administrator list and create tenants.
-- It never widens access to tenant-owned rows other than tenant and audit_log.
create function app_is_platform() returns boolean
  language sql stable
  as $$ select coalesce(current_setting('app.platform', true), '') = 'on' $$;

-- Civil date in Sao Paulo, used for validity checks (timezone policy of the 001 spec).
create function app_today_sp() returns date
  language sql stable
  as $$ select (now() at time zone 'America/Sao_Paulo')::date $$;

-- Custom SQLSTATE class RF (stable codes, mapped to domain errors in T012):
--   RF001 immutable field changed      RF002 monotonic counter decreased
--   RF003 invalid state transition     RF004 four-eyes or scope violation
--   RF005 append-only record changed   RF006 cross-row consistency violated
