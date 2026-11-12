-- 0012: commercial payment method -> official tPag, per environment (005 T011,
-- operator decision 2026-11-12). The mapping is configuration chosen by the
-- tenant, never inferred. Replacing it supersedes the active rows, so every
-- version stays auditable. Codes are checked against the national table
-- (regulatory/payment-methods.json) by the application.

create table environment_payment_method (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  commercial_method text not null check (commercial_method in
    ('CASH', 'PIX', 'CREDIT_CARD', 'DEBIT_CARD', 'VOUCHER', 'STORE_CREDIT', 'OTHER')),
  t_pag char(2) not null check (t_pag ~ '^[0-9]{2}$'),
  status text not null check (status in ('ACTIVE', 'SUPERSEDED')),
  created_by_application_id uuid not null,
  created_at timestamptz not null default now(),
  superseded_at timestamptz,
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  foreign key (created_by_application_id, tenant_id) references application(id, tenant_id),
  check ((status = 'SUPERSEDED') = (superseded_at is not null))
);

create unique index environment_payment_method_one_active
  on environment_payment_method (environment_id, commercial_method) where status = 'ACTIVE';

-- Rows only move ACTIVE -> SUPERSEDED; nothing else changes.
create function environment_payment_method_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.establishment_id, new.environment_id, new.commercial_method, new.t_pag,
      new.created_by_application_id, new.created_at)
     is distinct from
     (old.tenant_id, old.establishment_id, old.environment_id, old.commercial_method, old.t_pag,
      old.created_by_application_id, old.created_at) then
    raise exception 'environment_payment_method is immutable; supersede it instead' using errcode = 'RF001';
  end if;
  if not (old.status = 'ACTIVE' and new.status = 'SUPERSEDED') then
    raise exception 'environment_payment_method only moves from ACTIVE to SUPERSEDED' using errcode = 'RF003';
  end if;
  return new;
end
$$;

create trigger environment_payment_method_guard before update on environment_payment_method
  for each row execute function environment_payment_method_guard();

alter table environment_payment_method enable row level security;
alter table environment_payment_method force row level security;
create policy tenant_isolation on environment_payment_method
  using (tenant_id = app_current_tenant()) with check (tenant_id = app_current_tenant());

grant select, insert, update on environment_payment_method to rrf5_app;
grant select on environment_payment_method to rrf5_worker;
