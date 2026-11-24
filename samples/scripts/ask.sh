#!/usr/bin/env bash
# Asks an agent from a shell script and uses the answer: exit code 0 = success, the answer on stdout.
#
#   samples/scripts/ask.sh dolly "Hány Python fájl van a workspace-ben?"
#   git diff | samples/scripts/ask.sh dolly -        # the question from stdin
#
# Needs jq; RACKA points to the racka executable when it is not on PATH.
# A shared conversation key keeps the follow-up questions in context.
set -euo pipefail

agent="${1:?usage: ask.sh <agent> <question>|-}"
shift
conversation="${ASK_CONVERSATION:-script:$(basename "$PWD")}"

reply="$("${RACKA:-racka}" send "$agent" "$@" --wait --json --timeout "${ASK_TIMEOUT:-600}" --from "script:$USER" -c "$conversation")" || true

if [[ -z "$reply" ]]; then
  echo "no reply from $agent" >&2
  exit 1
fi
if [[ "$(jq -r '.success' <<<"$reply")" == "true" ]]; then
  jq -r '.output' <<<"$reply"
else
  echo "error: $(jq -r '.error' <<<"$reply")" >&2
  exit 1
fi
