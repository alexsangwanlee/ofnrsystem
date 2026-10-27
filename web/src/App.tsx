import { BrowserRouter, Route, Routes } from "react-router";
import { FluentProvider } from "@fluentui/react-components";
import { AppShell } from "./components/AppShell";
import { Agents } from "./pages/Agents";
import { Board } from "./pages/Board";
import { JobDetail } from "./pages/JobDetail";
import { Mail } from "./pages/Mail";
import { ProjectDetail } from "./pages/ProjectDetail";
import { Settings } from "./pages/Settings";
import { Skills } from "./pages/Skills";
import { Today } from "./pages/Today";
import { useAppTheme } from "./theme";

export default function App() {
  const { theme, dark, toggle } = useAppTheme();
  return (
    <FluentProvider theme={theme} style={{ height: "100%" }}>
      <BrowserRouter>
        <Routes>
          <Route element={<AppShell dark={dark} onToggleTheme={toggle} />}>
            <Route index element={<Today />} />
            <Route path="mail/:id?" element={<Mail />} />
            <Route path="projects" element={<Board />} />
            <Route path="projects/:id" element={<ProjectDetail />} />
            <Route path="jobs/:id" element={<JobDetail />} />
            <Route path="agents" element={<Agents />} />
            <Route path="skills" element={<Skills />} />
            <Route path="settings" element={<Settings />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </FluentProvider>
  );
}
