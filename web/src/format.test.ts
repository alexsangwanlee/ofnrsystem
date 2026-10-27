import { describe, expect, it } from "vitest";
import { dday, formatKrw, stageLabel } from "./format";
import { STAGES } from "./types";

const today = new Date(2026, 9, 27, 15, 30);
const daysFrom = (n: number) => new Date(2026, 9, 27 + n, 9, 0);

describe("format", () => {
  it("formats KRW with thousands separators and 원", () => {
    expect(formatKrw(1250000)).toBe("1,250,000원");
  });

  it("counts D-day from local calendar days", () => {
    expect(dday(daysFrom(3), today)).toBe("D-3");
    expect(dday(daysFrom(0), today)).toBe("D-day");
    expect(dday(daysFrom(-2), today)).toBe("D+2");
    expect(dday("2026-10-30", today)).toBe("D-3");
  });

  it("gives every stage a Korean label", () => {
    expect(STAGES.map(stageLabel)).toEqual(["문의", "기획", "디자인", "제작", "운영", "리포트", "완료"]);
  });
});
