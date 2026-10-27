import { describe, expect, it } from "vitest";
import { darkTheme, lightTheme } from "./theme";

const luminance = (hex: string) => {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const contrast = (a: string, b: string) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

describe("theme", () => {
  // Fluent puts colorNeutralForegroundOnBrand text on these fills (primary buttons, filled badges).
  const fills = [
    "colorBrandBackground",
    "colorPaletteRedBackground3",
    "colorPaletteDarkOrangeBackground3",
    "colorPaletteGreenBackground3",
  ] as const;

  it.each([
    ["light", lightTheme],
    ["dark", darkTheme],
  ])("keeps on-brand text at WCAG AA on every filled color in the %s theme", (_, theme) => {
    for (const fill of fills) {
      expect(contrast(theme.colorNeutralForegroundOnBrand, theme[fill]), fill).toBeGreaterThanOrEqual(4.5);
    }
  });
});
