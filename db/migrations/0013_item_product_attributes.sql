-- 0013: product attributes sent by the ERP, kept per document item (ADR-017, FR-016).
-- Patterns are those of leiauteNFe_v4.00.xsd (PL_010g v1.03). item_class records which
-- rule of the operation nature covered the item (ncm-<NCM> or ANY), next to the version.
-- fiscal_document_item has no rows yet: nothing writes to it before the emission (Phase 6).

alter table fiscal_document_item
  add column gtin text not null check (gtin ~ '^(SEM GTIN|[0-9]{8}|[0-9]{12,14})$'),
  add column ncm text not null check (ncm ~ '^([0-9]{2}|[0-9]{8})$'),
  add column cest text check (cest ~ '^[0-9]{7}$'),
  add column origin char(1) not null check (origin ~ '^[0-8]$'),
  add column cfop char(4) not null check (cfop ~ '^[123567][0-9]{3}$'),
  add column csosn char(3) not null check (csosn ~ '^[0-9]{3}$');

comment on column fiscal_document_item.item_class is
  'Item class of the operation-nature rule that covered the item: ncm-<NCM> or ANY (ADR-017).';
