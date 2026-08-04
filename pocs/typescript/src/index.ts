const result = {
  candidate: "typescript",
  runtime: process.version,
  scope: "fiscal-discovery-poc",
  productionReady: false,
} as const;

process.stdout.write(`${JSON.stringify(result)}\n`);

