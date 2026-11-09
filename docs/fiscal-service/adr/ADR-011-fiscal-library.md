# ADR-011 — Biblioteca fiscal própria ou de terceiros

**Status:** Aceito pelo operador em 2026-11-07  
**Data:** 2026-11-07  
**Escopo:** NFC-e modelo 65, SEFAZ-SP, runtime recomendado .NET 10 (scorecard T029; ADR-012)

## Contexto

O ADR original preferia uma biblioteca consolidada, desde que validadas manutenção,
cobertura, RTC, licença, testes, suporte e auditabilidade. A discovery implementou as
primitivas sobre as bibliotecas da plataforma .NET e as provou contra a SEFAZ-SP:

| Capacidade | Implementação própria (.NET, bibliotecas da plataforma) | Evidência |
| --- | --- | --- |
| Geração determinística + XSD 010e v1.02 | PASS | `pocs/evidence/xml-xsd-poc.md` |
| XMLDSig com A1, verificação e mutação negativa | PASS — assinatura aceita pela SEFAZ | `xmldsig-poc.md` |
| SOAP autorização e consulta em homologação | PASS — cStat 100, host e container | `homologation.md`, `container-poc.md` |
| Resultado incerto: consultar antes de reenviar | PASS | `homologation.md`, `pocs/shared/uncertain-result` |
| DANFE NFC-e e QR v3 | PASS (online); impressão térmica pendente | `danfe-qr.md` |
| Dependências | 3 pacotes Microsoft + QRCoder (MIT); 0 vulnerabilidades | `dependency-review.md` |

## Avaliação do Unimake.DFe (2026-11-07)

Pacote `Unimake.DFe` 20261106.1734.46, inspecionado sem execução (nupkg do NuGet,
recursos lidos por *reflection-only*), e repositório `github.com/Unimake/DFe`.

| Critério | Achado |
| --- | --- |
| Licença | MIT no GitHub; o nuspec não declara licença (sem `license`/`licenseUrl`) |
| Manutenção | Muito ativa: 722 versões, publicação diária, 1 issue aberta. Versionamento por data, sem semver nem canal estável |
| Resposta a NT | Pacote oficial 010e v1.02 (publicado 2026-07-10) incorporado em 2026-07-14 |
| Fidelidade aos XSD oficiais | **Corrigido em 2026-11-07 (ver Emenda).** Segue o pacote oficial vigente 010g v1.03: 4 dos 5 XSD de NF-e são iguais ao oficial; em `leiauteNFe_v4.00.xsd` a única diferença é um prefixo de namespace no XPath das duas restrições `xs:unique` de `gPgto`, que torna efetiva a unicidade que o seletor oficial sem prefixo não aplica. Em 2026-06-30 o schema foi relaxado para aceitar `tpNFCredito` 06 antes do pacote oficial |
| Empacotamento | `netstandard2.0`, DLL única de 24,6 MB com todos os DF-e (NF-e, CT-e, MDF-e, NFS-e etc.) e 1.687 recursos; dependências `Unimake.Utils`, `Unimake.Extensions`, `Unimake.Cryptography`, `System.Net.Http.WinHttpHandler` |
| Linux/container | Não provado; `WinHttpHandler` é específico de Windows |
| Prova na nossa POC | Nenhuma: não instalado, sem homologação, sem comparação com o golden XML |

Java_NFe não foi avaliado: só se aplica se o ADR-012 escolher Java.

## Decisão

1. **Implementação própria sobre as bibliotecas da plataforma .NET** para o núcleo da
   NFC-e: geração, validação XSD, XMLDSig, transporte SOAP, autorização, consulta,
   eventos (cancelamento, inutilização) e DANFE/QR.
2. **A validação usa sempre o pacote XSD oficial congelado e com hash** da
   `normative-baseline.md`, nunca schemas embutidos em biblioteca de terceiros.
3. **Unimake.DFe não é dependência de runtime.** Fica como referência: sinal precoce de
   mudanças de NT e oráculo opcional em testes (comparar XML gerado), sempre subordinado
   à documentação oficial.
4. A decisão é **revisada por capacidade**. Se um módulo futuro (por exemplo, contingência
   offline ou outros modelos de DF-e) tiver custo próprio alto, uma biblioteca pode ser
   adotada para ele após passar pelos mesmos gates: XSD oficial, homologação SP,
   container Linux, SBOM e licença.
5. Qualquer código fiscal fica atrás de interfaces próprias; nenhum tipo de biblioteca
   aparece na API pública.

## Justificativa

- A implementação própria já passou por todos os gates de US1 contra a SEFAZ-SP; a
  biblioteca não passou por nenhum.
- A biblioteca embute os XSD e os ajusta pontualmente (unicidade de `gPgto`,
  aceite antecipado de `tpNFCredito` 06); a validação do serviço precisa ser
  reprodutível contra o pacote oficial congelado com hash, não contra uma cópia
  embutida que muda a cada publicação diária.
- Superfície e cadeia de fornecimento menores: 4 pacotes contra uma DLL de 24,6 MB com
  dependências próprias e sem versionamento semântico, num serviço que guarda
  certificado A1.
- Portabilidade Linux já provada no caminho próprio; não provada na biblioteca.

## Consequências

- Mudanças de NT exigem trabalho próprio: acompanhar o portal da NF-e, congelar o novo
  pacote com hash, atualizar golden XML e regras. A agilidade do Unimake ajuda como
  alerta, não como fonte.
- Eventos, inutilização e contingência offline ainda precisam ser implementados e
  homologados; são o maior risco do caminho próprio.
- Revisar esta decisão se o custo de manutenção por NT ficar acima do planejado ou se
  surgir biblioteca que use os XSD oficiais sem alteração e prove Linux + homologação SP.
## Emenda — 2026-11-07

A versão aceita desta ADR afirmava que o Unimake.DFe editava os XSD oficiais. A
comparação tinha sido feita contra a baseline 010e v1.02, que estava desatualizada. O
operador confirmou em fonte oficial que o pacote vigente é o **010g v1.03** (com o
intermediário 010f v1.04), e as mudanças atribuídas à biblioteca estão nesse pacote
oficial. A linha de fidelidade e a justificativa correspondente foram corrigidas acima.

Efeito na avaliação: a resposta a NT do Unimake é melhor do que registrado, pois
acompanhou 010f e 010g antes da nossa baseline. Os demais fundamentos da decisão não
dependem desse ponto: implementação própria provada em homologação SP e container,
cadeia de fornecimento menor e portabilidade Linux não provada na biblioteca. O
Unimake passa a ser também a referência prática para detectar novos pacotes XSD.
O operador reconfirmou a decisão em 2026-11-08, após a correção. O pacote 010g
v1.03 foi congelado nessa data (NT 2026.006 e NT 2026.008, publicado em 2026-10-26),
e as datas de publicação de 010f (2026-08-31) e 010g coincidem com os commits do
Unimake de 2026-09-08 e 2026-10-26.
