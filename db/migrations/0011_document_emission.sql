-- 0011: NFC-e emission records (005 T007) and the document state machine in the
-- database (005 T008, data-model.md "State transitions", ADR-005, ADR-007).
--
-- No raw XML goes into the database. Sent XML and nfeProc live in the write-once
-- object store; rows keep their hash, key and version. Items, payments, protocols
-- and SEFAZ exchanges are immutable once written.

-- Document: totals, the signature digest, the emission mode and the artifact references.
alter table fiscal_document
  add column emission_mode text check (emission_mode in ('SYNC', 'ASYNC')),
  add column total_amount numeric(15, 2) check (total_amount >= 0),
  add column change_amount numeric(15, 2) not null default 0 check (change_amount >= 0),
  add column dig_val text check (dig_val ~ '^[A-Za-z0-9+/]{27}=$'),
  add column sent_xml_object_key text,
  add column sent_xml_object_version text,
  add column proc_xml_sha256 char(64) check (proc_xml_sha256 ~ '^[a-f0-9]{64}$'),
  add column proc_xml_object_key text,
  add column proc_xml_object_version text,
  add column authorized_at timestamptz,
  add constraint fiscal_document_signed_digest check (signed_at is null or dig_val is not null),
  add constraint fiscal_document_proc_complete check (
    (proc_xml_sha256 is null and proc_xml_object_key is null)
    or (proc_xml_sha256 is not null and proc_xml_object_key is not null));

create table fiscal_document_item (
  id uuid primary key,
  tenant_id uuid not null,
  document_id uuid not null,
  item_number smallint not null check (item_number between 1 and 990),
  product_code text not null check (length(product_code) between 1 and 60),
  description text not null check (length(description) between 1 and 120),
  item_class text not null check (length(item_class) between 1 and 60),
  unit text not null check (length(unit) between 1 and 6),
  quantity numeric(15, 4) not null check (quantity > 0),
  unit_price numeric(21, 10) not null check (unit_price >= 0),
  line_value numeric(15, 2) not null check (line_value >= 0),
  discount numeric(15, 2) not null default 0 check (discount >= 0),
  surcharge numeric(15, 2) not null default 0 check (surcharge >= 0),
  tax_rule_version_id uuid not null,
  unique (document_id, item_number),
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id),
  check (discount <= line_value)
);

create table fiscal_document_payment (
  id uuid primary key,
  tenant_id uuid not null,
  document_id uuid not null,
  payment_number smallint not null check (payment_number between 1 and 100),
  commercial_method text not null check (commercial_method in
    ('CASH', 'PIX', 'CREDIT_CARD', 'DEBIT_CARD', 'VOUCHER', 'STORE_CREDIT', 'OTHER')),
  t_pag char(2) not null check (t_pag ~ '^[0-9]{2}$'),
  description text check (length(description) between 2 and 60),
  amount numeric(15, 2) not null check (amount >= 0),
  card_integration smallint check (card_integration in (1, 2)),
  acquirer_cnpj text check (acquirer_cnpj ~ '^[0-9A-Z]{12}[0-9]{2}$'),
  card_brand char(2) check (card_brand ~ '^[0-9]{2}$'),
  authorization_code text check (length(authorization_code) between 1 and 128),
  unique (document_id, payment_number),
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id)
);

-- One row per attempt to talk to SEFAZ, written once when the attempt ends.
create table sefaz_exchange (
  id uuid primary key,
  tenant_id uuid not null,
  document_id uuid not null,
  operation text not null check (operation in ('AUTHORIZE', 'CONSULT')),
  outcome text not null check (outcome in ('ANSWERED', 'NOT_SENT', 'TIMEOUT', 'TRANSPORT_ERROR', 'UNREADABLE')),
  c_stat integer check (c_stat between 100 and 9999),  -- TStat [0-9]{3,4}
  request_sha256 char(64) not null check (request_sha256 ~ '^[a-f0-9]{64}$'),
  request_object_key text,
  request_object_version text,
  response_sha256 char(64) check (response_sha256 ~ '^[a-f0-9]{64}$'),
  response_object_key text,
  response_object_version text,
  http_status smallint,
  started_at timestamptz not null,
  finished_at timestamptz not null,
  unique (id, tenant_id),
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id),
  check (finished_at >= started_at),
  check ((outcome = 'ANSWERED') = (c_stat is not null and response_sha256 is not null))
);

-- Official answers about one document: the batch answer (retEnviNFe without protNFe,
-- e.g. 225), the protocol (protNFe) or a consultation answer (retConsSitNFe).
create table fiscal_protocol (
  id uuid primary key,
  tenant_id uuid not null,
  document_id uuid not null,
  exchange_id uuid not null,
  kind text not null check (kind in ('BATCH', 'PROTOCOL', 'CONSULTATION')),
  c_stat integer not null check (c_stat between 100 and 9999),  -- TStat [0-9]{3,4}
  x_motivo text not null check (length(x_motivo) between 1 and 255),
  n_prot text check (n_prot ~ '^([0-9]{15}|[0-9]{17})$'),  -- TProt
  ch_nfe text check (ch_nfe ~ '^[0-9]{6}[0-9A-Z]{12}[0-9]{26}$'),  -- TChNFe
  dig_val text check (dig_val ~ '^[A-Za-z0-9+/]{27}=$'),
  received_at timestamptz not null,
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id),
  foreign key (exchange_id, tenant_id) references sefaz_exchange(id, tenant_id),
  check (c_stat <> 100 or (kind <> 'BATCH' and n_prot is not null and ch_nfe is not null and dig_val is not null))
);

-- Transactional outbox (ADR-005): written with the document, published by the worker.
create table document_outbox (
  id uuid primary key,
  tenant_id uuid not null,
  document_id uuid not null,
  kind text not null check (kind in ('PROCESS', 'RECONCILE')),
  available_at timestamptz not null default now(),
  published_at timestamptz,
  attempts integer not null default 0 check (attempts >= 0),
  created_at timestamptz not null default now(),
  foreign key (document_id, tenant_id) references fiscal_document(id, tenant_id)
);
create index document_outbox_pending on document_outbox (tenant_id, available_at) where published_at is null;

-- Items, payments, exchanges and protocols never change once written.
create function emission_record_append_only() returns trigger
  language plpgsql
  as $$
begin
  raise exception '% is append-only', tg_table_name using errcode = 'RF005';
end
$$;

do $$
declare
  t text;
begin
  foreach t in array array['fiscal_document_item', 'fiscal_document_payment', 'sefaz_exchange', 'fiscal_protocol']
  loop
    execute format('create trigger %I before update or delete on %I for each row execute function emission_record_append_only()', t || '_append_only', t);
    execute format('create trigger %I before truncate on %I for each statement execute function emission_record_append_only()', t || '_no_truncate', t);
  end loop;
end
$$;

-- The outbox only moves forward: published once, attempts never decrease.
create function document_outbox_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.tenant_id, new.document_id, new.kind, new.created_at) is distinct from (old.tenant_id, old.document_id, old.kind, old.created_at) then
    raise exception 'document_outbox identity is immutable' using errcode = 'RF001';
  end if;
  if old.published_at is not null and new.published_at is distinct from old.published_at then
    raise exception 'document_outbox.published_at is set once' using errcode = 'RF001';
  end if;
  if new.attempts < old.attempts then
    raise exception 'document_outbox.attempts cannot decrease' using errcode = 'RF002';
  end if;
  return new;
end
$$;
create trigger document_outbox_guard before update on document_outbox
  for each row execute function document_outbox_guard();

-- The existing guard also freezes the signature digest, and the nfeProc reference once set.
create or replace function fiscal_document_guard() returns trigger
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
        new.schema_reference_id, new.certificate_fingerprint, new.signed_xml_sha256, new.dig_val,
        new.total_amount, new.change_amount, new.emission_mode)
       is distinct from
       (old.signed_at, old.series_id, old.model, old.number, old.issued_at, old.tax_rule_snapshot,
        old.schema_reference_id, old.certificate_fingerprint, old.signed_xml_sha256, old.dig_val,
        old.total_amount, old.change_amount, old.emission_mode)) then
    raise exception 'signed fiscal_document identity and snapshots are immutable' using errcode = 'RF001';
  end if;
  if old.sent_xml_object_key is not null and (new.sent_xml_object_key, new.sent_xml_object_version)
     is distinct from (old.sent_xml_object_key, old.sent_xml_object_version) then
    raise exception 'fiscal_document sent XML reference is write-once' using errcode = 'RF001';
  end if;
  if old.proc_xml_sha256 is not null and (new.proc_xml_sha256, new.proc_xml_object_key, new.proc_xml_object_version, new.authorized_at)
     is distinct from (old.proc_xml_sha256, old.proc_xml_object_key, old.proc_xml_object_version, old.authorized_at) then
    raise exception 'fiscal_document nfeProc reference is write-once' using errcode = 'RF001';
  end if;
  if new.version < old.version then
    raise exception 'fiscal_document.version cannot decrease' using errcode = 'RF002';
  end if;
  return new;
end
$$;

-- State machine (data-model.md): only these edges exist. ERROR is entered only from
-- the stages before a request leaves; after sending, doubt means WAITING_SEFAZ.
create function fiscal_document_transition() returns trigger
  language plpgsql
  as $$
declare
  allowed boolean;
  last_authorize timestamptz;
begin
  if tg_op = 'INSERT' then
    if new.state not in ('DRAFT', 'VALIDATING', 'READY_TO_SIGN') then
      raise exception 'a fiscal_document starts as DRAFT, VALIDATING or READY_TO_SIGN, not %', new.state using errcode = 'RF003';
    end if;
    return new;
  end if;

  if new.state = old.state then
    return new;
  end if;

  allowed := case old.state
    when 'DRAFT' then new.state = 'VALIDATING'
    when 'VALIDATING' then new.state = 'READY_TO_SIGN'
    when 'READY_TO_SIGN' then new.state in ('SIGNING', 'ERROR')
    when 'SIGNING' then new.state in ('SIGNED', 'ERROR')
    when 'SIGNED' then new.state in ('QUEUED', 'ERROR')
    when 'QUEUED' then new.state in ('SENDING', 'ERROR')
    when 'SENDING' then new.state in ('WAITING_SEFAZ', 'AUTHORIZED', 'REJECTED', 'QUEUED')
    when 'WAITING_SEFAZ' then new.state in ('AUTHORIZED', 'REJECTED', 'QUEUED')
    when 'ERROR' then (new.state = 'READY_TO_SIGN' and old.signed_at is null)
                   or (new.state = 'QUEUED' and old.signed_at is not null)
    when 'AUTHORIZED' then new.state = 'CANCELLATION_REQUESTED'
    when 'CANCELLATION_REQUESTED' then new.state in ('CANCELLED', 'AUTHORIZED')
    else false
  end;
  if not allowed then
    raise exception 'fiscal_document cannot move from % to %', old.state, new.state using errcode = 'RF003';
  end if;

  if new.state = 'SIGNING' and (new.series_id is null or new.number is null) then
    raise exception 'a number is reserved before signing' using errcode = 'RF006';
  end if;

  if new.state = 'SENDING' and new.sent_xml_object_key is null then
    raise exception 'the signed XML is stored before it is sent' using errcode = 'RF006';
  end if;

  -- AUTHORIZED: protocol cStat 100 for exactly this key and digest, and the nfeProc stored.
  if new.state = 'AUTHORIZED' and old.state <> 'CANCELLATION_REQUESTED' then
    if not exists (
      select 1 from fiscal_protocol p
      where p.document_id = new.id and p.tenant_id = new.tenant_id and p.c_stat = 100
        and p.ch_nfe = new.access_key and p.dig_val = new.dig_val) then
      raise exception 'AUTHORIZED needs a cStat 100 protocol with this access key and digest' using errcode = 'RF006';
    end if;
    if new.proc_xml_sha256 is null or new.authorized_at is null then
      raise exception 'AUTHORIZED needs the stored nfeProc' using errcode = 'RF006';
    end if;
  end if;

  if new.state = 'REJECTED' and not exists (
      select 1 from fiscal_protocol p
      where p.document_id = new.id and p.tenant_id = new.tenant_id and p.c_stat <> 100) then
    raise exception 'REJECTED needs the official answer' using errcode = 'RF006';
  end if;

  -- Resend after a sent request only when a later consultation answered 217 (not found).
  if old.state = 'WAITING_SEFAZ' and new.state = 'QUEUED' then
    select max(finished_at) into last_authorize from sefaz_exchange
      where document_id = new.id and tenant_id = new.tenant_id and operation = 'AUTHORIZE';
    if not exists (
      select 1 from sefaz_exchange e
      where e.document_id = new.id and e.tenant_id = new.tenant_id and e.operation = 'CONSULT'
        and e.outcome = 'ANSWERED' and e.c_stat = 217
        and (last_authorize is null or e.started_at >= last_authorize)) then
      raise exception 'a document waiting on SEFAZ is resent only after a consultation answers 217' using errcode = 'RF006';
    end if;
  end if;

  return new;
end
$$;

create trigger fiscal_document_transition before insert or update of state on fiscal_document
  for each row execute function fiscal_document_transition();

-- Row-level security, like 0007.
do $$
declare
  t text;
begin
  foreach t in array array['fiscal_document_item', 'fiscal_document_payment', 'sefaz_exchange', 'fiscal_protocol', 'document_outbox']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy tenant_isolation on %I using (tenant_id = app_current_tenant()) '
      'with check (tenant_id = app_current_tenant())', t);
  end loop;
end
$$;

-- API: creates the document with its items, payments and outbox message; reads results.
grant select, insert on fiscal_document_item, fiscal_document_payment, document_outbox to rrf5_app;
grant select on sefaz_exchange, fiscal_protocol to rrf5_app;

-- Worker: numbers, signs, sends and finalizes documents (ADR-005).
grant select on fiscal_document_item, fiscal_document_payment to rrf5_worker;
grant select, insert on sefaz_exchange, fiscal_protocol to rrf5_worker;
grant select, insert, update on document_outbox to rrf5_worker;
grant update on fiscal_document, fiscal_series, number_reservation to rrf5_worker;
grant insert on number_reservation to rrf5_worker;
