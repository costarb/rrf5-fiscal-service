// Splits the TypeScript bench loop into generate / sign / verify to see where the
// time goes. Synthetic inputs and a throwaway key only (RRF5_A1_PASSWORD).
import { readFile } from "node:fs/promises";
import { loadA1, sign, verify } from "./dsig.js";
import { generate } from "./generate.js";

const [fixturePath, profilePath, issuerPath, pfxPath] = process.argv.slice(2) as [string, string, string, string];
const fixture = JSON.parse(await readFile(fixturePath, "utf8"));
const profile = JSON.parse(await readFile(profilePath, "utf8"));
const issuer = JSON.parse(await readFile(issuerPath, "utf8"));
const a1 = loadA1(await readFile(pfxPath), process.env.RRF5_A1_PASSWORD ?? "");

const totals = { generate: 0, sign: 0, verify: 0 };
const iterations = 300;
for (let i = 1; i <= 50 + iterations; i++) {
  const t0 = performance.now();
  const doc = generate(fixture, profile, issuer, i, "12345678", "2026-11-06T12:00:00-03:00");
  const t1 = performance.now();
  const signed = sign(doc.xml, a1);
  const t2 = performance.now();
  if (!verify(signed, a1.certificateDer).verified) throw new Error("verify failed");
  const t3 = performance.now();
  if (i > 50) {
    totals.generate += t1 - t0;
    totals.sign += t2 - t1;
    totals.verify += t3 - t2;
  }
}
const per = (v: number) => Number((v / iterations).toFixed(3));
process.stdout.write(`${JSON.stringify({ iterations, msPerOp: { generate: per(totals.generate), sign: per(totals.sign), verify: per(totals.verify) } })}\n`);
