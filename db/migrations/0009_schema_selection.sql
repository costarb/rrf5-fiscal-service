-- 0009: schema selection (F5, T033-T035).

-- Homologation may adopt a package before production (NT schedules give both dates).
-- NULL means the package was already in force when it was catalogued.
alter table schema_version add column homologation_from date;

-- Official artifacts stored in object storage, addressed by content hash (T035).
-- A row exists only after the stored bytes were verified against the catalog hash.
create table regulatory_artifact (
  sha256 char(64) primary key check (sha256 ~ '^[a-f0-9]{64}$'),
  catalog_reference_id text not null check (catalog_reference_id ~ '^[a-z0-9][a-z0-9-]{2,99}$'),
  object_key text not null unique,
  size_bytes bigint not null check (size_bytes > 0),
  object_version text,
  object_lock text not null check (object_lock in ('COMPLIANCE', 'GOVERNANCE', 'UNAVAILABLE')),
  stored_at timestamptz not null default now()
);

create function regulatory_artifact_append_only() returns trigger
  language plpgsql
  as $$
begin
  raise exception 'regulatory_artifact is append-only' using errcode = 'RF005';
end
$$;

create trigger regulatory_artifact_no_change before update or delete on regulatory_artifact
  for each row execute function regulatory_artifact_append_only();

grant select on regulatory_artifact to rrf5_app;
grant select, insert on regulatory_artifact to rrf5_worker;
