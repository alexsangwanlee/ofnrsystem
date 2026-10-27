import { BrowserRouter, Route, Routes } from "react-router";
import { FluentProvider, Title2 } from "@fluentui/react-components";
import { AppShell } from "./components/AppShell";
import { useAppTheme } from "./theme";

// Task 11 replaces these headings with the real pages.
const page = (title: string) => <Title2 as="h1">{title}</Title2>;

export default function App() {
  const { theme, dark, toggle } = useAppTheme();
  return (
    <FluentProvider theme={theme} style={{ height: "100%" }}>
      <BrowserRouter>
        <Routes>
          <Route element={<AppShell dark={dark} onToggleTheme={toggle} />}>
            <Route index element={page("오늘")} />
            <Route path="mail" element={page("메일")} />
            <Route path="mail/:id" element={page("메일")} />
            <Route path="projects" element={page("프로젝트")} />
            <Route path="projects/:id" element={page("프로젝트 상세")} />
            <Route path="jobs/:id" element={page("작업 상세")} />
            <Route path="agents" element={page("에이전트")} />
            <Route path="skills" element={page("스킬")} />
            <Route path="settings" element={page("설정")} />
          </Route>
        </Routes>
      </BrowserRouter>
    </FluentProvider>
  );
}
