-- 0006: idempotency, audit log and schema versions (FF-003, FF-009, FF-010).

-- Administrative mutations are keyed by the calling tenant; establishment_id is
-- set for establishment-scoped operations and NULL otherwise (NULLS NOT DISTINCT
-- keeps a single claim per key in both cases). The request hash never changes.
-- response_body is the replayable response and must never contain a secret:
-- an operation that shows a secret once stores a redacted body (T010).
create table idempotency_record (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  establishment_id uuid,
  operation text not null check (operation ~ '^[a-z][a-z0-9_.]{1,99}$'),
  idempotency_key text not null check (length(idempotency_key) between 1 and 255),
  request_hash char(64) not null check (request_hash ~ '^[a-f0-9]{64}$'),
  state text not null check (state in ('IN_PROGRESS', 'COMPLETED')),
  resource_type text,
  resource_id uuid,
  document_id uuid,
  response_status smallint check (response_status between 100 and 599),
  response_body jsonb,
  created_at timestamptz not null default now(),
  completed_at timestamptz,
  unique nulls not distinct (tenant_id, establishment_id, operation, idempotency_key),
  foreign key (establishment_id, tenant_id) references establishment(id, tenant_id),
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id),
  check ((state = 'COMPLETED') = (response_status is not null and completed_at is not null))
);

create function idempotency_record_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.establishment_id, new.operation, new.idempotency_key, new.request_hash, new.created_at)
     is distinct from
     (old.tenant_id, old.establishment_id, old.operation, old.idempotency_key, old.request_hash, old.created_at) then
    raise exception 'idempotency claim and request hash are immutable' using errcode = 'RF001';
  end if;
  if old.state = 'COMPLETED' then
    raise exception 'a completed idempotency record is immutable' using errcode = 'RF001';
  end if;
  return new;
end
$$;

create trigger idempotency_record_guard before update on idempotency_record
  for each row execute function idempotency_record_guard();

-- Append-only. tenant_id is NULL only for platform-level events (for example a
-- token request for an unknown client). details is redacted before insert (T012).
create table audit_log (
  id bigint generated always as identity primary key,
  occurred_at timestamptz not null default now(),
  tenant_id uuid references tenant(id),
  principal_type text not null check (principal_type in ('APPLICATION', 'SYSTEM', 'ANONYMOUS')),
  principal_id text,
  action text not null check (action ~ '^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$'),
  resource_type text not null check (length(resource_type) between 1 and 100),
  resource_id text,
  outcome text not null check (outcome in ('SUCCEEDED', 'DENIED', 'FAILED')),
  correlation_id text not null check (length(correlation_id) between 1 and 128),
  details jsonb not null default '{}' check (jsonb_typeof(details) = 'object'),
  check ((principal_type = 'APPLICATION') = (principal_id is not null))
);

create index audit_log_tenant_time on audit_log (tenant_id, occurred_at);

create function audit_log_append_only() returns trigger
  language plpgsql
  as $$
begin
  raise exception 'audit_log is append-only' using errcode = 'RF005';
end
$$;

create trigger audit_log_no_update before update or delete on audit_log
  for each row execute function audit_log_append_only();
create trigger audit_log_no_truncate before truncate on audit_log
  for each statement execute function audit_log_append_only();

-- XSD packages from regulatory/catalog.json. Only packages with a verified
-- artifact hash can exist (T033); a loaded row never changes its identity or hash.
create table schema_version (
  id uuid primary key,
  catalog_reference_id text not null unique check (catalog_reference_id ~ '^[a-z0-9][a-z0-9-]{2,99}$'),
  package text not null check (package ~ '^[0-9]{3}[a-z]$'),
  package_version text not null check (length(package_version) between 1 and 32),
  models fiscal_model[] not null check (cardinality(models) > 0),
  artifact_sha256 char(64) not null check (artifact_sha256 ~ '^[a-f0-9]{64}$'),
  hash_status text not null check (hash_status = 'VERIFIED'),
  source_url text not null check (source_url ~ '^https://'),
  effective_from date,
  effective_to date,
  loaded_at timestamptz not null default now(),
  check (effective_to is null or effective_from is null or effective_to > effective_from)
);

create function schema_version_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.catalog_reference_id, new.package, new.package_version, new.artifact_sha256, new.models)
     is distinct from
     (old.catalog_reference_id, old.package, old.package_version, old.artifact_sha256, old.models) then
    raise exception 'schema_version identity and hash are immutable' using errcode = 'RF001';
  end if;
  return new;
end
$$;

create trigger schema_version_guard before update on schema_version
  for each row execute function schema_version_guard();
