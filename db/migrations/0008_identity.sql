-- 0008: platform tenant, scope guards and the credential lookup of /oauth/token (F1, ADR-013).

-- Exactly one PLATFORM tenant holds the platform administrator applications.
alter table tenant add column kind text not null default 'CUSTOMER'
  check (kind in ('PLATFORM', 'CUSTOMER'));
create unique index tenant_one_platform on tenant (kind) where kind = 'PLATFORM';

-- platform.admin exists only in the PLATFORM tenant (checked against the row the
-- caller can see; RLS gives the application's own tenant).
create function application_scope_guard() returns trigger
  language plpgsql
  as $$
declare
  tenant_kind text;
begin
  if 'platform.admin' = any (new.scopes) then
    select kind into tenant_kind from tenant where id = new.tenant_id;
    if tenant_kind is distinct from 'PLATFORM' then
      raise exception 'platform.admin is reserved to the platform tenant' using errcode = 'RF004';
    end if;
  end if;
  if tg_op = 'UPDATE' and (new.tenant_id, new.client_id, new.created_at)
     is distinct from (old.tenant_id, old.client_id, old.created_at) then
    raise exception 'application identity is immutable' using errcode = 'RF001';
  end if;
  return new;
end
$$;

create trigger application_scope_guard before insert or update on application
  for each row execute function application_scope_guard();

-- The token endpoint must find an application by client_id before it knows the
-- tenant. A NOLOGIN role with a read-only, role-specific policy owns a SECURITY
-- DEFINER function that returns exactly one credential by exact client_id; the
-- API role can call the function but still cannot read the tables across tenants.
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'rrf5_auth') then
    create role rrf5_auth nologin nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
end
$$;

grant usage on schema public to rrf5_auth;
grant select (id, tenant_id, client_id, secret_hash, secret_salt, secret_iterations, scopes, status)
  on application to rrf5_auth;
grant select (id, status) on tenant to rrf5_auth;
create policy auth_lookup on application for select to rrf5_auth using (true);
create policy auth_lookup on tenant for select to rrf5_auth using (true);

create function auth_find_client(p_client_id text)
  returns table (
    application_id uuid,
    tenant_id uuid,
    secret_hash bytea,
    secret_salt bytea,
    secret_iterations integer,
    scopes text[],
    application_status text,
    tenant_status text)
  language sql
  stable
  security definer
  set search_path = public, pg_temp
  as $$
    select a.id, a.tenant_id, a.secret_hash, a.secret_salt, a.secret_iterations, a.scopes, a.status, t.status
    from application a
    join tenant t on t.id = a.tenant_id
    where a.client_id = p_client_id
  $$;

alter function auth_find_client(text) owner to rrf5_auth;
revoke all on function auth_find_client(text) from public;
grant execute on function auth_find_client(text) to rrf5_app;
