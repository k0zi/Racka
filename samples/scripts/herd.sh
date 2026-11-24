#!/usr/bin/env bash
# Starts, stops and shows a herd of Racka agents in the background.
#
#   samples/scripts/herd.sh start [agent…]   # default: every directory in agents/
#   samples/scripts/herd.sh status
#   samples/scripts/herd.sh logs <agent>     # follow one agent's log
#   samples/scripts/herd.sh stop [agent…]
#
# RACKA   the racka executable (default: racka on PATH, else the Debug build in src/)
# HERD_DIR where pid and log files go (default: .herd in the repository root)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
herd_dir="${HERD_DIR:-$root/.herd}"
mkdir -p "$herd_dir"

if [[ -n "${RACKA:-}" ]]; then
  racka="$RACKA"
elif command -v racka >/dev/null; then
  racka="$(command -v racka)"
else
  racka="$root/src/Racka.Host/bin/Debug/net10.0/racka"
  [[ -x "$racka" ]] || { echo "racka not found: build it with 'dotnet build src/Racka.slnx' or set RACKA" >&2; exit 1; }
fi

agents() {
  if (($# > 0)); then printf '%s\n' "$@"; else ls "$root/agents"; fi
}

running() {
  [[ -f "$herd_dir/$1.pid" ]] && kill -0 "$(cat "$herd_dir/$1.pid")" 2>/dev/null
}

case "${1:-}" in
  start)
    shift
    for name in $(agents "$@"); do
      if running "$name"; then
        echo "$name already running (pid $(cat "$herd_dir/$name.pid"))"
        continue
      fi
      nohup "$racka" run "$root/agents/$name" >"$herd_dir/$name.log" 2>&1 &
      echo $! >"$herd_dir/$name.pid"
      echo "started $name (pid $!, log $herd_dir/$name.log)"
    done
    sleep 2
    "$racka" list
    ;;
  stop)
    shift
    for name in $(agents "$@"); do
      if running "$name"; then
        kill "$(cat "$herd_dir/$name.pid")" && echo "stopped $name"
      fi
      rm -f "$herd_dir/$name.pid"
    done
    ;;
  status)
    "$racka" list
    ;;
  logs)
    tail -f "$herd_dir/${2:?agent name}.log"
    ;;
  *)
    sed -n '2,10p' "$0"
    exit 1
    ;;
esac
