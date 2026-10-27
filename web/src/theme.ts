import { useState, useSyncExternalStore } from "react";
import { createDarkTheme, createLightTheme, type BrandVariants, type Theme } from "@fluentui/react-components";

const INK = "#1a1c1c";
const PAPER = "#f9f4eb";

// 10..80 darken toward black and 80 is the ink itself; 90..160 run from ink to paper.
const brand: BrandVariants = {
  10: "#030404",
  20: "#060707",
  30: "#0a0a0a",
  40: "#0d0e0e",
  50: "#101212",
  60: "#141515",
  70: "#171818",
  80: INK,
  90: "#363736",
  100: "#525250",
  110: "#6e6d6a",
  120: "#8a8884",
  130: "#a5a39d",
  140: "#c1beb7",
  150: "#ddd9d1",
  160: PAPER,
};

const shared = {
  fontFamilyBase: 'Pretendard Variable, Pretendard, "Malgun Gothic", sans-serif',
  // One radius scale: 4px controls, 8px cards and dialogs.
  borderRadiusSmall: "4px",
  borderRadiusMedium: "4px",
  borderRadiusLarge: "8px",
  borderRadiusXLarge: "8px",
};

export const lightTheme: Theme = {
  ...createLightTheme(brand),
  ...shared,
  // Warm neutrals next to the paper canvas instead of Fluent's cool grays (navigation and header use Background4).
  colorNeutralBackground2: PAPER,
  colorNeutralBackground4: "#f1ece2",
  colorNeutralBackground4Hover: "#e8e2d7",
  colorNeutralBackground4Pressed: "#ded8cc",
  colorNeutralBackground4Selected: "#e8e2d7",
  // Darker than ink is invisible, so hover and press lighten instead.
  colorBrandBackgroundHover: brand[90],
  colorBrandBackgroundPressed: brand[100],
  colorBrandBackgroundSelected: brand[90],
  colorCompoundBrandBackgroundHover: brand[90],
  colorCompoundBrandBackgroundPressed: brand[100],
};

const darkBase = createDarkTheme(brand);

// On dark surfaces the ink end is invisible, so brand foregrounds and fills come from the paper end.
export const darkTheme: Theme = {
  ...darkBase,
  ...shared,
  colorBrandBackground: brand[150],
  colorBrandBackgroundHover: brand[160],
  colorBrandBackgroundPressed: brand[140],
  colorBrandBackgroundSelected: brand[150],
  colorNeutralForegroundOnBrand: INK,
  colorBrandForeground1: brand[150],
  colorBrandForeground2: brand[160],
  colorBrandForeground2Hover: brand[160],
  colorBrandForegroundLink: brand[150],
  colorBrandForegroundLinkHover: brand[160],
  colorBrandForegroundLinkPressed: brand[140],
  colorBrandForegroundLinkSelected: brand[150],
  colorCompoundBrandForeground1: brand[150],
  colorCompoundBrandForeground1Hover: brand[160],
  colorCompoundBrandForeground1Pressed: brand[140],
  colorCompoundBrandBackground: brand[150],
  colorCompoundBrandBackgroundHover: brand[160],
  colorCompoundBrandBackgroundPressed: brand[140],
  colorCompoundBrandStroke: brand[150],
  colorCompoundBrandStrokeHover: brand[160],
  colorCompoundBrandStrokePressed: brand[140],
  colorBrandStroke1: brand[150],
  colorNeutralForeground2BrandHover: brand[150],
  colorNeutralForeground2BrandPressed: brand[140],
  colorNeutralForeground2BrandSelected: brand[150],
  colorNeutralForeground3BrandHover: brand[150],
  colorNeutralForeground3BrandPressed: brand[140],
  colorNeutralForeground3BrandSelected: brand[150],
  colorNeutralStrokeAccessibleSelected: brand[150],
  // Filled danger, severe and success badges also use the on-brand (ink) text color, which is about
  // 3.5:1 on Fluent's saturated fills; the lighter tints reach 6:1. Progress bars and presence icons
  // in dark mode get the lighter tints too.
  colorPaletteRedBackground3: darkBase.colorPaletteRedForeground1,
  colorPaletteDarkOrangeBackground3: darkBase.colorPaletteDarkOrangeForeground1,
  colorPaletteGreenBackground3: darkBase.colorPaletteGreenForeground1,
};

const THEME_KEY = "ofnr.theme";
const DARK_QUERY = "(prefers-color-scheme: dark)";

function subscribeScheme(onChange: () => void) {
  const mq = window.matchMedia?.(DARK_QUERY);
  mq?.addEventListener("change", onChange);
  return () => mq?.removeEventListener("change", onChange);
}

function readChoice(): "light" | "dark" | null {
  try {
    const v = localStorage.getItem(THEME_KEY);
    return v === "light" || v === "dark" ? v : null;
  } catch {
    return null;
  }
}

/** Follows the OS scheme until the user toggles; the toggle is remembered in localStorage. */
export function useAppTheme() {
  const [choice, setChoice] = useState(readChoice);
  const systemDark = useSyncExternalStore(subscribeScheme, () => window.matchMedia?.(DARK_QUERY).matches ?? false);
  const dark = choice ? choice === "dark" : systemDark;
  const toggle = () => {
    const next = dark ? "light" : "dark";
    setChoice(next);
    try {
      localStorage.setItem(THEME_KEY, next);
    } catch {
      // Storage can be unavailable; the toggle still applies for this session.
    }
  };
  return { theme: dark ? darkTheme : lightTheme, dark, toggle };
}
