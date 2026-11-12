-- 0002: tenancy, applications and issuer registration (data-model.md, FF-002, FF-004).

create type fiscal_environment_type as enum ('HOMOLOGATION', 'PRODUCTION');
create type fiscal_model as enum ('55', '65');
create type series_owner_type as enum ('CLOUD', 'LOCAL_AGENT');

create table tenant (
  id uuid primary key,
  name text not null check (length(btrim(name)) between 1 and 200),
  status text not null check (status in ('ACTIVE', 'SUSPENDED')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

-- The secret is stored only as PBKDF2-SHA256 output (ADR-013).
create table application (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  client_id text not null unique check (client_id ~ '^[A-Za-z0-9_-]{16,64}$'),
  name text not null check (length(btrim(name)) between 1 and 200),
  secret_hash bytea not null check (octet_length(secret_hash) = 32),
  secret_salt bytea not null check (octet_length(secret_salt) >= 16),
  secret_iterations integer not null check (secret_iterations >= 600000),
  scopes text[] not null default '{}',
  status text not null check (status in ('ACTIVE', 'SUSPENDED')),
  secret_rotated_at timestamptz not null default now(),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id),
  -- ADR-013 item 6 / D5: authoring and approving fiscal rules never share a credential.
  check (not ('fiscal.rules.approve' = any (scopes) and 'fiscal.rules.write' = any (scopes)))
);

-- Tax IDs are stored canonical (uppercase, no punctuation, 14 characters), so the
-- alphanumeric CNPJ fits without a numeric type. Check digits are validated in the
-- domain with the source reference, not by a rigid pattern here.
create table company (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  legal_name text not null check (length(btrim(legal_name)) between 1 and 200),
  tax_id_canonical text not null check (tax_id_canonical ~ '^[0-9A-Z]{14}$'),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id),
  unique (tenant_id, tax_id_canonical)
);

create table establishment (
  id uuid primary key,
  tenant_id uuid not null references tenant(id),
  company_id uuid not null,
  tax_id_canonical text not null check (tax_id_canonical ~ '^[0-9A-Z]{14}$'),
  state_registration text,
  uf char(2) not null check (uf ~ '^[A-Z]{2}$'),
  municipality_code char(7) not null check (municipality_code ~ '^[0-9]{7}$'),
  cnae text,
  crt smallint not null check (crt > 0),
  address jsonb not null default '{}' check (jsonb_typeof(address) = 'object'),
  status text not null check (status in ('ACTIVE', 'SUSPENDED')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id),
  unique (tenant_id, tax_id_canonical),
  foreign key (company_id, tenant_id) references company(id, tenant_id)
);

create table application_establishment_grant (
  tenant_id uuid not null,
  application_id uuid not null,
  establishment_id uuid not null,
  granted_at timestamptz not null default now(),
  primary key (application_id, establishment_id),
  foreign key (application_id, tenant_id) references application(id, tenant_id),
  foreign key (establishment_id, tenant_id) references establishment(id, tenant_id)
);

create table fiscal_environment (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment fiscal_environment_type not null,
  status text not null check (status in ('ACTIVE', 'INACTIVE')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (id, tenant_id, establishment_id),
  unique (tenant_id, establishment_id, environment),
  foreign key (establishment_id, tenant_id) references establishment(id, tenant_id)
);

-- One ACTIVE configuration per environment; a change creates a new version.
-- csc_secret_ref points to the secret store (FF-006); the CSC itself is never here.
-- schema_policy holds audited releases of package-exclusive elements (E4, FF-009).
create table fiscal_configuration (
  id uuid primary key,
  tenant_id uuid not null,
  establishment_id uuid not null,
  environment_id uuid not null,
  version integer not null check (version > 0),
  timezone text not null check (timezone ~ '^[A-Za-z_]+(/[A-Za-z_+-]+)+$'),
  authorizer text not null check (authorizer ~ '^[A-Z]{2,8}$'),
  catalog_version text not null,
  csc_id text,
  csc_secret_ref text,
  schema_policy jsonb not null default '{}' check (jsonb_typeof(schema_policy) = 'object'),
  status text not null check (status in ('ACTIVE', 'SUPERSEDED')),
  created_by_application_id uuid not null,
  created_at timestamptz not null default now(),
  unique (environment_id, version),
  check ((csc_id is null) = (csc_secret_ref is null)),
  foreign key (environment_id, tenant_id, establishment_id)
    references fiscal_environment(id, tenant_id, establishment_id),
  foreign key (created_by_application_id, tenant_id) references application(id, tenant_id)
);

create unique index fiscal_configuration_one_active
  on fiscal_configuration (environment_id) where status = 'ACTIVE';
