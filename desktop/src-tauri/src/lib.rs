use std::{sync::Mutex, time::Duration};

use tauri::{
    menu::{Menu, MenuItem},
    tray::TrayIconBuilder,
    AppHandle, Manager, RunEvent, WindowEvent,
};
use tauri_plugin_autostart::ManagerExt as _;
use tauri_plugin_dialog::{DialogExt, MessageDialogKind};
use tauri_plugin_shell::{
    process::{CommandChild, CommandEvent},
    ShellExt,
};
use tokio::sync::watch;

/// Includes a first launch after install, when Defender scans the single-file server and it runs migrations.
const READY_TIMEOUT: Duration = Duration::from_secs(60);

#[derive(Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct ServerInfo {
    base_url: String,
    token: String,
}

struct Sidecar {
    token: String,
    child: Mutex<Option<CommandChild>>,
    /// Taken once, by whichever comes first: the OFNR_READY line, an early exit or the timeout.
    ready: Mutex<Option<watch::Sender<Option<ServerInfo>>>>,
    info: watch::Receiver<Option<ServerInfo>>,
}

/// The line the server prints once Kestrel has bound its port, e.g. "OFNR_READY http://127.0.0.1:53817".
fn ready_url(line: &[u8]) -> Option<String> {
    let line = String::from_utf8_lossy(line);
    let url = line.trim().strip_prefix("OFNR_READY ")?;
    url.starts_with("http://127.0.0.1:").then(|| url.to_string())
}

fn kill_sidecar(app: &AppHandle) {
    if let Some(child) = app.state::<Sidecar>().child.lock().unwrap().take() {
        // The child runs in a job object (set_process_group), so this also ends the claude and codex CLIs it started.
        let _ = child.kill();
    }
}

fn settle(app: &AppHandle, result: Result<String, String>) {
    let state = app.state::<Sidecar>();
    let Some(ready) = state.ready.lock().unwrap().take() else { return };
    match result {
        Ok(base_url) => {
            ready.send_replace(Some(ServerInfo { base_url, token: state.token.clone() }));
        }
        Err(detail) => {
            drop(ready); // server_info now fails instead of waiting
            kill_sidecar(app);
            let exit = app.clone();
            app.dialog()
                .message(format!("서버를 시작하지 못했습니다.\n{detail}"))
                .title("OFNR 운영")
                .kind(MessageDialogKind::Error)
                .show(move |_| exit.exit(1));
        }
    }
}

fn show_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.unminimize();
        let _ = w.show();
        let _ = w.set_focus();
    }
}

#[tauri::command]
async fn server_info(state: tauri::State<'_, Sidecar>) -> Result<ServerInfo, String> {
    let mut info = state.info.clone();
    let ready = info.wait_for(Option::is_some).await.map_err(|_| "서버를 시작하지 못했습니다.".to_string())?;
    Ok(ready.clone().unwrap())
}

/// Called by the update flow between download() and install(); install() exits without RunEvent::Exit on Windows.
#[tauri::command]
fn stop_backend(app: AppHandle) {
    kill_sidecar(&app)
}

#[tauri::command]
fn set_autostart(app: AppHandle, enabled: bool) -> Result<(), String> {
    let launcher = app.autolaunch();
    if enabled { launcher.enable() } else { launcher.disable() }.map_err(|e| e.to_string())
}

#[tauri::command]
fn autostart_enabled(app: AppHandle) -> bool {
    app.autolaunch().is_enabled().unwrap_or(false)
}

fn start_sidecar(app: &tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let token: String = rand::random::<[u8; 32]>().iter().map(|b| format!("{b:02x}")).collect();
    let (mut events, child) = app
        .shell()
        .sidecar("ofnr-server")?
        .args(["--sidecar"])
        // Env, not argv: other processes can read a command line.
        .env("OFNR_TOKEN", &token)
        .env("OFNR_CONTENT", app.path().resource_dir()?)
        .set_process_group(true)
        .spawn()?;
    let (ready, info) = watch::channel(None);
    app.manage(Sidecar { token, child: Mutex::new(Some(child)), ready: Mutex::new(Some(ready)), info });

    let handle = app.handle().clone();
    // Keeps reading after OFNR_READY: the plugin's channel holds one event, so an unread one blocks the server's stdout.
    tauri::async_runtime::spawn(async move {
        while let Some(event) = events.recv().await {
            match event {
                CommandEvent::Stdout(line) => {
                    if let Some(url) = ready_url(&line) {
                        settle(&handle, Ok(url));
                    }
                }
                CommandEvent::Stderr(line) => eprintln!("[server] {}", String::from_utf8_lossy(&line)),
                CommandEvent::Terminated(t) => settle(&handle, Err(format!("서버가 종료되었습니다. (코드 {:?})", t.code))),
                _ => {}
            }
        }
    });

    let handle = app.handle().clone();
    std::thread::spawn(move || {
        std::thread::sleep(READY_TIMEOUT);
        settle(&handle, Err("서버가 응답하지 않습니다.".into()));
    });
    Ok(())
}

pub fn run() {
    tauri::Builder::default()
        // Must be registered first.
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| show_main(app)))
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_autostart::Builder::new().build())
        .plugin(tauri_plugin_window_state::Builder::default().build())
        .setup(|app| {
            start_sidecar(app)?;
            let open = MenuItem::with_id(app, "open", "열기", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "종료", true, None::<&str>)?;
            let mut tray = TrayIconBuilder::new()
                .tooltip("OFNR 운영")
                .menu(&Menu::with_items(app, &[&open, &quit])?)
                .on_menu_event(|app, e| match e.id.as_ref() {
                    "open" => show_main(app),
                    "quit" => app.exit(0),
                    _ => {}
                });
            if let Some(icon) = app.default_window_icon() {
                tray = tray.icon(icon.clone());
            }
            tray.build(app)?;
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing the window keeps mail sync and running jobs going; 종료 in the tray quits.
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![server_info, stop_backend, set_autostart, autostart_enabled])
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|app, event| {
            if let RunEvent::Exit = event {
                kill_sidecar(app)
            }
        });
}

#[cfg(test)]
mod tests {
    use super::ready_url;

    #[test]
    fn reads_only_the_loopback_ready_line() {
        assert_eq!(ready_url(b"OFNR_READY http://127.0.0.1:53817\r\n").as_deref(), Some("http://127.0.0.1:53817"));
        assert_eq!(ready_url(b"info: Microsoft.Hosting.Lifetime[0]"), None);
        assert_eq!(ready_url(b"OFNR_READY http://evil.example:80"), None);
    }
}
