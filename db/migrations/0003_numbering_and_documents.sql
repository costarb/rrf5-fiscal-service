-- 0003: series, number reservations and the fiscal document table (data-model.md, FF-005).
-- The foundation does not emit documents; fiscal_document exists only so the
-- composite keys, checks and immutability rules are in place and tested.

-- nNF accepts 1..999999999 and serie 0..999 in layout 4.00 (MOC Anexo I).
-- next_number may reach number_max + 1, meaning the range is exhausted; the
-- data-model pseudocode capped it at number_max, which made the last number
-- impossible to allocate.
create table fiscal_series (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  model fiscal_model not null,
  series integer not null check (series between 0 and 999),
  number_min bigint not null check (number_min >= 1),
  number_max bigint not null check (number_max <= 999999999),
  next_number bigint not null,
  version bigint not null default 0 check (version >= 0),
  allocation_owner series_owner_type not null,
  owner_id uuid,
  owner_epoch bigint not null default 1 check (owner_epoch > 0),
  status text not null check (status in ('ACTIVE', 'SUSPENDED')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id, establishment_id, environment_id, model),
  unique (tenant_id, establishment_id, environment_id, model, series),
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  check (number_min <= number_max),
  check (next_number between number_min and number_max + 1),
  check (
    (allocation_owner = 'CLOUD' and owner_id is null) or
    (allocation_owner = 'LOCAL_AGENT' and owner_id is not null)
  )
);

create function fiscal_series_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.establishment_id, new.environment_id, new.model, new.series, new.number_min)
     is distinct from
     (old.tenant_id, old.establishment_id, old.environment_id, old.model, old.series, old.number_min) then
    raise exception 'fiscal_series identity is immutable' using errcode = 'RF001';
  end if;
  if new.next_number < old.next_number then
    raise exception 'fiscal_series.next_number cannot decrease' using errcode = 'RF002';
  end if;
  if new.owner_epoch < old.owner_epoch or new.version < old.version then
    raise exception 'fiscal_series.owner_epoch and version cannot decrease' using errcode = 'RF002';
  end if;
  if (new.allocation_owner, new.owner_id) is distinct from (old.allocation_owner, old.owner_id)
     and new.owner_epoch = old.owner_epoch then
    raise exception 'ownership transfer must increase owner_epoch' using errcode = 'RF006';
  end if;
  return new;
end
$$;

create trigger fiscal_series_guard before update on fiscal_series
  for each row execute function fiscal_series_guard();

create table fiscal_document (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  series_id uuid,
  model fiscal_model not null,
  external_id text not null check (length(external_id) between 1 and 100),
  access_key text check (access_key ~ '^[0-9A-Z]{44}$'),
  number bigint,
  state text not null check (state in (
    'DRAFT', 'VALIDATING', 'READY_TO_SIGN', 'SIGNING', 'SIGNED', 'QUEUED', 'SENDING',
    'WAITING_SEFAZ', 'AUTHORIZED', 'REJECTED', 'ERROR', 'CANCELLATION_REQUESTED', 'CANCELLED')),
  request_hash char(64) not null check (request_hash ~ '^[a-f0-9]{64}$'),
  issued_at timestamptz,
  signed_at timestamptz,
  tax_rule_snapshot jsonb,
  schema_reference_id text,
  certificate_fingerprint char(64) check (certificate_fingerprint ~ '^[a-f0-9]{64}$'),
  signed_xml_sha256 char(64) check (signed_xml_sha256 ~ '^[a-f0-9]{64}$'),
  version bigint not null default 0 check (version >= 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id),
  unique (tenant_id, establishment_id, environment_id, external_id),
  unique (tenant_id, environment_id, access_key),
  unique (series_id, number),
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  foreign key (series_id, tenant_id, establishment_id, environment_id, model)
    references fiscal_series(id, tenant_id, establishment_id, environment_id, model),
  check ((series_id is null and number is null) or (series_id is not null and number is not null)),
  check (signed_at is null or (access_key is not null and number is not null and signed_xml_sha256 is not null))
);

-- Ownership never changes. access_key and signed_at never change once set. After
-- the signed boundary (signed_at set), identity and snapshot fields are frozen.
create function fiscal_document_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.establishment_id, new.environment_id, new.request_hash, new.external_id)
     is distinct from
     (old.tenant_id, old.establishment_id, old.environment_id, old.request_hash, old.external_id) then
    raise exception 'fiscal_document ownership and request hash are immutable' using errcode = 'RF001';
  end if;
  if old.access_key is not null and new.access_key is distinct from old.access_key then
    raise exception 'fiscal_document.access_key cannot be reassigned' using errcode = 'RF001';
  end if;
  if old.signed_at is not null and (
       (new.signed_at, new.series_id, new.model, new.number, new.issued_at, new.tax_rule_snapshot,
        new.schema_reference_id, new.certificate_fingerprint, new.signed_xml_sha256)
       is distinct from
       (old.signed_at, old.series_id, old.model, old.number, old.issued_at, old.tax_rule_snapshot,
        old.schema_reference_id, old.certificate_fingerprint, old.signed_xml_sha256)) then
    raise exception 'signed fiscal_document identity and snapshots are immutable' using errcode = 'RF001';
  end if;
  if new.version < old.version then
    raise exception 'fiscal_document.version cannot decrease' using errcode = 'RF002';
  end if;
  return new;
end
$$;

create trigger fiscal_document_guard before update on fiscal_document
  for each row execute function fiscal_document_guard();

create table number_reservation (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  series_id uuid not null,
  model fiscal_model not null,
  number bigint not null,
  document_id uuid,
  owner_epoch bigint not null check (owner_epoch > 0),
  state text not null check (state in ('RESERVED', 'BOUND', 'VOID_PENDING', 'VOIDED', 'GAP_REVIEW')),
  reserved_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (series_id, number),
  unique (document_id),
  foreign key (series_id, tenant_id, establishment_id, environment_id, model)
    references fiscal_series(id, tenant_id, establishment_id, environment_id, model),
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id)
);

-- A reservation must carry the series' current owner epoch and a number the
-- series has already handed out (allocation increments next_number first).
create function number_reservation_insert_guard() returns trigger
  language plpgsql
  as $$
declare
  s record;
begin
  select owner_epoch, next_number into s from fiscal_series where id = new.series_id;
  if not found then
    raise exception 'series not visible for this reservation' using errcode = 'RF006';
  end if;
  if new.owner_epoch <> s.owner_epoch then
    raise exception 'stale owner_epoch for series' using errcode = 'RF006';
  end if;
  if new.number >= s.next_number then
    raise exception 'number was not allocated by the series' using errcode = 'RF006';
  end if;
  return new;
end
$$;

create function number_reservation_update_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.establishment_id, new.environment_id, new.series_id, new.model,
      new.number, new.owner_epoch, new.reserved_at)
     is distinct from
     (old.tenant_id, old.establishment_id, old.environment_id, old.series_id, old.model,
      old.number, old.owner_epoch, old.reserved_at) then
    raise exception 'number_reservation identity is immutable' using errcode = 'RF001';
  end if;
  if old.document_id is not null and new.document_id is distinct from old.document_id then
    raise exception 'number_reservation.document_id cannot be rebound' using errcode = 'RF001';
  end if;
  if old.state = 'VOIDED' and new.state <> 'VOIDED' then
    raise exception 'VOIDED reservation is terminal' using errcode = 'RF003';
  end if;
  return new;
end
$$;

create trigger number_reservation_insert_guard before insert on number_reservation
  for each row execute function number_reservation_insert_guard();
create trigger number_reservation_update_guard before update on number_reservation
  for each row execute function number_reservation_update_guard();
