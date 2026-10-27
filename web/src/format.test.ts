import { describe, expect, it } from "vitest";
import { dday, formatKrw, stageLabel, toolLabel } from "./format";
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

  it("labels tools in Korean and keeps unknown names raw", () => {
    expect(toolLabel("mcp__ofnr__get_project")).toBe("프로젝트 정보 읽기");
    expect(toolLabel("mcp__ofnr__ask_ceo")).toBe("대표에게 질문");
    expect(toolLabel("Grep")).toBe("파일 찾기");
    expect(toolLabel("PowerShell")).toBe("명령 실행");
    expect(toolLabel("command_execution")).toBe("명령 실행");
    expect(toolLabel("file_change")).toBe("파일 변경");
    expect(toolLabel("mcp__excel__write_range")).toBe("엑셀: write range");
    expect(toolLabel("mcp__photoshop__export_layer_png")).toBe("포토샵: export layer png");
    expect(toolLabel("mcp__other__do_thing")).toBe("mcp__other__do_thing");
    expect(toolLabel("constructor")).toBe("constructor");
  });
});
