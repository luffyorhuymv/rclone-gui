#!/usr/bin/env bash
set -euo pipefail

APP_NAME="RcloneDrive Linux"
DEFAULT_MOUNT="$HOME/mnt/rclone-drive"
DEFAULT_PROJECT="public_html"
LOG_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/rclone-drive"
LOG_FILE="$LOG_DIR/rclone-drive.log"

mkdir -p "$LOG_DIR"

log() {
  local level="${1:-INFO}"
  shift || true
  printf '%s  %-5s %s\n' "$(date '+%Y/%m/%d %H:%M:%S')" "$level" "$*" | tee -a "$LOG_FILE"
}

die() {
  log "ERROR" "$*"
  exit 1
}

usage() {
  cat <<'EOF'
RcloneDrive Linux

Usage:
  ./linux/rclone-drive-linux.sh install
  ./linux/rclone-drive-linux.sh list
  ./linux/rclone-drive-linux.sh mount <remote:path> [mount_dir]
  ./linux/rclone-drive-linux.sh unmount [mount_dir]
  ./linux/rclone-drive-linux.sh opencode [mount_dir] [project_subpath]
  ./linux/rclone-drive-linux.sh webui

Examples:
  ./linux/rclone-drive-linux.sh install
  ./linux/rclone-drive-linux.sh list
  ./linux/rclone-drive-linux.sh mount phukientudien:/ ~/mnt/phukientudien
  ./linux/rclone-drive-linux.sh opencode ~/mnt/phukientudien public_html
  ./linux/rclone-drive-linux.sh unmount ~/mnt/phukientudien

Notes:
  - Linux does not use X:/Y:/ drive letters. Mount to a folder instead.
  - For OpenCode, open a concrete project folder such as public_html, not the mount root.
EOF
}

have() {
  command -v "$1" >/dev/null 2>&1
}

sudo_cmd() {
  if [ "$(id -u)" -eq 0 ]; then
    "$@"
  elif have sudo; then
    sudo "$@"
  else
    die "Can thiet quyen root hoac sudo de cai goi: $*"
  fi
}

install_packages() {
  local packages=("$@")
  [ "${#packages[@]}" -gt 0 ] || return 0

  if have apt-get; then
    sudo_cmd apt-get update
    sudo_cmd apt-get install -y "${packages[@]}"
  elif have dnf; then
    sudo_cmd dnf install -y "${packages[@]}"
  elif have yum; then
    sudo_cmd yum install -y "${packages[@]}"
  elif have pacman; then
    sudo_cmd pacman -Sy --needed --noconfirm "${packages[@]}"
  elif have zypper; then
    sudo_cmd zypper install -y "${packages[@]}"
  else
    die "Khong nhan dien duoc package manager. Hay cai thu cong: ${packages[*]}"
  fi
}

ensure_curl() {
  if ! have curl; then
    log "INFO" "Thieu curl, dang cai..."
    install_packages curl
  fi
}

ensure_git() {
  if have git; then
    return 0
  fi
  log "WARN" "Thieu git.exe/git. Dang tu cai Git..."
  install_packages git
  have git || die "Cai Git xong nhung van khong thay lenh git."
}

ensure_fuse() {
  if [ -e /dev/fuse ]; then
    return 0
  fi

  log "WARN" "Chua thay /dev/fuse. Dang cai FUSE..."
  if have apt-get; then
    install_packages fuse3
  elif have pacman; then
    install_packages fuse3
  else
    install_packages fuse
  fi

  [ -e /dev/fuse ] || log "WARN" "Van chua thay /dev/fuse. Neu chay trong WSL/container, hay bat FUSE hoac dung may Linux that."
}

ensure_rclone() {
  if have rclone; then
    return 0
  fi

  log "WARN" "Thieu rclone. Dang tai va cai rclone tu trang chinh thuc..."
  ensure_curl
  local installer
  installer="$(mktemp)"
  curl -fsSL https://rclone.org/install.sh -o "$installer"
  sudo_cmd bash "$installer"
  rm -f "$installer"
  have rclone || die "Cai rclone xong nhung van khong thay lenh rclone."
}

install_all() {
  ensure_git
  ensure_fuse
  ensure_rclone
  log "INFO" "Da san sang: git, fuse, rclone."
}

remote_type() {
  local source="$1"
  local remote="${source%%:*}:"
  rclone config show "$remote" 2>/dev/null | awk -F'=' '/^[[:space:]]*type[[:space:]]*=/{gsub(/[[:space:]]/,"",$2); print tolower($2); exit}'
}

is_mounted() {
  local mount_dir="$1"
  findmnt -rn --target "$mount_dir" >/dev/null 2>&1
}

mount_remote() {
  local source="${1:-}"
  local mount_dir="${2:-$DEFAULT_MOUNT}"
  [ -n "$source" ] || die "Thieu remote:path. Vi du: phukientudien:/"

  ensure_rclone
  ensure_fuse
  mkdir -p "$mount_dir"

  if is_mounted "$mount_dir"; then
    log "INFO" "Thu muc da mount: $mount_dir"
    return 0
  fi

  log "INFO" "Kiem tra remote truoc khi mount: $source"
  if ! rclone lsf "$source" --max-depth 1 >/tmp/rclone-drive-lsf.$$ 2>&1; then
    cat /tmp/rclone-drive-lsf.$$ | tee -a "$LOG_FILE" >&2 || true
    rm -f /tmp/rclone-drive-lsf.$$
    die "Remote chua san sang, sai dang nhap hoac sai duong dan: $source"
  fi
  rm -f /tmp/rclone-drive-lsf.$$

  local type
  type="$(remote_type "$source")"

  local args=(
    mount "$source" "$mount_dir"
    --daemon
    --links
    --vfs-cache-mode full
    --vfs-cache-max-age 168h
    --vfs-cache-max-size 20G
    --vfs-write-back 2s
    --vfs-read-ahead 4M
    --vfs-read-chunk-size 4M
    --vfs-read-chunk-size-limit 64M
    --vfs-cache-poll-interval 30s
    --dir-cache-time 30m
    --attr-timeout 1m
    --transfers 1
    --checkers 1
    --buffer-size 16M
    --log-level INFO
    --log-file "$LOG_FILE"
  )

  if [ "${ALLOW_OTHER:-0}" = "1" ]; then
    args+=(--allow-other)
  fi

  if [ "$type" = "ftp" ] || [ "$type" = "sftp" ]; then
    args+=(
      --timeout 30s
      --contimeout 15s
      --retries 2
      --low-level-retries 2
    )
  fi

  log "INFO" "Mount $source -> $mount_dir"
  rclone "${args[@]}"
  sleep 1

  if is_mounted "$mount_dir"; then
    log "INFO" "Mounted: $mount_dir"
  else
    die "Mount da chay nhung Linux chua thay mountpoint: $mount_dir"
  fi
}

unmount_remote() {
  local mount_dir="${1:-$DEFAULT_MOUNT}"
  if ! is_mounted "$mount_dir"; then
    log "INFO" "Chua mount: $mount_dir"
    return 0
  fi

  log "INFO" "Unmount $mount_dir"
  if have fusermount3; then
    fusermount3 -u "$mount_dir" || fusermount3 -uz "$mount_dir"
  elif have fusermount; then
    fusermount -u "$mount_dir" || fusermount -uz "$mount_dir"
  else
    umount "$mount_dir" || sudo_cmd umount "$mount_dir"
  fi
}

ensure_git_project() {
  local project_dir="$1"
  ensure_git
  [ -d "$project_dir" ] || die "Thu muc project khong ton tai: $project_dir"

  if [ -e "$project_dir/.git" ]; then
    log "INFO" "Project da co Git: $project_dir"
    return 0
  fi

  log "INFO" "Khoi tao Git cho OpenCode: $project_dir"
  git -C "$project_dir" init -b main
  git -C "$project_dir" config user.name "RcloneDrive"
  git -C "$project_dir" config user.email "rclonedrive@local"
  git -C "$project_dir" commit --allow-empty -m "Initialize repository"
}

open_opencode() {
  local mount_dir="${1:-$DEFAULT_MOUNT}"
  local project_subpath="${2:-$DEFAULT_PROJECT}"
  project_subpath="${project_subpath#/}"
  project_subpath="${project_subpath%/}"

  [ -n "$project_subpath" ] || die "Khong mo truc tiep mount root. Hay nhap thu muc project, vi du: public_html"

  local project_dir="$mount_dir/$project_subpath"
  project_dir="$(cd "$project_dir" 2>/dev/null && pwd -P)" || die "Thu muc project khong ton tai: $mount_dir/$project_subpath"

  ensure_git_project "$project_dir"

  local encoded
  encoded="$(PROJECT_DIR="$project_dir" python3 - <<'PY' 2>/dev/null || true
import os
from urllib.parse import quote
print(quote(os.environ["PROJECT_DIR"]))
PY
)"
  [ -n "$encoded" ] || encoded="$project_dir"

  log "INFO" "OpenCode project: $project_dir"
  if have opencode; then
    opencode "$project_dir" >/dev/null 2>&1 &
  elif have xdg-open; then
    xdg-open "opencode://new-session?directory=$encoded" >/dev/null 2>&1 &
  else
    log "WARN" "Khong thay opencode hoac xdg-open. Hay mo thu cong: $project_dir"
  fi
}

run_webui() {
  ensure_rclone
  log "INFO" "Chay rclone Web UI tai http://127.0.0.1:5572"
  rclone rcd --rc-web-gui --rc-addr 127.0.0.1:5572 --rc-user admin --rc-pass admin
}

case "${1:-}" in
  install)
    install_all
    ;;
  list)
    ensure_rclone
    rclone listremotes
    ;;
  mount)
    mount_remote "${2:-}" "${3:-$DEFAULT_MOUNT}"
    ;;
  unmount|umount)
    unmount_remote "${2:-$DEFAULT_MOUNT}"
    ;;
  opencode)
    open_opencode "${2:-$DEFAULT_MOUNT}" "${3:-$DEFAULT_PROJECT}"
    ;;
  webui)
    run_webui
    ;;
  help|-h|--help|"")
    usage
    ;;
  *)
    usage
    die "Lenh khong hop le: $1"
    ;;
esac
