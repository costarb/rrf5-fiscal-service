# ADR-016 — Geração, assinatura, QR Code e DANFE da NFC-e

**Status:** Aceito pelo operador em 2026-11-12
**Escopo:** `Rrf5.Fiscal.Nfce` (005, FE-002 a FE-006, E5); detalha o ADR-011

## Contexto

O ADR-011 escolheu a implementação fiscal própria. A discovery provou a geração, a assinatura e o QR/DANFE com a POC: houve autorização real na homologação SP, e o golden é idêntico byte a byte em .NET, Java e TypeScript. Na Fase 1 da 005 as fontes oficiais ficaram registradas (`specs/005-nfce-homologation/normative-gates.md`).

## Decisão

1. **Projeto `Rrf5.Fiscal.Nfce`**, sem I/O. Recebe o pedido normalizado, a versão de regra fiscal aprovada (snapshot) e a versão de schema resolvida. Devolve o XML, a chave e os dados de QR e DANFE. A POC é reescrita com testes, não copiada.
2. **Gerador por versão de leiaute:**
   - escreve na ordem do leiaute com `XmlWriter`, UTF-8 sem BOM e sem espaços entre elementos;
   - os valores saem do snapshot da regra; o gerador não infere tributação (FR-017);
   - elementos exclusivos de pacote só saem com liberação auditada (ADR-009).
3. **Valores:** duas casas pela nota (\*4) do Anexo I, arredondando meio longe do zero (`FiscalRounding`). Os totais são somas exatas.
4. **Pagamentos:** `tPag` resolvido na tabela nacional embutida (`PaymentMethods`), dentro da vigência. O mapeamento meio comercial → código é configuração, nunca inferido.
5. **Chave de acesso:** 44 posições com DV módulo 11, CNPJ alfanumérico aceito (NT Conjunta 2025.001) e `cNF` de fonte criptográfica.
6. **Validação XSD antes de assinar:**
   - o XSD vem do pacote resolvido, lido do armazenamento de artefatos com hash conferido;
   - o parser roda com DTD proibido, sem resolver externo e com limites de tamanho e profundidade (TM-09).
7. **Assinatura (MOC 7.0 Visão Geral, §4.2.4, Tabela 4-2 e §4.2.5):**
   - XML-DSig enveloped sobre `infNFe`, com referência ao `Id`;
   - RSA-SHA1, digest SHA-1, transformações Enveloped e C14N (`REC-xml-c14n-20010315`);
   - `X509Data` só com o certificado do emitente (EndCertOnly), sem `X509IssuerSerial` nem `KeyValue`;
   - sem prefixos de namespace, com o namespace declarado em `Signature`.
   
   Só o worker assina. A assinatura é validada, junto com a unicidade do `Id` referenciado, antes do envio.
8. **QR Code:**
   - v3 online pelo Manual DANFE NFC-e v6.0 §4.4.1 (chave, versão e ambiente), como provado na homologação; não usa CSC;
   - a URL vem da configuração por UF e ambiente;
   - o v2 com hash do CSC só entra por decisão posterior.
9. **DANFE:**
   - HTML pronto para impressão em 80 e 58 mm, montado só a partir do `nfeProc` (Manual DANFE v6.0);
   - rótulos de pagamento oficiais;
   - a API entrega o DANFE e os dados de impressão; quem imprime são os ERPs (decisão do operador, 2026-11-12);
   - a impressão é simulada em teste: o QR é decodificado da imagem renderizada.

## Consequências

- **Positivas:**
  - cada regra fica com a fonte citada no código e nos testes;
  - nenhuma biblioteca fiscal de terceiros entra no runtime.
- **Custos:**
  - SHA-1 é imposto pelo MOC; a troca depende de NT;
  - mudanças de leiaute exigem nova versão do gerador e novo golden;
  - a renderização e a leitura de QR em teste exigem dependências só de teste, que passam pelo `dependency-review.md`.
