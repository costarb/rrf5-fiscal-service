# SEFAZ-SP NFC-e endpoints

Source checked on **2026-08-04**: [SEFAZ-SP Web Services](https://portal.fazenda.sp.gov.br/servicos/nfce/Paginas/WebServices.aspx).

## Homologation

| Capability | Endpoint |
| --- | --- |
| Authorization | `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeAutorizacao4.asmx` |
| Authorization receipt | `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeRetAutorizacao4.asmx` |
| Number invalidation | `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeInutilizacao4.asmx` |
| Protocol query | `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeConsultaProtocolo4.asmx` |
| Event reception | `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeRecepcaoEvento4.asmx` |
| Service status | `https://homologacao.nfce.fazenda.sp.gov.br/ws/NFeStatusServico4.asmx` |
| QR consultation | `https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode` |

Production uses the corresponding `https://nfce.fazenda.sp.gov.br/` service host shown by the official page. Production endpoints are deliberately not enabled in this discovery branch.

## Capability constraints

- The POC needs an externally supplied homologation A1 credential; it is never committed.
- SOAP success alone does not validate tax treatment.
- A timeout produces an uncertain result: query the access key/protocol before retransmission.
- Offline contingency is a distinct official fiscal mode, not a generic HTTP retry.
- Endpoints must be rechecked and captured before release because government service addresses can change.

