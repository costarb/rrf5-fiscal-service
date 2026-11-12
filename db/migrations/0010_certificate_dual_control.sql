-- 0010: dual control of A1 activation and ICP-Brasil chain metadata (TM-03, ADR-004).
-- An uploaded certificate is PENDING_APPROVAL; a different application holding
-- certificates.approve activates or rejects it. The two scopes never share an
-- application, so upload and approval always need two credentials.

alter table digital_certificate drop constraint digital_certificate_status_check;
alter table digital_certificate add constraint digital_certificate_status_check
  check (status in ('PENDING_APPROVAL', 'ACTIVE', 'SUPERSEDED', 'REVOKED', 'REJECTED'));

alter table digital_certificate
  add column uploaded_by_application_id uuid,
  add column approver_application_id uuid,
  add column approved_at timestamptz,
  add column rejected_at timestamptz,
  add column rejection_reason text,
  add column chain_root text;

alter table digital_certificate
  add constraint digital_certificate_uploader_fk foreign key (uploaded_by_application_id, tenant_id) references application(id, tenant_id),
  add constraint digital_certificate_approver_fk foreign key (approver_application_id, tenant_id) references application(id, tenant_id),
  add constraint digital_certificate_rejected check ((status = 'REJECTED') = (rejected_at is not null)),
  add constraint digital_certificate_four_eyes check (approver_application_id is null or approver_application_id <> uploaded_by_application_id);

-- Every certificate that is or was active carries its approval. NOT VALID keeps rows
-- loaded before this migration (local development only) without inventing approvals.
alter table digital_certificate add constraint digital_certificate_approved
  check (status in ('PENDING_APPROVAL', 'REJECTED') or (approver_application_id is not null and approved_at is not null)) not valid;

-- A rejected upload must not block a corrected upload of the same certificate.
alter table digital_certificate drop constraint digital_certificate_environment_id_fingerprint_sha256_key;
create unique index digital_certificate_fingerprint_live
  on digital_certificate (environment_id, fingerprint_sha256) where status <> 'REJECTED';
create unique index digital_certificate_one_pending
  on digital_certificate (environment_id) where status = 'PENDING_APPROVAL';

drop trigger digital_certificate_guard on digital_certificate;

create or replace function digital_certificate_guard() returns trigger
  language plpgsql
  as $$
declare
  approver_scopes text[];
begin
  if tg_op = 'INSERT' then
    if new.status <> 'PENDING_APPROVAL' or new.uploaded_by_application_id is null or new.approver_application_id is not null then
      raise exception 'a certificate starts PENDING_APPROVAL with its uploader and no approver' using errcode = 'RF003';
    end if;
    return new;
  end if;

  if (new.tenant_id, new.establishment_id, new.environment_id, new.vault_ref, new.fingerprint_sha256,
      new.subject_tax_id, new.not_before, new.not_after, new.created_at, new.uploaded_by_application_id, new.chain_root)
     is distinct from
     (old.tenant_id, old.establishment_id, old.environment_id, old.vault_ref, old.fingerprint_sha256,
      old.subject_tax_id, old.not_before, old.not_after, old.created_at, old.uploaded_by_application_id, old.chain_root) then
    raise exception 'digital_certificate metadata is immutable' using errcode = 'RF001';
  end if;

  if new.status = old.status then
    if (new.approver_application_id, new.approved_at) is distinct from (old.approver_application_id, old.approved_at) then
      raise exception 'approval record is immutable' using errcode = 'RF001';
    end if;
    return new;
  end if;

  if old.status = 'PENDING_APPROVAL' and new.status = 'ACTIVE' then
    if new.approver_application_id is null or new.approved_at is null then
      raise exception 'activation needs an approver' using errcode = 'RF004';
    end if;
    if new.approver_application_id = new.uploaded_by_application_id then
      raise exception 'the uploader cannot activate its own certificate' using errcode = 'RF004';
    end if;
    select scopes into approver_scopes from application
      where id = new.approver_application_id and tenant_id = new.tenant_id and status = 'ACTIVE';
    if approver_scopes is null or not ('certificates.approve' = any (approver_scopes)) then
      raise exception 'approver lacks the certificates.approve scope' using errcode = 'RF004';
    end if;
    return new;
  end if;

  if old.status = 'PENDING_APPROVAL' and new.status = 'REJECTED' then
    return new;
  end if;

  if old.status = 'ACTIVE' and new.status in ('SUPERSEDED', 'REVOKED') then
    if (new.approver_application_id, new.approved_at) is distinct from (old.approver_application_id, old.approved_at) then
      raise exception 'approval record is immutable' using errcode = 'RF001';
    end if;
    return new;
  end if;

  raise exception 'certificate transition % -> % is not allowed', old.status, new.status using errcode = 'RF003';
end
$$;

create trigger digital_certificate_guard before insert or update on digital_certificate
  for each row execute function digital_certificate_guard();

-- Upload and approval of certificates never share a credential (as with fiscal rules).
alter table application add constraint application_certificate_four_eyes
  check (not ('certificates.approve' = any (scopes) and 'certificates.manage' = any (scopes)));
