-- 0005: tax profiles, rules and immutable rule versions (ADR-015, FF-007).
-- The database enforces the lifecycle, four-eyes approval, approver scope,
-- non-retroactive validity and non-overlapping APPROVED intervals. It never
-- interprets the payload: payload consistency is validated by the domain (T030).

create table tax_profile (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  code text not null check (code ~ '^[A-Z0-9][A-Z0-9-]{1,63}$'),
  name text not null check (length(btrim(name)) between 1 and 200),
  status text not null check (status in ('ACTIVE', 'INACTIVE')),
  created_by_application_id uuid not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id),
  unique (tenant_id, code),
  foreign key (created_by_application_id, tenant_id) references application(id, tenant_id)
);

create table tax_rule (
  id uuid primary key,
  tenant_id uuid not null,
  profile_id uuid not null,
  model fiscal_model not null,
  uf char(2) not null check (uf ~ '^[A-Z]{2}$'),
  operation text not null check (operation ~ '^[A-Z0-9][A-Z0-9_]{1,63}$'),
  item_class text not null check (item_class ~ '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$'),
  created_at timestamptz not null default now(),
  unique (id, tenant_id),
  unique (tenant_id, profile_id, model, uf, operation, item_class),
  foreign key (profile_id, tenant_id) references tax_profile(id, tenant_id)
);

create function tax_rule_guard() returns trigger
  language plpgsql
  as $$
begin
  raise exception 'tax_rule dimensions are immutable; create a new rule' using errcode = 'RF001';
end
$$;

create trigger tax_rule_guard before update on tax_rule
  for each row execute function tax_rule_guard();

-- Validity is [valid_from, valid_to) in Sao Paulo civil dates; NULL valid_to is open-ended.
create table tax_rule_version (
  id uuid primary key,
  tenant_id uuid not null,
  rule_id uuid not null,
  version_number integer not null check (version_number > 0),
  status text not null check (status in ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'RETIRED')),
  payload jsonb not null check (jsonb_typeof(payload) = 'object'),
  generator_version text not null check (length(generator_version) between 1 and 64),
  valid_from date not null,
  valid_to date,
  source_refs jsonb not null
    check (jsonb_typeof(source_refs) = 'array' and jsonb_array_length(source_refs) > 0),
  author_application_id uuid not null,
  submitted_at timestamptz,
  approver_application_id uuid,
  approved_at timestamptz,
  retired_at timestamptz,
  rejection_reason text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id),
  unique (rule_id, version_number),
  foreign key (rule_id, tenant_id) references tax_rule(id, tenant_id),
  foreign key (author_application_id, tenant_id) references application(id, tenant_id),
  foreign key (approver_application_id, tenant_id) references application(id, tenant_id),
  check (valid_to is null or valid_to > valid_from),
  check (approver_application_id is null or approver_application_id <> author_application_id),
  check ((status in ('APPROVED', 'RETIRED')) = (approver_application_id is not null and approved_at is not null)),
  check ((status = 'RETIRED') = (retired_at is not null)),
  check (status = 'DRAFT' or submitted_at is not null),
  exclude using gist (rule_id with =, daterange(valid_from, valid_to, '[)') with &&)
    where (status = 'APPROVED')
);

create function tax_rule_version_guard() returns trigger
  language plpgsql
  as $$
declare
  content_changed boolean;
  approver_scopes text[];
begin
  if tg_op = 'INSERT' then
    if new.status <> 'DRAFT' then
      raise exception 'a rule version starts as DRAFT' using errcode = 'RF003';
    end if;
    return new;
  end if;

  if tg_op = 'DELETE' then
    if old.status <> 'DRAFT' then
      raise exception 'only a DRAFT rule version can be deleted' using errcode = 'RF003';
    end if;
    return old;
  end if;

  if (new.tenant_id, new.rule_id, new.version_number, new.author_application_id, new.created_at)
     is distinct from
     (old.tenant_id, old.rule_id, old.version_number, old.author_application_id, old.created_at) then
    raise exception 'tax_rule_version identity is immutable' using errcode = 'RF001';
  end if;

  content_changed := (new.payload, new.generator_version, new.valid_from, new.source_refs)
                     is distinct from
                     (old.payload, old.generator_version, old.valid_from, old.source_refs);

  if old.status = 'DRAFT' then
    if new.status not in ('DRAFT', 'PENDING_APPROVAL') then
      raise exception 'DRAFT may only move to PENDING_APPROVAL' using errcode = 'RF003';
    end if;
    return new;
  end if;

  if content_changed or (old.status <> 'APPROVED' and new.valid_to is distinct from old.valid_to) then
    raise exception 'only a DRAFT rule version can be edited; create a new version' using errcode = 'RF001';
  end if;

  if old.status = 'PENDING_APPROVAL' then
    if new.status = 'DRAFT' then
      return new;
    end if;
    if new.status <> 'APPROVED' then
      raise exception 'PENDING_APPROVAL may only move to APPROVED or back to DRAFT' using errcode = 'RF003';
    end if;
    if new.approver_application_id = new.author_application_id then
      raise exception 'the author cannot approve its own rule version' using errcode = 'RF004';
    end if;
    select scopes into approver_scopes from application
      where id = new.approver_application_id and tenant_id = new.tenant_id and status = 'ACTIVE';
    if approver_scopes is null or not ('fiscal.rules.approve' = any (approver_scopes)) then
      raise exception 'approver lacks the fiscal.rules.approve scope' using errcode = 'RF004';
    end if;
    if new.valid_from < app_today_sp() then
      raise exception 'validity cannot start before the approval date' using errcode = 'RF006';
    end if;
    return new;
  end if;

  if old.status = 'APPROVED' then
    if (new.approver_application_id, new.approved_at, new.submitted_at)
       is distinct from (old.approver_application_id, old.approved_at, old.submitted_at) then
      raise exception 'approval record is immutable' using errcode = 'RF001';
    end if;
    -- An open-ended approved version may be closed once, never retroactively.
    if new.valid_to is distinct from old.valid_to then
      if old.valid_to is not null or new.valid_to is null or new.valid_to < app_today_sp() then
        raise exception 'valid_to can only be set once, to today or later' using errcode = 'RF006';
      end if;
    end if;
    if new.status not in ('APPROVED', 'RETIRED') then
      raise exception 'APPROVED may only move to RETIRED' using errcode = 'RF003';
    end if;
    return new;
  end if;

  raise exception 'RETIRED rule version is terminal' using errcode = 'RF003';
end
$$;

create trigger tax_rule_version_guard before insert or update or delete on tax_rule_version
  for each row execute function tax_rule_version_guard();
