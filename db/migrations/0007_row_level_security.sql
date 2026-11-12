-- 0007: row-level security and runtime grants (T008, FF-002, ADR-014).
-- RLS is defense in depth: composite keys already bind every row to its tenant.
-- FORCE applies the policies to the table owner too; only a superuser or a
-- BYPASSRLS role skips them, and neither runtime role has those attributes.

do $$
declare
  t text;
begin
  foreach t in array array[
    'application', 'company', 'establishment', 'application_establishment_grant',
    'fiscal_environment', 'fiscal_configuration', 'fiscal_series', 'fiscal_document',
    'number_reservation', 'digital_certificate', 'tax_profile', 'tax_rule',
    'tax_rule_version', 'idempotency_record']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy tenant_isolation on %I using (tenant_id = app_current_tenant()) '
      'with check (tenant_id = app_current_tenant())', t);
  end loop;
end
$$;

-- A tenant sees its own row; listing and creating tenants needs platform mode.
alter table tenant enable row level security;
alter table tenant force row level security;
create policy tenant_self_or_platform on tenant
  using (id = app_current_tenant() or app_is_platform())
  with check (app_is_platform());

-- Audit rows follow the tenant; tenant-less rows are platform events.
alter table audit_log enable row level security;
alter table audit_log force row level security;
create policy audit_tenant_or_platform on audit_log
  using (tenant_id = app_current_tenant() or app_is_platform())
  with check (tenant_id = app_current_tenant() or (tenant_id is null and app_is_platform()));

-- API role: no DDL, no DELETE except where a row is meant to disappear.
grant select, insert, update on
  tenant, application, company, establishment, fiscal_environment, fiscal_configuration,
  fiscal_series, fiscal_document, number_reservation, digital_certificate, tax_profile,
  tax_rule, tax_rule_version, idempotency_record
  to rrf5_app;
grant delete on application_establishment_grant, tax_rule_version to rrf5_app;
grant select, insert on application_establishment_grant, audit_log to rrf5_app;
grant select on schema_version to rrf5_app;

-- Worker role: internal jobs (certificate alerts, catalog load).
grant select on
  tenant, application, company, establishment, fiscal_environment, fiscal_configuration,
  fiscal_series, fiscal_document, number_reservation, digital_certificate, tax_profile,
  tax_rule, tax_rule_version, idempotency_record
  to rrf5_worker;
grant select, insert on audit_log to rrf5_worker;
grant select, insert, update on schema_version to rrf5_worker;

grant usage on all sequences in schema public to rrf5_app, rrf5_worker;
grant execute on function app_current_tenant(), app_is_platform(), app_today_sp() to rrf5_app, rrf5_worker;
