#!/usr/bin/env bash
# Fails when test code sleeps, polls to a deadline or reads the real clock.
# Rule: docs/CONVENTIONS.md #11. Allow-list: eng/test-time-allowlist.txt
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

allowlist="${TEST_TIME_ALLOWLIST:-eng/test-time-allowlist.txt}"
patterns='Task\.Delay\(|Thread\.Sleep\(|DateTime\.Now|DateTime\.UtcNow|DateTimeOffset\.UtcNow|Stopwatch|WaitUntil'

# Scan the test projects and the TestKit (TEST_TIME_DIRS overrides, for trying the check out).
if [[ -n "${TEST_TIME_DIRS:-}" ]]; then
  read -r -a dirs <<< "$TEST_TIME_DIRS"
else
  dirs=()
  for d in RevalQuery.*.Tests RevalQuery.TestKit; do [[ -d "$d" ]] && dirs+=("$d"); done
fi

# One "file<TAB>member<TAB>line<TAB>text" per hit; member is the nearest declaration above it.
hits="$(
  find "${dirs[@]}" \( -name obj -o -name bin \) -prune -o \( -name '*.cs' -o -name '*.razor' \) -print | sort |
  while IFS= read -r file; do
    PAT="$patterns" awk -v file="$file" '
      /^[ \t]+(public|private|internal|protected)[ \t]/ && !/[ \t](class|record|struct|interface|enum)[ \t]/ {
        s = $0; sub(/[(=;{].*$/, "", s); sub(/[ \t]+$/, "", s); sub(/<[^>]*>[ \t]*$/, "", s); n = split(s, w, /[ \t]+/); member = w[n]
      }
      { t = $0; sub(/^[ \t]+/, "", t) }
      t ~ /^\/\// { next }
      $0 ~ ENVIRON["PAT"] { printf "%s\t%s\t%d\t%s\n", file, (member == "" ? "(file)" : member), NR, t }
    ' "$file"
  done
)"

allowed="$(sed -e 's/[ \t]*#.*$//' -e '/^[ \t]*$/d' "$allowlist" | awk '{print $1 "\t" $2}')"

status=0

if [[ -n "$hits" ]]; then
  unlisted="$(awk -F'\t' 'NR==FNR { ok[$1 "\t" $2] = 1; next } !(($1 "\t" $2) in ok) { printf "  %s:%s  in %s: %s\n", $1, $3, $2, $4 }' \
    <(printf '%s\n' "$allowed") <(printf '%s\n' "$hits"))"
  if [[ -n "$unlisted" ]]; then
    echo "Real time in test code. Wait on a library signal (Signals.UntilAsync, a gate) or a fake clock instead:"
    echo "$unlisted"
    status=1
  fi
fi

# An allow-list entry that no longer matches must be deleted, so the list only ever shrinks.
stale="$(awk -F'\t' 'NR==FNR { seen[$1 "\t" $2] = 1; next } NF && !(($1 "\t" $2) in seen) { printf "  %s %s\n", $1, $2 }' \
  <(printf '%s\n' "$hits") <(printf '%s\n' "$allowed"))"
if [[ -n "$stale" ]]; then
  echo "Stale allow-list entries (no longer use real time; delete them from $allowlist):"
  echo "$stale"
  status=1
fi

[[ $status -eq 0 ]] && echo "No real time in test code outside the allow-list."
exit $status
