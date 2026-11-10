// Minimal fixed-point decimal on BigInt for the spike. JavaScript has no decimal
// type and `number` is binary floating point, so money cannot use it. This is
// exactly the kind of fiscal-adjacent surface the TypeScript candidate must
// implement or import (decimal.js/big.js) that .NET (decimal) and Java
// (BigDecimal) provide natively.

export class Dec {
  private constructor(readonly units: bigint, readonly scale: number) {}

  static parse(text: string): Dec {
    if (!/^\d+(\.\d+)?$/.test(text)) throw new Error(`Not a non-negative decimal string: ${text}`);
    const [whole, fraction = ""] = text.split(".");
    return new Dec(BigInt(whole + fraction), fraction.length);
  }

  static zero(): Dec {
    return new Dec(0n, 0);
  }

  private rescaleUp(scale: number): bigint {
    return this.units * 10n ** BigInt(scale - this.scale);
  }

  add(other: Dec): Dec {
    const scale = Math.max(this.scale, other.scale);
    return new Dec(this.rescaleUp(scale) + other.rescaleUp(scale), scale);
  }

  sub(other: Dec): Dec {
    const scale = Math.max(this.scale, other.scale);
    return new Dec(this.rescaleUp(scale) - other.rescaleUp(scale), scale);
  }

  mul(other: Dec): Dec {
    return new Dec(this.units * other.units, this.scale + other.scale);
  }

  compare(other: Dec): number {
    const scale = Math.max(this.scale, other.scale);
    const a = this.rescaleUp(scale);
    const b = other.rescaleUp(scale);
    return a === b ? 0 : a < b ? -1 : 1;
  }

  isZero(): boolean {
    return this.units === 0n;
  }

  // Rounds to `scale` decimals. "halfEven" mirrors .NET decimal.Round default;
  // "halfUp" (away from zero for non-negative values) mirrors .NET formatting.
  round(scale: number, mode: "halfEven" | "halfUp"): Dec {
    if (scale >= this.scale) return new Dec(this.rescaleUp(scale), scale);
    const divisor = 10n ** BigInt(this.scale - scale);
    let quotient = this.units / divisor;
    const remainder = this.units % divisor;
    const twice = remainder * 2n;
    if (twice > divisor || (twice === divisor && (mode === "halfUp" || quotient % 2n === 1n))) quotient += 1n;
    return new Dec(quotient, scale);
  }

  toFixed(scale: number): string {
    const rounded = this.round(scale, "halfUp");
    const digits = rounded.units.toString().padStart(scale + 1, "0");
    return scale === 0 ? digits : `${digits.slice(0, -scale)}.${digits.slice(-scale)}`;
  }

  // Equivalent of .NET "0.00########": between 2 and 10 decimals.
  toFlexible(): string {
    let text = this.toFixed(10);
    while (text.endsWith("0") && text.length - text.indexOf(".") - 1 > 2) text = text.slice(0, -1);
    return text;
  }
}
