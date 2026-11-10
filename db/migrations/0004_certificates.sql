-- 0004: certificate metadata (FF-004, FF-006). Bytes and password live only in
-- the vault; vault_ref is an opaque reference. Certificates are bound to one
-- environment, so homologation and production never share a row.

create table digital_certificate (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  vault_ref text not null check (length(vault_ref) between 1 and 512),
  fingerprint_sha256 char(64) not null check (fingerprint_sha256 ~ '^[a-f0-9]{64}$'),
  subject_tax_id text not null check (subject_tax_id ~ '^[0-9A-Z]{14}$'),
  not_before timestamptz not null,
  not_after timestamptz not null,
  status text not null check (status in ('ACTIVE', 'SUPERSEDED', 'REVOKED')),
  created_at timestamptz not null default now(),
  superseded_at timestamptz,
  revoked_at timestamptz,
  unique (id, tenant_id),
  unique (environment_id, fingerprint_sha256),
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  check (not_before < not_after),
  check ((status = 'SUPERSEDED') = (superseded_at is not null)),
  check ((status = 'REVOKED') = (revoked_at is not null))
);

create unique index digital_certificate_one_active
  on digital_certificate (environment_id) where status = 'ACTIVE';

create function digital_certificate_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.establishment_id, new.environment_id, new.vault_ref, new.fingerprint_sha256,
      new.subject_tax_id, new.not_before, new.not_after, new.created_at)
     is distinct from
     (old.tenant_id, old.establishment_id, old.environment_id, old.vault_ref, old.fingerprint_sha256,
      old.subject_tax_id, old.not_before, old.not_after, old.created_at) then
    raise exception 'digital_certificate metadata is immutable' using errcode = 'RF001';
  end if;
  if old.status <> 'ACTIVE' and new.status <> old.status then
    raise exception 'only an ACTIVE certificate can change status' using errcode = 'RF003';
  end if;
  return new;
end
$$;

create trigger digital_certificate_guard before update on digital_certificate
  for each row execute function digital_certificate_guard();
