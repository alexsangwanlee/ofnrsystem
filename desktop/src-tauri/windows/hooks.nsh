!macro NSIS_HOOK_PREINSTALL
  ; Tauri checks only the main exe for a running instance; a live sidecar would lock ofnr-server.exe.
  nsExec::Exec 'taskkill /F /T /IM ofnr-server.exe'
!macroend
