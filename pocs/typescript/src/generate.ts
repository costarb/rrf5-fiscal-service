// T016 spike (TypeScript): deterministic NFC-e 4.00 generation mirroring the
// .NET and Java POCs. Node has no standard XML writer, so text is assembled with
// explicit escaping. Every fiscal value comes from the profile file.
import { Dec } from "./decimal.js";

const NFE_NS = "http://www.portalfiscal.inf.br/nfe";
const HOMOLOGATION_FIRST_ITEM = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";

type Json = Record<string, unknown>;

export class RefusedError extends Error {}

function require(condition: boolean, message: string): asserts condition {
  if (!condition) throw new RefusedError(message);
}

function obj(node: unknown, name: string): Json {
  const value = (node as Json | undefined)?.[name];
  require(typeof value === "object" && value !== null && !Array.isArray(value), `Missing object property '${name}'.`);
  return value as Json;
}

function str(node: unknown, name: string): string {
  const value = (node as Json | undefined)?.[name];
  if (typeof value !== "string" || value.trim() === "") throw new RefusedError(`Missing or empty string property '${name}'.`);
  return value;
}

function escapeText(value: string): string {
  return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

export function accessKeyCheckDigit(keyBody: string): number {
  let sum = 0;
  let weight = 2;
  for (let i = keyBody.length - 1; i >= 0; i--) {
    sum += (keyBody.charCodeAt(i) - 48) * weight;
    weight = weight === 9 ? 2 : weight + 1;
  }
  const remainder = sum % 11;
  return remainder < 2 ? 0 : 11 - remainder;
}

export interface GenerateResult {
  xml: string;
  accessKey: string;
}

// dhEmi is passed as the exact ISO text with offset; its local date fields are
// used as written (no clock, no time-zone conversion).
export function generate(fixture: Json, profile: Json, issuer: Json, nNF: number, cNF: string, dhEmi: string): GenerateResult {
  require(str(fixture, "documentType") === "NFCE", "Fixture documentType must be NFCE.");
  require(str(fixture, "environment") === "HOMOLOGATION", "Only HOMOLOGATION is supported by the POC.");
  require(str(profile, "documentType") === "NFCE_65" && str(profile, "uf") === "SP", "Profile must be NFCE_65/SP.");
  require(obj(profile, "ibsCbs").send === false, "Profile requests IBS/CBS groups; not implemented.");
  require(obj(profile, "vTotTrib").send === false, "Profile requests vTotTrib; source not implemented.");
  require(/^\d{8}$/.test(cNF), "cNF must be 8 digits.");
  require(Number.isInteger(nNF) && nNF > 0 && nNF <= 999_999_999, "nNF out of range.");
  const match = /^(\d{4})-(\d{2})-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$/.exec(dhEmi);
  require(match !== null, "dhEmi must be yyyy-MM-ddTHH:mm:ss+hh:mm.");

  const profileId = str(profile, "profileId");
  const item0 = obj(profile, "item");
  const address = obj(issuer, "address");
  const cnpj = str(issuer, "cnpj");
  const series = Number.parseInt(str(issuer, "series"), 10);
  const cUF = "35", mod = "65", tpEmis = "1", tpAmb = "2";

  const keyBody = cUF + match[1]!.slice(2) + match[2]! + cnpj + mod + String(series).padStart(3, "0")
    + String(nNF).padStart(9, "0") + tpEmis + cNF;
  require(/^\d{43}$/.test(keyBody), "Access key components are malformed.");
  const cDV = accessKeyCheckDigit(keyBody);
  const accessKey = keyBody + cDV;

  const items = (fixture.items as Json[] | undefined) ?? [];
  require(Array.isArray(items) && items.length > 0, "Fixture has no items.");

  const out: string[] = [];
  const e = (name: string, value: string) => out.push(`<${name}>${escapeText(value)}</${name}>`);
  const open = (name: string) => out.push(`<${name}>`);
  const close = (name: string) => out.push(`</${name}>`);

  out.push(`<?xml version="1.0" encoding="utf-8"?><NFe xmlns="${NFE_NS}"><infNFe versao="4.00" Id="NFe${accessKey}">`);
  open("ide");
  e("cUF", cUF); e("cNF", cNF); e("natOp", str(profile, "natOp")); e("mod", mod);
  e("serie", String(series)); e("nNF", String(nNF)); e("dhEmi", dhEmi);
  e("tpNF", "1"); e("idDest", "1"); e("cMunFG", str(address, "cityCode"));
  e("tpImp", "4"); e("tpEmis", tpEmis); e("cDV", String(cDV)); e("tpAmb", tpAmb);
  e("finNFe", "1"); e("indFinal", "1"); e("indPres", "1"); e("procEmi", "0");
  e("verProc", "RRF5-POC-0.1");
  close("ide");

  open("emit");
  e("CNPJ", cnpj); e("xNome", str(issuer, "name"));
  open("enderEmit");
  e("xLgr", str(address, "street")); e("nro", str(address, "number"));
  e("xBairro", str(address, "district")); e("cMun", str(address, "cityCode"));
  e("xMun", str(address, "city")); e("UF", str(address, "uf")); e("CEP", str(address, "zip"));
  e("cPais", "1058"); e("xPais", "Brasil");
  close("enderEmit");
  e("IE", str(issuer, "ie")); e("CRT", str(profile, "crt"));
  close("emit");

  let vProd = Dec.zero();
  items.forEach((item, index) => {
    require(str(item, "fiscalProfileId") === profileId, `Item ${index + 1} references a different fiscal profile.`);
    const qty = Dec.parse(str(item, "quantity"));
    const unit = Dec.parse(str(item, "unitPrice"));
    const total = Dec.parse(str(item, "totalPrice"));
    require(qty.mul(unit).round(2, "halfEven").compare(total) === 0, `Item ${index + 1}: quantity x unitPrice != totalPrice.`);
    vProd = vProd.add(total);

    out.push(`<det nItem="${index + 1}">`);
    open("prod");
    e("cProd", str(item, "productId")); e("cEAN", str(item0, "gtin"));
    e("xProd", index === 0 ? HOMOLOGATION_FIRST_ITEM : str(item, "description"));
    e("NCM", str(item0, "ncm")); e("CFOP", str(item0, "cfop"));
    e("uCom", str(item0, "unit")); e("qCom", qty.toFixed(4));
    e("vUnCom", unit.toFlexible()); e("vProd", total.toFixed(2));
    e("cEANTrib", str(item0, "gtin")); e("uTrib", str(item0, "unit"));
    e("qTrib", qty.toFixed(4)); e("vUnTrib", unit.toFlexible());
    e("indTot", "1");
    close("prod");

    open("imposto");
    const icms = obj(item0, "icms");
    require(str(icms, "group") === "ICMSSN102", "Only ICMSSN102 is implemented.");
    open("ICMS"); open("ICMSSN102");
    e("orig", str(icms, "orig")); e("CSOSN", str(icms, "csosn"));
    close("ICMSSN102"); close("ICMS");
    for (const [tax, rateName, valueName] of [["PIS", "pPIS", "vPIS"], ["COFINS", "pCOFINS", "vCOFINS"]] as const) {
      const config = obj(item0, tax.toLowerCase());
      require(str(config, "group") === `${tax}Outr`, `Only ${tax}Outr is implemented.`);
      const rate = Dec.parse(str(config, "rate"));
      require(rate.isZero(), `Only zero-rate ${tax} is implemented.`);
      open(tax); open(`${tax}Outr`);
      e("CST", str(config, "cst")); e("vBC", "0.00"); e(rateName, rate.toFixed(4)); e(valueName, "0.00");
      close(`${tax}Outr`); close(tax);
    }
    close("imposto");
    close("det");
  });

  const vNF = vProd;
  open("total"); open("ICMSTot");
  for (const name of ["vBC", "vICMS", "vICMSDeson", "vFCP", "vBCST", "vST", "vFCPST", "vFCPSTRet"]) e(name, "0.00");
  e("vProd", vProd.toFixed(2));
  for (const name of ["vFrete", "vSeg", "vDesc", "vII", "vIPI", "vIPIDevol", "vPIS", "vCOFINS", "vOutro"]) e(name, "0.00");
  e("vNF", vNF.toFixed(2));
  close("ICMSTot"); close("total");

  open("transp"); e("modFrete", "9"); close("transp");

  open("pag");
  let paid = Dec.zero();
  let change = Dec.zero();
  for (const payment of (fixture.payments as Json[] | undefined) ?? []) {
    require(str(payment, "method") === "CASH", "Only CASH is mapped in the POC.");
    const amount = Dec.parse(str(payment, "amount"));
    paid = paid.add(amount);
    change = change.add(Dec.parse(str(payment, "change")));
    open("detPag"); e("indPag", "0"); e("tPag", "01"); e("vPag", amount.toFixed(2)); close("detPag");
  }
  require(paid.sub(change).compare(vNF) === 0, "Payments minus change must equal vNF.");
  if (!change.isZero()) e("vTroco", change.toFixed(2));
  close("pag");

  const tech = (issuer as Json).technicalResponsible;
  if (typeof tech === "object" && tech !== null) {
    open("infRespTec");
    e("CNPJ", str(tech, "cnpj")); e("xContato", str(tech, "contact"));
    e("email", str(tech, "email")); e("fone", str(tech, "phone"));
    close("infRespTec");
  }
  close("infNFe");

  open("infNFeSupl");
  e("qrCode", `${str(issuer, "qrCodeUrl")}?p=${accessKey}|3|${tpAmb}`);
  e("urlChave", str(issuer, "consultationUrl"));
  close("infNFeSupl");
  close("NFe");

  return { xml: out.join(""), accessKey };
}
