-- 0014: the consumer identified on the sale (group dest), needed to generate the XML in
-- the worker (005 T020). Only a CPF is accepted for NFC-e here (MOC rule E05-10). The
-- name is optional. Both are frozen with the rest of the signed document.

alter table fiscal_document
  add column consumer_cpf char(11) check (consumer_cpf ~ '^[0-9]{11}$'),
  add column consumer_name text check (length(btrim(consumer_name)) between 2 and 60),
  add constraint fiscal_document_consumer_name_needs_cpf check (consumer_name is null or consumer_cpf is not null);

create or replace function fiscal_document_consumer_guard() returns trigger
  language plpgsql
  as $$
begin
  if (new.consumer_cpf, new.consumer_name) is distinct from (old.consumer_cpf, old.consumer_name) then
    raise exception 'fiscal_document consumer is immutable' using errcode = 'RF001';
  end if;
  return new;
end
$$;

create trigger fiscal_document_consumer_guard before update on fiscal_document
  for each row execute function fiscal_document_consumer_guard();
