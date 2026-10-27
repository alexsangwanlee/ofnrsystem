import { useCallback, useEffect, useState, type ReactElement } from "react";
import { Outlet, useLinkClickHandler, useLocation, useNavigate } from "react-router";
import {
  Button,
  CounterBadge,
  NavDrawer,
  NavDrawerBody,
  NavItem,
  Text,
  Tooltip,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  Board20Regular,
  BookOpen20Regular,
  Bot20Regular,
  Home20Regular,
  Mail20Regular,
  Settings20Regular,
  WeatherMoon20Regular,
  WeatherSunny20Regular,
} from "@fluentui/react-icons";
import { api } from "../api";
import { useOps } from "../realtime";
import type { TodayDto } from "../types";

const NAV: { to: string; label: string; icon: ReactElement }[] = [
  { to: "/", label: "오늘", icon: <Home20Regular /> },
  { to: "/mail", label: "메일", icon: <Mail20Regular /> },
  { to: "/projects", label: "프로젝트", icon: <Board20Regular /> },
  { to: "/agents", label: "에이전트", icon: <Bot20Regular /> },
  { to: "/skills", label: "스킬", icon: <BookOpen20Regular /> },
  { to: "/settings", label: "설정", icon: <Settings20Regular /> },
];

const useStyles = makeStyles({
  root: {
    display: "grid",
    gridTemplateColumns: "200px minmax(0, 1fr)",
    gridTemplateRows: "48px minmax(0, 1fr)",
    height: "100%",
  },
  header: {
    gridColumn: "1 / -1",
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    paddingInline: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground4,
    borderBottom: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  spacer: { flexGrow: 1 },
  nav: {
    width: "200px",
    height: "100%",
    borderRight: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
  },
  main: {
    overflow: "auto",
    padding: tokens.spacingHorizontalXXL,
    backgroundColor: tokens.colorNeutralBackground2,
  },
});

function NavLinkItem({ to, label, icon }: { to: string; label: string; icon: ReactElement }) {
  const onClick = useLinkClickHandler<HTMLAnchorElement>(to);
  return (
    <NavItem as="a" value={to} href={to} icon={icon} onClick={onClick}>
      {label}
    </NavItem>
  );
}

export function AppShell({ dark, onToggleTheme }: { dark: boolean; onToggleTheme: () => void }) {
  const s = useStyles();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const [attention, setAttention] = useState(0);

  const refresh = useCallback(() => {
    // The badge is a hint; a failed refresh keeps the last count and the 오늘 page shows the error.
    api<TodayDto>("/api/today")
      .then((t) => setAttention(t.counts.attention))
      .catch(() => undefined);
  }, []);
  useEffect(refresh, [refresh]);
  useOps({ approvalRequested: refresh, approvalResolved: refresh, jobUpdated: refresh });

  return (
    <div className={s.root}>
      <header className={s.header}>
        <img src="/ofnr-mark.png" alt="" width={28} height={28} />
        <Text weight="semibold" size={400}>
          OFNR 운영
        </Text>
        <span className={s.spacer} />
        {attention > 0 && (
          <Button
            appearance="subtle"
            aria-label={`확인 필요 ${attention}건`}
            icon={<CounterBadge count={attention} color="brand" size="small" />}
            iconPosition="after"
            onClick={() => navigate("/")}
          >
            확인 필요
          </Button>
        )}
        <Tooltip content={dark ? "밝은 테마로 전환" : "어두운 테마로 전환"} relationship="label">
          <Button
            appearance="subtle"
            icon={dark ? <WeatherSunny20Regular /> : <WeatherMoon20Regular />}
            onClick={onToggleTheme}
          />
        </Tooltip>
      </header>
      <NavDrawer open type="inline" className={s.nav} selectedValue={`/${pathname.split("/")[1]}`}>
        <NavDrawerBody>
          {NAV.map((item) => (
            <NavLinkItem key={item.to} {...item} />
          ))}
        </NavDrawerBody>
      </NavDrawer>
      <main className={s.main}>
        <Outlet />
      </main>
    </div>
  );
}
